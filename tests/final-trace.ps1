# final-trace.ps1 — 定稿取证：充分加载后乒乓切换，查白闪/稳定时间/首页往返
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FT1 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[FT1]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\final"
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

function FindById($id) {
    $w = Get-Win
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
function ClickId($id) {
    $el = FindById $id
    if ($el -eq $null) { Write-Output "MISS button $id"; return $false }
    [FT1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    $r = $el.Current.BoundingRectangle
    [FT1]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [FT1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [FT1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
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

# 充分加载两个站
Write-Output "loading bili..."
ClickId "tab_bili" | Out-Null; Start-Sleep -Seconds 8
Shot "loaded_bili"
Write-Output "loading chatgpt..."
ClickId "tab_chatgpt" | Out-Null; Start-Sleep -Seconds 8
Shot "loaded_chatgpt"

# 乒乓 4 次切换，每次：点击后立即每 50ms 采 10 帧 + 1000ms 收尾帧
$switches = @(@("tab_bili","bili"), @("tab_chatgpt","gpt"), @("tab_bili","bili"), @("tab_chatgpt","gpt"))
$s = 0
foreach ($sw in $switches) {
    $s++
    if (-not (ClickId $sw[0])) { continue }
    $t0 = [DateTime]::Now
    for ($f = 0; $f -lt 10; $f++) {
        Shot ("P{0}_{1}_f{2:d2}" -f $s, $sw[1], $f)
        $target = ($f + 1) * 50
        while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt $target) { Start-Sleep -Milliseconds 3 }
    }
    while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt 1000) { Start-Sleep -Milliseconds 20 }
    Shot ("P{0}_{1}_t1000" -f $s, $sw[1])
    Write-Output ("switch{0} -> {1} captured" -f $s, $sw[1])
    Start-Sleep -Seconds 2
}

# 首页往返：web -> home -> web（隐藏/重建路径）
Write-Output "home roundtrip..."
ClickId "tab_home" | Out-Null; Start-Sleep -Seconds 2
Shot "home_from_web"
if (-not (ClickId "tab_bili")) { Write-Output "FAIL bili btn after home" }
$t0 = [DateTime]::Now
for ($f = 0; $f -lt 10; $f++) {
    Shot ("H_bili_f{0:d2}" -f $f)
    $target = ($f + 1) * 100
    while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt $target) { Start-Sleep -Milliseconds 3 }
}
while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt 3000) { Start-Sleep -Milliseconds 20 }
Shot "H_bili_t3000"
Write-Output "FINAL_TRACE_DONE"

# ---- 自清理：杀应用 + 只杀孤儿 webview（CommandLine 含 focus-desktop-data），不误伤系统 SearchHost ----
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$orphans = Get-CimInstance Win32_Process -Filter "name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*focus-desktop-data*' }
foreach ($o in $orphans) { Stop-Process -Id $o.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1
$left = @(Get-CimInstance Win32_Process -Filter "name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*focus-desktop-data*' }).Count
Write-Output ("CLEANUP: focus-desktop orphans left = {0}" -f $left)
