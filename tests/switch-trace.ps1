# switch-trace.ps1 — 高密度切换取证：强制窗口前置，切换瞬间每 50ms 采一帧
# 目的：判定切换瞬间屏幕上是 ①遮罩纯色(#23262C) ②黑/灰重合成面 ③瞬时内容
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ST1 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@
[ST1]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\trace"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Remove-Item "$outDir\*.png" -ErrorAction SilentlyContinue

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList "--preview" -PassThru
$root = [System.Windows.Automation.AutomationElement]::RootElement
function Get-Win {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($x in $all) { if ($x.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $x } }
    return $null
}
$win = $null
for ($i = 0; $i -lt 20; $i++) { $win = Get-Win; if ($win -ne $null) { break }; Start-Sleep -Seconds 1 }
if ($win -eq $null) { Write-Output "FAIL no window"; exit 1 }
Start-Sleep -Seconds 5
[ST1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$wr = $win.Current.BoundingRectangle
Write-Output ("window: L={0} T={1} W={2} H={3}" -f $wr.Left, $wr.Top, $wr.Width, $wr.Height)

function FindById($id) {
    $w = Get-Win
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
function ClickEl($el) {
    [ST1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    $r = $el.Current.BoundingRectangle
    [ST1]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 100
    [ST1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [ST1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
function Shot($name) {
    $b = New-Object System.Drawing.Bitmap([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $g.Dispose()
    $b.Save("$outDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

# Step 1: 打开 bili，等稳定
$el = FindById "tab_bili"; ClickEl $el; Start-Sleep -Seconds 3
Shot "pre_bili"
# Step 2: 切到 chatgpt，切换后 0/50/100/.../800ms 各一帧
$el2 = FindById "tab_chatgpt"
if ($el2 -eq $null) { Write-Output "FAIL no chatgpt btn"; exit 1 }
[ST1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
$r2 = $el2.Current.BoundingRectangle
[ST1]::SetCursorPos([int](($r2.Left + $r2.Right) / 2), [int](($r2.Top + $r2.Bottom) / 2)) | Out-Null
Start-Sleep -Milliseconds 100
$t0 = [DateTime]::Now
[ST1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
[ST1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
for ($f = 0; $f -lt 17; $f++) {
    Shot ("sw_{0:d2}" -f $f)
    $target = ($f + 1) * 50
    while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt $target) { Start-Sleep -Milliseconds 5 }
}
Write-Output "switch1 bili->chatgpt captured (17 frames @50ms)"
Start-Sleep -Seconds 2
# Step 3: 切回 bili，同样高密度
$el3 = FindById "tab_bili"
[ST1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
$r3 = $el3.Current.BoundingRectangle
[ST1]::SetCursorPos([int](($r3.Left + $r3.Right) / 2), [int](($r3.Top + $r3.Bottom) / 2)) | Out-Null
Start-Sleep -Milliseconds 100
$t1 = [DateTime]::Now
[ST1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
[ST1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
for ($f = 0; $f -lt 17; $f++) {
    Shot ("bk_{0:d2}" -f $f)
    $target = ($f + 1) * 50
    while ((([DateTime]::Now - $t1).TotalMilliseconds) -lt $target) { Start-Sleep -Milliseconds 5 }
}
Write-Output "switch2 chatgpt->bili captured"
Write-Output "TRACE_DONE"
