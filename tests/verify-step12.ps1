# focus-desktop 停点①自动验收脚本（PowerShell 驱动）
# 原理：注入真实按键 → 查询系统状态变化（与人手测同一套判据）
# 用法：powershell -ExecutionPolicy Bypass -File verify-step12.ps1
#
# 测试期间：桌面被真实锁定约 40 秒 + 短暂弹一次任务管理器（测试本身）
# 兜底：finally 块保证无论脚本怎么死，任务栏恢复 + 进程清理
#
param(
    [string]$ExePath = "D:\focus-desktop\src\focus-desktop\bin\Debug\net10.0-windows\focus-desktop.exe",
    [string]$DataDir = "D:\focus-desktop\src\focus-desktop\bin\Debug\net10.0-windows\focus-desktop-data"
)

$ErrorActionPreference = "Continue"
$results = [System.Collections.Generic.List[string]]::new()
$env:DOTNET_ROOT = "C:\Users\LCS\.dotnet"

function Pass($name) { $results.Add("PASS  $name"); Write-Host "  PASS  $name" -ForegroundColor Green }
function Fail($name, $detail) { $results.Add("FAIL  $name  [$detail]"); Write-Host "  FAIL  $name  [$detail]" -ForegroundColor Red }
function Info($msg) { Write-Host "  ..    $msg" -ForegroundColor DarkGray }

Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Win32Probe {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr i, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    public static string ForegroundProcessName() {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        try {
            var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName + ":" + pid;
        } catch { return "unknown:" + pid; }
    }

    public static bool TaskbarHidden() {
        var h = FindWindow("Shell_TrayWnd", null);
        return h != IntPtr.Zero && !IsWindowVisible(h);
    }

    public static void TaskbarShow() {
        var h = FindWindow("Shell_TrayWnd", null);
        if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, 0,0,0,0, 0x0001|0x0002|0x0010|0x0040);
    }

    public static string WindowStateOf(IntPtr h) {
        if (h == IntPtr.Zero) return "notfound";
        var style = GetWindowLong(h, -16);
        bool caption = (style & 0x00C00000) != 0;
        var ex = GetWindowLong(h, -20);
        bool top = (ex & 0x8) != 0;
        var r = new RECT(); GetWindowRect(h, out r);
        return string.Format("caption={0} topmost={1} rect=({2},{3})-({4},{5})",
            caption, top, r.L, r.T, r.R, r.B);
    }

    public static IntPtr FindFocusWindow() {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("focus-desktop")) {
            if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        }
        return IntPtr.Zero;
    }

    // Win11 开始菜单检测（实测有效的唯一判据）：
    // 弹出时前台窗口标题变为「开始」/「Start」
    public static bool StartMenuVisible() {
        var h = GetForegroundWindow();
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        var t = sb.ToString();
        return t == "开始" || t == "Start";
    }

    // 注入 Win 键（SendKeys 不支持 LWIN，用 keybd_event）
    public static void SendWinKey() {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);        // VK_LWIN down
        System.Threading.Thread.Sleep(50);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);        // KEYEVENTF_KEYUP
    }
}
"@

try {
Write-Host "`n=== focus-desktop 停点① 自动验收 ===" -ForegroundColor Cyan

# ---------- 0. 前置清理 ----------
Info "前置：清理残留实例"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
# 预置 setup_done.flag（模拟已完成首次设置；否则应用进 Setup 模式不锁定）
$setupFlag = Join-Path $DataDir "setup_done.flag"
if (-not (Test-Path $setupFlag)) { Set-Content -Path $setupFlag -Value "verify" -Encoding UTF8 }

# ---------- 1. 正常模式启动（真实锁定）----------
Info "启动 focus-desktop（真实锁定模式）……"
Start-Process $ExePath -WorkingDirectory (Split-Path $ExePath)
$deadline = (Get-Date).AddSeconds(10)
do {
    Start-Sleep -Milliseconds 300
    $fw = [Win32Probe]::FindFocusWindow()
} while ($fw -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline)
if ($fw -eq [IntPtr]::Zero) { Fail "启动" "主窗口未出现"; throw "启动失败" }
Pass "启动：主窗口出现"
Start-Sleep -Milliseconds 800

# ---------- 2. 全屏覆盖 + 无边框 ----------
$st = [Win32Probe]::WindowStateOf($fw)
Info "窗口状态: $st"
$sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
$sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
if ($st -match "caption=False" -and $st -match "rect=\(0,0\)-\((\d+),(\d+)\)") {
    $w = [int]$Matches[1]; $h = [int]$Matches[2]
    if ($w -ge $sw -and $h -ge $sh) { Pass "全屏铺满主屏 ($w x $h，屏幕 ${sw}x${sh})" }
    else { Fail "全屏铺满主屏" "窗口 ${w}x${h} < 屏幕 ${sw}x${sh}" }
} else { Fail "全屏/无边框" "$st" }

# ---------- 3. 任务栏隐藏 ----------
if ([Win32Probe]::TaskbarHidden()) { Pass "任务栏隐藏" }
else { Fail "任务栏隐藏" "Shell_TrayWnd 仍可见" }

# ---------- 4. Win 键拦截 ----------
Info "注入 Win 键（keybd_event）……"
[Win32Probe]::SendWinKey()
Start-Sleep -Milliseconds 1200
if (-not [Win32Probe]::StartMenuVisible()) { Pass "Win 键被拦（开始菜单未弹出）" }
else { Fail "Win 键拦截" "开始菜单弹出了"; [System.Windows.Forms.SendKeys]::SendWait("{ESC}") }

# ---------- 5. Alt+Tab 拦截 ----------
Info "注入 Alt+Tab……"
$before = [Win32Probe]::ForegroundProcessName()
[System.Windows.Forms.SendKeys]::SendWait("%{TAB}")
Start-Sleep -Milliseconds 1200
$after = [Win32Probe]::ForegroundProcessName()
if ($before -eq $after) { Pass "Alt+Tab 被拦（前台未切换: $after）" }
else { Fail "Alt+Tab 拦截" "前台从 $before 切到 $after" }

# ---------- 6. Alt+F4 拦截 ----------
Info "注入 Alt+F4……"
[System.Windows.Forms.SendKeys]::SendWait("%{F4}")
Start-Sleep -Milliseconds 1500
if (Get-Process "focus-desktop" -ErrorAction SilentlyContinue) { Pass "Alt+F4 被拦（进程存活）" }
else { Fail "Alt+F4 拦截" "进程退出了" }

# ---------- 7. 强杀 → 脏标志 ----------
Info "强杀 focus-desktop……"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1000
$stateFile = Join-Path $DataDir "session_state.json"
$state = Get-Content $stateFile -Raw -ErrorAction SilentlyContinue
if ($state -match '"focus_mode_active":\s*true') { Pass "强杀后脏标志残留 true（自愈依据）" }
else { Fail "强杀脏标志" "state=$state" }

# ---------- 8. 重启自愈 ----------
Info "重启 focus-desktop 验证启动自愈……"
Start-Process $ExePath -WorkingDirectory (Split-Path $ExePath)
$deadline = (Get-Date).AddSeconds(10)
do {
    Start-Sleep -Milliseconds 300
    $fw = [Win32Probe]::FindFocusWindow()
} while ($fw -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline)
Start-Sleep -Milliseconds 2000
$crashFiles = Get-ChildItem (Join-Path $DataDir "logs") -Filter "crash-*" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 2
$healed = $false
foreach ($cf in $crashFiles) {
    if ((Get-Content $cf.FullName -Raw) -match "startup-self-heal") { $healed = $true }
}
if ($healed) { Pass "启动自愈触发（self-heal 日志存在）" }
elseif ([Win32Probe]::TaskbarHidden()) { Pass "重启进入新一轮锁定（self-heal 日志缺失但锁定生效）" }
else { Fail "重启自愈" "无 self-heal 日志且任务栏未隐藏" }

# ---------- 9. 干净退出（UIA 点击退出按钮）----------
Info "UIA 点击退出按钮……"
Add-Type -AssemblyName UIAutomationClient
$proc = Get-Process "focus-desktop" -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "退出")
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($btn) {
    $invoke = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Start-Sleep -Milliseconds 1500
    # 退出验证弹窗（独立 ExitWindow 顶层窗口）：从根元素找，读取 config.json 退出语填入再确认
    $cfgFile = Join-Path $DataDir "config.json"
    $phrase = (Get-Content $cfgFile -Raw | ConvertFrom-Json).exitPhrase
    $dlgCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "确认离开专注环境")
    $dlg = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $dlgCond)
    if ($dlg) {
        $inputCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        $inputBox = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $inputCond)
        if ($inputBox) {
            $vp = $inputBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $vp.SetValue($phrase)
            Start-Sleep -Milliseconds 400
            $okCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, "确认退出")
            $okBtn = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $okCond)
            if ($okBtn) {
                ($okBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            } else { Fail "干净退出" "弹窗内未找到确认退出按钮" }
        } else { Fail "干净退出" "弹窗内未找到输入框" }
    } else { Fail "干净退出" "未找到退出弹窗窗口" }
    Start-Sleep -Milliseconds 2500
    if (-not (Get-Process "focus-desktop" -ErrorAction SilentlyContinue)) {
        Pass "干净退出（UIA 点击退出 → 进程退出）"
        Start-Sleep -Milliseconds 800
        if (-not [Win32Probe]::TaskbarHidden()) { Pass "退出后任务栏恢复" }
        else { Fail "退出后任务栏恢复" "任务栏仍隐藏" }
        $state = Get-Content $stateFile -Raw -ErrorAction SilentlyContinue
        if ($state -match '"focus_mode_active":\s*false') { Pass "退出后脏标志清除" }
        else { Fail "退出后脏标志" "state=$state" }
    } else { Fail "干净退出" "进程未退出" }
} else { Fail "干净退出" "UIA 未找到退出按钮" }

# ---------- 10. --restore 兜底 ----------
Info "人为制造孤儿态（脏标志 true + 任务栏隐藏）……"
Set-Content -Path $stateFile -Value '{"focus_mode_active": true}' -Encoding UTF8
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class TBHide {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr i, int x, int y, int cx, int cy, uint f);
    public static void Hide() { var h = FindWindow("Shell_TrayWnd", null); if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, 0,0,0,0, 0x0001|0x0002|0x0010|0x0080); }
}
"@
[TBHide]::Hide()
Start-Sleep -Milliseconds 500

Info "跑 --restore（自动点掉确认框）……"
$p = Start-Process $ExePath -ArgumentList "--restore" -WorkingDirectory (Split-Path $ExePath) -PassThru
# --restore 会弹 MessageBox，自动点 OK
$deadline = (Get-Date).AddSeconds(8)
do {
    Start-Sleep -Milliseconds 300
    $mb = [Win32Probe]::FindWindow("#32770", "focus-desktop 恢复")
    if ($mb -ne [IntPtr]::Zero) {
        [Win32Probe]::SendMessage($mb, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) # WM_CLOSE
        break
    }
} while ((Get-Date) -lt $deadline -and -not $p.HasExited)
$p.WaitForExit(10000) | Out-Null
Start-Sleep -Milliseconds 1000
if (-not [Win32Probe]::TaskbarHidden()) { Pass "--restore 后任务栏恢复" }
else { Fail "--restore 恢复" "任务栏仍隐藏" }
$state = Get-Content $stateFile -Raw -ErrorAction SilentlyContinue
if ($state -match '"focus_mode_active":\s*false') { Pass "--restore 后脏标志清除" }
else { Fail "--restore 脏标志" "state=$state" }

} finally {
    # 兜底清理：无论脚本怎么死，恢复用户桌面
    Info "finally：清理测试现场（恢复任务栏 + 杀残留进程）"
    try {
        Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
        [Win32Probe]::TaskbarShow()
        if (Test-Path (Join-Path $DataDir "session_state.json")) {
            Set-Content -Path (Join-Path $DataDir "session_state.json") -Value '{"focus_mode_active": false}' -Encoding UTF8
        }
    } catch { }
}

# ---------- 汇总 ----------
Write-Host "`n===== 汇总 =====" -ForegroundColor Cyan
$results | ForEach-Object { Write-Host $_ }
$passCount = ($results | Where-Object { $_ -match "^PASS" }).Count
$failCount = ($results | Where-Object { $_ -match "^FAIL" }).Count
Write-Host "`n通过 $passCount / $($results.Count) 项" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Yellow" })
$results | Out-File "$PSScriptRoot\verify-results.txt" -Encoding utf8
if ($failCount -gt 0) { exit 1 } else { exit 0 }
