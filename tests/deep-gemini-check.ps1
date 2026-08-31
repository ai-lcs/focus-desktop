# deep-gemini-check.ps1 — 新架构下 deepseek/gemini 加载与切换取证（补齐最后两站证据）
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DG1 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[DG1]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\dgcheck"
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

function FindById($id) {
    $w = Get-Win
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
function ClickId($id) {
    $el = FindById $id
    if ($el -eq $null) { return $false }
    [DG1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    $r = $el.Current.BoundingRectangle
    [DG1]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [DG1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [DG1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    return $true
}
function Shot($name) {
    $b = New-Object System.Drawing.Bitmap([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $g.Dispose()
    $b.Save("$outDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

# 依次加载三站
foreach ($pair in @(@("tab_gemini","gemini"), @("tab_deepseek","deepseek"), @("tab_bili","bili"))) {
    ClickId $pair[0] | Out-Null
    Start-Sleep -Seconds 10
    Shot ("loaded_" + $pair[1])
    Write-Output ("loaded " + $pair[1])
}
# 乒乓切换（含 deepseek 回切）
$seq = @(@("tab_bili","bili"), @("tab_deepseek","deepseek"), @("tab_gemini","gemini"), @("tab_deepseek","deepseek"), @("tab_bili","bili"))
$s = 0
foreach ($sw in $seq) {
    $s++
    ClickId $sw[0] | Out-Null
    $t0 = [DateTime]::Now
    for ($f = 0; $f -lt 6; $f++) {
        Shot ("S{0}_{1}_f{2:d2}" -f $s, $sw[1], $f)
        $target = ($f + 1) * 100
        while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt $target) { Start-Sleep -Milliseconds 3 }
    }
    Start-Sleep -Seconds 2
}
Write-Output "DG_CHECK_DONE"

# 自清理
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$orphans = Get-CimInstance Win32_Process -Filter "name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*focus-desktop-data*' }
foreach ($o in $orphans) { Stop-Process -Id $o.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1
$left = @(Get-CimInstance Win32_Process -Filter "name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*focus-desktop-data*' }).Count
Write-Output ("CLEANUP: orphans left = {0}" -f $left)
