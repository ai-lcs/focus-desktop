
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF15 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiF15]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MC15 {
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
function CoordClick($el) {
    $r = $el.Current.BoundingRectangle
    $x = [int](($r.Left + $r.Right) / 2)
    $y = [int](($r.Top + $r.Bottom) / 2)
    [MC15]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 120
    [MC15]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [MC15]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
function Shot($n) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $b.Save("D:\focus-desktop\tests\switch\cc_$n.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
}
$n = 0
foreach ($round in 1..2) {
    foreach ($tid in @("tab_bili","tab_chatgpt","tab_gemini","tab_deepseek")) {
        $n++
        $el = FindById($tid)
        if ($el -ne $null) {
            CoordClick $el
            # 首轮（懒加载创建+网络加载）等 8s；第二轮（切换+Resume）等 2s
            if ($round -eq 1) { Start-Sleep -Seconds 8 } else { Start-Sleep -Seconds 2 }
            Shot ("{0:d2}_{1}" -f $n, ($tid -split "_")[1])
        }
    }
}
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
