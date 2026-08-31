# measure-lag.ps1 — 卡顿定量取证
# Phase A: 冷启动后依次点开四站 → 内容出现耗时（懒加载创建+网络代价）
# Phase B: 四站全开后，为每站拍"稳定参照帧"
# Phase C: 快速来回切换，在点击后 150/400/800/1500/2500ms 各拍帧（离线与参照帧比对得"稳定耗时"）
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ML1 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
[ML1]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\measure"
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
if ($win -eq $null) { Write-Output "FAIL no main window"; exit 1 }
Start-Sleep -Seconds 6
$wr = $win.Current.BoundingRectangle
Write-Output ("window rect: {0},{1} {2}x{3}" -f $wr.Left, $wr.Top, $wr.Width, $wr.Height)

function FindById($id) {
    $w = Get-Win
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
function ClickEl($el) {
    $r = $el.Current.BoundingRectangle
    [ML1]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [ML1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [ML1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
function Shot($name) {
    $b = New-Object System.Drawing.Bitmap([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $g.Dispose()
    $b.Save("$outDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}
# 全内容区灰度极差（17x17 采样）：纯色空态≈0，有内容页>40
function FrameSpread($path) {
    $im = New-Object System.Drawing.Bitmap($path)
    $w = $im.Width; $h = $im.Height
    $min = 255; $max = 0
    for ($i = 0; $i -lt 17; $i++) {
        for ($j = 0; $j -lt 17; $j++) {
            $x = [int]($w * (0.15 + 0.7 * $i / 16))
            $y = [int]($h * (0.12 + 0.76 * $j / 16))
            $px = $im.GetPixel($x, $y)
            $lum = [int](0.299 * $px.R + 0.587 * $px.G + 0.114 * $px.B)
            if ($lum -lt $min) { $min = $lum }
            if ($lum -gt $max) { $max = $lum }
        }
    }
    $im.Dispose()
    return ($max - $min)
}

# ---------- Phase A ----------
Write-Output "=== Phase A: cold first-open (click -> content visible) ==="
$n = 0
foreach ($tid in @("tab_bili", "tab_chatgpt", "tab_gemini", "tab_deepseek")) {
    $n++
    $site = ($tid -split "_")[1]
    $el = FindById $tid
    if ($el -eq $null) { Write-Output "A$n $site FAIL button-not-found"; continue }
    $t0 = [DateTime]::Now
    ClickEl $el
    $found = -1
    for ($f = 0; $f -lt 60; $f++) {
        Start-Sleep -Milliseconds 250
        $fn = "$outDir\A{0}_{1}_f{2:d2}.png" -f $n, $site, $f
        Shot ("A{0}_{1}_f{2:d2}" -f $n, $site, $f)
        if ((FrameSpread $fn) -gt 40) { $found = ([DateTime]::Now - $t0).TotalMilliseconds; break }
    }
    if ($found -ge 0) { Write-Output ("A{0} {1}: content in {2:N0} ms" -f $n, $site, $found) }
    else { Write-Output ("A{0} {1}: NO CONTENT in 15000 ms (last spread check failed)" -f $n, $site) }
    Start-Sleep -Milliseconds 800
}

# ---------- Phase B: settled refs ----------
Write-Output "=== Phase B: settled reference frames ==="
foreach ($tid in @("tab_bili", "tab_chatgpt", "tab_gemini", "tab_deepseek")) {
    $site = ($tid -split "_")[1]
    $el = FindById $tid
    if ($el -eq $null) { Write-Output "B $site FAIL button-not-found"; continue }
    ClickEl $el
    Start-Sleep -Seconds 4
    Shot ("ref_$site")
    Write-Output "ref_$site captured"
}

# ---------- Phase C: switch latency ----------
Write-Output "=== Phase C: ping-pong switch latency frames ==="
$seq = @(@("tab_bili","bili"), @("tab_chatgpt","chatgpt"), @("tab_gemini","gemini"), @("tab_deepseek","deepseek"), @("tab_bili","bili"), @("tab_chatgpt","chatgpt"))
$m = 0
foreach ($pair in $seq) {
    $m++
    $tid = $pair[0]; $site = $pair[1]
    $el = FindById $tid
    if ($el -eq $null) { Write-Output "C$m $site FAIL button-not-found"; continue }
    ClickEl $el
    $t0 = [DateTime]::Now
    Shot ("C{0:d2}_{1}_t0150" -f $m, $site)
    while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt 400) { Start-Sleep -Milliseconds 20 }
    Shot ("C{0:d2}_{1}_t0400" -f $m, $site)
    while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt 800) { Start-Sleep -Milliseconds 20 }
    Shot ("C{0:d2}_{1}_t0800" -f $m, $site)
    while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt 1500) { Start-Sleep -Milliseconds 20 }
    Shot ("C{0:d2}_{1}_t1500" -f $m, $site)
    while ((([DateTime]::Now - $t0).TotalMilliseconds) -lt 2500) { Start-Sleep -Milliseconds 20 }
    Shot ("C{0:d2}_{1}_t2500" -f $m, $site)
    Start-Sleep -Milliseconds 1500
}
Write-Output "MEASURE_DONE"
