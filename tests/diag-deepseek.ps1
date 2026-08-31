# diag-deepseek.ps1 — 手动启动→点 deepseek→10s/25s 两帧截图（判定页面是否渲染）
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF20 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiF20]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MC20 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList "--preview" -PassThru
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i = 0; $i -lt 15; $i++) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $all) { if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $win = $w } }
    if ($win -ne $null) { break }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 5

function FindById($id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $w2 = $null
    foreach ($x in $all) { if ($x.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $w2 = $x } }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Shot($n) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $b.Save("D:\focus-desktop\tests\switch\ds_$n.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
}

$el = FindById("tab_deepseek")
if ($el -ne $null) {
    $r = $el.Current.BoundingRectangle
    [MC20]::SetCursorPos([int](($r.Left+$r.Right)/2), [int](($r.Top+$r.Bottom)/2)) | Out-Null
    Start-Sleep -Milliseconds 150
    [MC20]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    [MC20]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Write-Output "clicked deepseek"
    Start-Sleep -Seconds 10
    Shot "t10"
    Start-Sleep -Seconds 15
    Shot "t25"
} else { Write-Output "tab not found" }
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
