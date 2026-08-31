# verify-switch2.ps1 — 切换流畅性实测 v2：点 Tab → 300ms/1s 后各截一帧（捕捉切换瞬间状态）
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF12 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiF12]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\switch"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Remove-Item "$outDir\*.png" -ErrorAction SilentlyContinue

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
if ($win -eq $null) { Write-Output "FAIL main window"; exit 1 }

function FindById($id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $w2 = $null
    foreach ($x in $all) { if ($x.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $w2 = $x } }
    if ($w2 -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MC12 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
function ClickEl($el) {
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch {
        $pt = $el.GetClickablePoint()
        [MC12]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
        Start-Sleep -Milliseconds 120
        [MC12]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [MC12]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
}
function Shot($name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $b.Save("$outDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
}

Write-Output "waiting warmup 20s..."
Start-Sleep -Seconds 20

# 2 轮 × 4 站：点完 300ms 快照（切换瞬间）+ 1.5s 快照（应完全就绪）
$tabs = @("tab_bili","tab_chatgpt","tab_gemini","tab_deepseek")
$n = 0
foreach ($round in 1..2) {
    foreach ($tid in $tabs) {
        $n++
        $el = FindById($tid)
        if ($el -eq $null) { Write-Output "MISS $tid"; continue }
        ClickEl $el
        Start-Sleep -Milliseconds 300
        Shot "s${n}_$(($tid -split '_')[1])_instant"
        Start-Sleep -Milliseconds 1200
        Shot "s${n}_$(($tid -split '_')[1])_settled"
        Write-Output "ok $tid r$round"
    }
}
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
