# verify-install.ps1 — T10: 安装包真机三态回归（静默装 → 首跑落 LocalAppData → 覆盖装保数据 → 静默卸载保数据/保学习目录）
# v1.0.9：交互卸载默认保留；静默卸载无法询问，同样默认保留用户数据。
# 前提：installer/build-release.ps1 已产出 release\FocusDesktop-Setup-<version>.exe
# Setup 路径动态推导（v1.0.2 审计修复：此前写死 1.0.0，版本升级后跑的是磁盘残留旧包 = 假绿）：
#   优先取仓库 release\ 下最新的 FocusDesktop-Setup-*.exe（时间戳最新），也可用 -Setup 参数显式指定。
param([string]$Setup = "")
$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)   # tests\ 的上一级 = 仓库根
if (-not $Setup) {
    $candidates = Get-ChildItem (Join-Path $repo "release") -Filter "FocusDesktop-Setup-*.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    if ($candidates) { $Setup = $candidates[0].FullName }
}
$userInstallDir = "$env:LOCALAPPDATA\Programs\FocusDesk"   # lowest 权限默认装这
$localData = "$env:LOCALAPPDATA\focus-desktop"
$desktop = [Environment]::GetFolderPath("Desktop")
$script:pass = 0; $script:fail = 0
function Log($m) { Write-Output $m }
function Check($name, $cond) {
    if ($cond) { $script:pass++; Log "PASS $name" } else { $script:fail++; Log "FAIL $name" }
}

if (-not $Setup -or -not (Test-Path $Setup)) { Log "FAIL setup exe missing: $Setup"; exit 1 }
Log ".. 被测安装包：$Setup（$(Get-Item $Setup).LastWriteTime）"

function Get-UninstallString {
    $keys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7B3F9E2A-6C1D-4E8B-9F5A-F2D8C4A1E6B0}_is1",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7B3F9E2A-6C1D-4E8B-9F5A-F2D8C4A1E6B0}_is1"
    )
    foreach ($k in $keys) {
        if (Test-Path $k) { return (Get-ItemProperty $k).UninstallString }
    }
    return $null
}

# ============ 任务栏安全网（2026-09-02 事故）：脚本中途被杀也绝不丢任务栏 ============
# 之前无 try/finally：脚本在「2. 首跑」（应用已锁定、任务栏已隐藏）被中断时 finally 永不执行 →
# 任务栏真实丢失（用户底线事故）。三重防护：
#   1) try/finally 清场（对齐 verify-step12 的成熟结构）
#   2) 每段结束立刻杀应用 + 校验任务栏可见，不等最后
#   3) finally 里强制 TaskbarShow（Win32 ShowWindow），失败再重启 explorer
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class TBGuard {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  public static bool ShowTaskbar() {
    var h = FindWindow("Shell_TrayWnd", null);
    if (h == IntPtr.Zero) return false;
    ShowWindow(h, 5);
    return IsWindowVisible(h);
  }
}
'@
function Assert-Taskbar {
    if (-not [TBGuard]::ShowTaskbar()) {
        Log "WARN taskbar show failed — restarting explorer"
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
        Start-Sleep -Seconds 2
    }
}
function Kill-App {
    Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

try {

# ============ 清场（先卸旧的，没有就跳过） ============
$us = Get-UninstallString
if ($us) {
    Log ".. 清场：卸载已有安装"
    Start-Process cmd -ArgumentList "/c", "`"$us`" /VERYSILENT /SUPPRESSMSGBOXES" -Wait -WindowStyle Hidden
    Start-Sleep -Seconds 3
}
# v1.0.9 安全语义：卸载器默认保留数据；测试开始前显式清理自己的 fixture。
Remove-Item "$localData\config.json" -ErrorAction SilentlyContinue
Remove-Item "$localData\setup_done.flag" -ErrorAction SilentlyContinue
Check "清场：测试配置已移除" (-not (Test-Path "$localData\config.json"))

# ============ 1. 静默安装 ============
Log "== 1. 静默安装 =="
$p = Start-Process $Setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER" -PassThru -Wait
Check "安装进程退出码 0" ($p.ExitCode -eq 0)
Check "exe 落地用户目录" (Test-Path "$userInstallDir\focus-desktop.exe")
Check "安装目录无 portable.flag（走 LocalAppData 布局）" (-not (Test-Path "$userInstallDir\portable.flag"))
Check "桌面快捷方式" (Test-Path "$desktop\Focus Desk.lnk")
Check "注册表卸载项" ((Get-UninstallString) -ne $null)

# ============ 2. 首跑：数据落 LocalAppData + 进向导 ============
Log "== 2. 首跑 =="
Remove-Item "$localData\config.json" -ErrorAction SilentlyContinue   # 确保全新状态（防本机历史残留）
Remove-Item "$localData\setup_done.flag" -ErrorAction SilentlyContinue
$app = Start-Process "$userInstallDir\focus-desktop.exe" -WorkingDirectory $userInstallDir -PassThru
Start-Sleep -Seconds 10
Check "首跑生成 LocalAppData\focus-desktop" (Test-Path $localData)
Check "首跑写出 config.json（configured:false 走向导）" (Test-Path "$localData\config.json")
$cfgText = ""
if (Test-Path "$localData\config.json") { $cfgText = Get-Content "$localData\config.json" -Raw }
Check "config 含 configured:false（向导待完成）" ($cfgText -match '"configured":\s*false')
Kill-App
Assert-Taskbar   # 段结束即校验（不等最后）——首跑轮 config 未 configured 时应用不锁定，但任何异常都当场恢复

# 手动模拟用户完成向导（写 configured:true 的 v2 config——向导 UI 交互已在 T6 verify-setup 32/32 验过）
$wizardDone = @'
{
  "schemaVersion": 2,
  "configured": true,
  "studyFolder":  "D:\\focus-desktop\\tests\\fixture-study",
  "exitPhrase":  "我发誓我确实有事需要离开这个环境，我要马上回来。",
  "focusQuote":  "你想成为怎样的人？",
  "sites": [
    { "Id": "bili" },
    { "Id": "chatgpt" }
  ]
}
'@
Set-Content -Path "$localData\config.json" -Value $wizardDone -Encoding UTF8
# 学习目录 fixture（卸载断言用：装/卸绝不碰它）
$fixtureDir = "D:\focus-desktop\tests\fixture-study"
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null
Set-Content -Path "$fixtureDir\keep-me.txt" -Value "uninstall must not touch me" -Encoding UTF8
Log ".. 模拟向导完成 + 学习目录 fixture 就绪"

# ============ 3. 覆盖装（升级）：数据保留 ============
Log "== 3. 覆盖装（升级）=="
$p = Start-Process $Setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER" -PassThru -Wait
Check "覆盖装退出码 0" ($p.ExitCode -eq 0)
Check "升级后 config 保留（configured:true 仍在）" ((Get-Content "$localData\config.json" -Raw) -match '"configured":\s*true')

# ============ 4. 静默卸载：默认保留数据 + 学习目录原样 ============
Log "== 4. 静默卸载 =="
$us = Get-UninstallString
Check "卸载入口存在" ($us -ne $null)
if ($us) {
    Start-Process cmd -ArgumentList "/c", "`"$us`" /VERYSILENT /SUPPRESSMSGBOXES" -Wait -WindowStyle Hidden
    Start-Sleep -Seconds 4
}
Check "卸载后 exe 移除" (-not (Test-Path "$userInstallDir\focus-desktop.exe"))
Check "静默卸载后 LocalAppData 数据默认保留" ((Get-Content "$localData\config.json" -Raw -ErrorAction SilentlyContinue) -match '"configured":\s*true')
Check "卸载后桌面快捷方式移除" (-not (Test-Path "$desktop\Focus Desk.lnk"))
Check "学习目录 fixture 原样（装/卸绝不碰）" (Test-Path "$fixtureDir\keep-me.txt")

}   # ============ try 结束 ============
finally {
    # 任务栏安全网（无条件执行——脚本中途被杀/异常都走这里）：杀残留进程 + 强制恢复任务栏
    Log ".. finally：清场（杀进程 + 恢复任务栏）"
    Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*focus-desktop-data*" -or $_.CommandLine -like "*LOCALAPPDATA*focus-desktop*" } | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
    # 静默卸载现在默认保留数据；只在确认仍是本脚本写入的 fixture 配置时清理测试数据。
    $testCfgPath = Join-Path $localData "config.json"
    if (Test-Path $testCfgPath) {
        try {
            $testCfg = Get-Content $testCfgPath -Raw | ConvertFrom-Json
            if ($testCfg.studyFolder -eq $fixtureDir) { Remove-Item $localData -Recurse -Force }
        } catch { Log "WARN 测试数据无法安全识别，已保留：$localData" }
    }
    if (Test-Path $fixtureDir) { Remove-Item $fixtureDir -Recurse -Force -ErrorAction SilentlyContinue }
    Assert-Taskbar
}

Log ("== RESULT: " + $script:pass + " pass / " + $script:fail + " fail ==")
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
