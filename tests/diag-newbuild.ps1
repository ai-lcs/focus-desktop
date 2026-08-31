# diag-newbuild.ps1 — 新构建(z-order方案)取证：窗口状态/Tab栏/点击后UIA Document/渲染进程数
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DN1 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@
[DN1]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\diag"
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
Start-Sleep -Seconds 6
$wr = $win.Current.BoundingRectangle
Write-Output ("WINDOW rect: L={0} T={1} W={2} H={3}" -f $wr.Left, $wr.Top, $wr.Width, $wr.Height)
Write-Output ("Screen: {0}x{1}" -f [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
[DN1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null

function FindById($id) {
    $w = Get-Win
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
function Shot($name) {
    $b = New-Object System.Drawing.Bitmap([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $g.Dispose()
    $b.Save("$outDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}
function Dump($tag) {
    Write-Output "--- $tag ---"
    $w = Get-Win
    if ($w -eq $null) { Write-Output "  NO WINDOW"; return }
    foreach ($tid in @("tab_bili","tab_chatgpt","tab_gemini","tab_deepseek")) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $tid)
        $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
        if ($el -eq $null) { Write-Output ("  {0}: ABSENT" -f $tid); continue }
        $r = $el.Current.BoundingRectangle
        Write-Output ("  {0}: rect={1},{2},{3}x{4} name='{5}'" -f $tid, $r.Left, $r.Top, $r.Width, $r.Height, $el.Current.Name)
    }
    $cd = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
    $docs = $w.FindAll([System.Windows.Automation.TreeScope]::Subtree, $cd)
    Write-Output ("  Documents: {0}" -f $docs.Count)
    foreach ($doc in $docs) {
        $r = $doc.Current.BoundingRectangle
        Write-Output ("    doc '{0}' rect={1},{2},{3}x{4}" -f $doc.Current.Name, $r.Left, $r.Top, $r.Width, $r.Height)
    }
    $wv = @(Get-Process "msedgewebview2" -ErrorAction SilentlyContinue)
    Write-Output ("  msedgewebview2 procs: {0}" -f $wv.Count)
}

Dump "S0 initial(home)"
Shot "d0_home"

# 点 bili
$el = FindById "tab_bili"
if ($el -ne $null) {
    $r = $el.Current.BoundingRectangle
    $cx = [int](($r.Left+$r.Right)/2); $cy = [int](($r.Top+$r.Bottom)/2)
    $hw = [DN1]::WindowFromPoint($cx, $cy)
    Write-Output ("bili btn center ({0},{1}) -> WindowFromPoint hwnd={2}" -f $cx, $cy, $hw)
    [DN1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    [DN1]::SetCursorPos($cx, $cy) | Out-Null
    Start-Sleep -Milliseconds 150
    [DN1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [DN1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
Start-Sleep -Seconds 5
Dump "S1 after bili click"
Shot "d1_bili"

# 点 chatgpt
$el2 = FindById "tab_chatgpt"
if ($el2 -ne $null) {
    $r = $el2.Current.BoundingRectangle
    [DN1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    [DN1]::SetCursorPos([int](($r.Left+$r.Right)/2), [int](($r.Top+$r.Bottom)/2)) | Out-Null
    Start-Sleep -Milliseconds 150
    [DN1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [DN1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
Start-Sleep -Seconds 5
Dump "S2 after chatgpt click"
Shot "d2_chatgpt"

Write-Output "DIAG_NEW_DONE"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
