# verify-watchdog.ps1 — 看门狗核验：锁定中强杀主进程，任务栏必须在 5 秒内自动恢复
# 覆盖此前覆盖不到的场景：taskkill /f（无异常处理机会、无 OnExit、脏标志残留 true）
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TB {
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);
}
"@

function Log($m) { Write-Output ("{0} {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $m) }
function TaskbarVisible { $h = [TB]::FindWindow("Shell_TrayWnd", $null); return ($h -ne [IntPtr]::Zero) -and [TB]::IsWindowVisible($h) }

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$dataDir = "D:\focus-desktop\release\focus-desktop\focus-desktop-data"

# 前置：确保 setup 已完成（直接锁定模式）
if (-not (Test-Path "$dataDir\setup_done.flag")) {
    Set-Content -Path "$dataDir\setup_done.flag" -Value "verify" -Encoding UTF8
}

Log "=== 场景：锁定中 taskkill /f 主进程（watchdog 应在 ~4 秒内恢复任务栏） ==="
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 6   # 等锁定完成（Enter: 脏标志+watchdog+任务栏隐藏+钩子）

$tbHidden = -not (TaskbarVisible)
Log ("锁定后任务栏隐藏 = $tbHidden（应为 True）")

# watchdog 进程应在运行（同 exe 名，pid != 主进程）
$wd = Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $p.Id }
Log ("watchdog 进程存在 = " + [bool]$wd + "（应为 True）")

# 强杀主进程（不走任何退出逻辑）
taskkill /f /pid $p.Id | Out-Null
Log "已 taskkill /f 主进程 pid=$($p.Id)，等待 watchdog 反应..."

$recovered = $false
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Milliseconds 1000
    if (TaskbarVisible) { $recovered = $true; Log ("任务栏已恢复（第 $($i+1) 秒）"); break }
}

$flagAfter = Get-Content "$dataDir\session_state.json" -Raw
Log ("恢复后脏标志 = $flagAfter（应为 false）")

$wdLog = Get-ChildItem "$dataDir\logs\crash-*.log" | Sort-Object LastWriteTime | Select-Object -Last 1
$wdHit = $false
if ($wdLog) { $wdHit = (Get-Content $wdLog.FullName -Raw).Contains("watchdog-recovered") }
Log ("watchdog-recovered 日志存在 = $wdHit（应为 True）")

if (-not $recovered) { Log "FAIL 任务栏未恢复"; & $exe --restore | Out-Null }

Log ("=== 结果: recovered=$recovered flagClean=$($flagAfter.Contains('false')) watchdogLog=$wdHit ===")

# 清理残留 watchdog
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force -ErrorAction SilentlyContinue
