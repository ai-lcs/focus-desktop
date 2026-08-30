# repro-freeze.ps1 — 复现今早卡死：真实模式 + 首次运行状态，UIA 驱动点击，逐部检测窗口挂起
# 输出全部写 stdout（重定向到文件），最后 finally 清理进程
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Log($m) { Write-Output ("{0} {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $m) }

# Win32 挂起检测：SMTO_ABORTIFHUNG 2 秒
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HangCheck {
    [DllImport("user32.dll")]
    public static extern bool SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr result);
    public static bool IsResponsive(IntPtr h) {
        IntPtr r;
        // WM_NULL = 0x0000
        return SendMessageTimeout(h, 0x0000, IntPtr.Zero, IntPtr.Zero, 0x0002 /*SMTO_ABORTIFHUNG*/, 2000, out r);
    }
}
"@

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
Log "启动 focus-desktop（真实模式，首次运行状态）"
$p = Start-Process -FilePath $exe -WorkingDirectory "D:\focus-desktop\release\focus-desktop" -PassThru
Start-Sleep -Seconds 6

try {
    # 找主窗口
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Log "FATAL 主窗口未找到"; throw "no window" }
    $hwnd = [IntPtr]::new($win.Current.NativeWindowHandle)
    Log ("窗口 hwnd=$hwnd responsive=" + [HangCheck]::IsResponsive($hwnd))

    function ClickByName([string]$name) {
        $c = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($btn) {
            ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Log ("已点击 [$name]")
            return $true
        }
        Log ("未找到按钮 [$name]")
        return $false
    }

    # 1) 学习文件页
    ClickByName "学习文件" | Out-Null
    Start-Sleep -Milliseconds 1500
    Log ("点击学习文件后 responsive=" + [HangCheck]::IsResponsive($hwnd))

    # 2) 搜索框打字（模拟用户试图"指定文件夹"）
    $tbCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, "TextBox")
    $tb = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tbCond)
    if ($tb) {
        ($tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue("D:\杂文件\focus")
        Log "搜索框输入了路径"
        Start-Sleep -Milliseconds 1500
        Log ("输入后 responsive=" + [HangCheck]::IsResponsive($hwnd))
    }

    # 3) 回 Home，点 Bilibili 标签
    ClickByName "Bilibili" | Out-Null
    Start-Sleep -Seconds 5
    Log ("点击Bilibili后 responsive=" + [HangCheck]::IsResponsive($hwnd))

    # 4) Bilibili 页面上点退出
    ClickByName "退出" | Out-Null
    Start-Sleep -Milliseconds 2000
    Log ("Bilibili页点退出后 responsive=" + [HangCheck]::IsResponsive($hwnd))
    # 检查退出弹窗是否存在（UIA 能找到=逻辑在，但可能被 WebView2 空域盖住）
    $dlgCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "确认离开专注环境")
    $dlg = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dlgCond)
    Log ("退出弹窗UIA存在=" + [bool]$dlg)
    if ($dlg) {
        $r = $dlg.Current.BoundingRectangle
        Log ("弹窗矩形: L=$($r.Left) T=$($r.Top) W=$($r.Width) H=$($r.Height)")
    }

    # 5) 截图取证
    $ss = "D:\focus-desktop\tests\repro-screen.png"
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $b.Save($ss, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
    Log "截图: $ss"

    # 6) 尝试 Alt+F4 路径（结束前）
    Log ("最终 responsive=" + [HangCheck]::IsResponsive($hwnd))
}
finally {
    Log "清理：杀进程"
    Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    # 若锁了任务栏，恢复
    & "$exe" --restore | Out-Null
}
Log "END"
