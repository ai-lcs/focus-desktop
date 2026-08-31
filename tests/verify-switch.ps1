# verify-switch.ps1 — 切换流畅性实测：等预热完成 → 依次连点 4 站 Tab ×2 轮 → 全程录屏
# 判据（帧分析用）：任何白屏/黑屏/半渲染帧 = FAIL
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF10 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiF10]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

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
if ($win -eq $null) { Write-Output "FAIL main window"; exit 1 }
Write-Output "main window ok"

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
public static class MC10 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
function ClickEl($el) {
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch {
        $pt = $el.GetClickablePoint()
        [MC10]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
        Start-Sleep -Milliseconds 150
        [MC10]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [MC10]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
}

# 等预热完成（1.5s + 4 站 × 2.5s + 余量）
Write-Output "waiting warmup 20s..."
Start-Sleep -Seconds 20

# 录屏器：后台线程连续抓帧（fps=5）
$script:recording = $true
$frames = [System.Collections.ArrayList]::new()
$recJob = Start-Job -ScriptBlock {
    param($pid2)
    Add-Type -AssemblyName System.Drawing
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF11 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
    [DpiF11]::SetProcessDPIAware() | Out-Null
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Add-Type -AssemblyName System.Windows.Forms
    $n = 0
    while ($true) {
        $b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($b)
        $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
        $b.Save("D:\focus-desktop\tests\switch\f_$('{0:d3}' -f $n).png", [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $b.Dispose()
        $n++
        Start-Sleep -Milliseconds 200
    }
} -ArgumentList $p.Id

Start-Sleep -Seconds 1
New-Item -ItemType Directory -Force -Path "D:\focus-desktop\tests\switch" | Out-Null
Remove-Item "D:\focus-desktop\tests\switch\*.png" -ErrorAction SilentlyContinue

# 连点 2 轮 × 4 站（真实用户节奏：每 1.2 秒一个）
$tabs = @("tab_bili","tab_chatgpt","tab_gemini","tab_deepseek")
foreach ($round in 1..2) {
    foreach ($tid in $tabs) {
        $el = FindById($tid)
        if ($el -ne $null) { ClickEl $el; Write-Output "clicked $tid (round $round)" }
        else { Write-Output "MISS $tid" }
        Start-Sleep -Milliseconds 1200
    }
}
Stop-Job $recJob -ErrorAction SilentlyContinue
Remove-Job $recJob -Force -ErrorAction SilentlyContinue
Write-Output "frames captured"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
