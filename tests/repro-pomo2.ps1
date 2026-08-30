# repro-pomo2.ps1 — 精确复现番茄钟运行态: 点45 -> 点番茄钟"开始"(第二个同名按钮) -> 5秒后截图
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF3 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiF3]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList "--preview" -PassThru
Start-Sleep -Seconds 7

$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i = 0; $i -lt 10; $i++) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($win -ne $null) { break }
    Start-Sleep -Seconds 1
}
if ($win -eq $null) { Write-Output "WIN NOT FOUND"; exit 1 }

function FindAllByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 点 45 模式按钮
$m45 = FindAllByName("45")
if ($m45.Count -gt 0) { $m45[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output "clicked 45" }
Start-Sleep -Milliseconds 600

# 取"开始"按钮中 T 坐标最大者（番茄钟在下方）→ 坐标点击（Invoke 模式对部分元素不可用）
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MClick {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
$starts = FindAllByName("开始")
Write-Output ("start buttons found: " + $starts.Count)
$best = $null
foreach ($s in $starts) {
    $pt = $s.GetClickablePoint()
    if ($best -eq $null -or $pt.Y -gt $best.Y) { $best = $pt }
}
if ($best -ne $null) {
    [MClick]::SetCursorPos([int]$best.X, [int]$best.Y) | Out-Null
    Start-Sleep -Milliseconds 200
    [MClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [MClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Write-Output ("clicked start at " + [int]$best.X + "," + [int]$best.Y)
}
Start-Sleep -Seconds 5

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen(0, 0, 0, 0, $b.Size)
$b.Save("D:\focus-desktop\tests\pomo-run2.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $b.Dispose()
Write-Output "shot saved"

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
