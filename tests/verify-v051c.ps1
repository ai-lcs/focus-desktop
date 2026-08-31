# verify-v051c.ps1 — v0.5.1 像素验证（UIA坐标+物理点击+前台强制校验）
# A: 预热后首开 ChatGPT 秒开无白闪；B: 文件页中转回 bili 无白闪；C: 文件页首开 PDF 深灰过渡
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class V51C {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@
[V51C]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\v051c"
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
$wr = $win.Current.BoundingRectangle
Write-Output ("window: {0},{1} {2}x{3}" -f $wr.Left, $wr.Top, $wr.Width, $wr.Height)

function FindById($id) {
    $w = Get-Win; if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
function Force-Fg {
    for ($k = 0; $k -lt 4; $k++) {
        [V51C]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 200
        $h = [V51C]::GetForegroundWindow()
        $fgPid = 0
        [V51C]::GetWindowThreadProcessId($h, [ref]$fgPid) | Out-Null
        if ($fgPid -eq $p.Id) { return $true }
    }
    return $false
}
function ClickId($id) {
    $el = FindById $id
    if ($el -eq $null) { Write-Output "MISS $id"; return $false }
    if (-not (Force-Fg)) { Write-Output "FG-FAIL"; return $false }
    $r = $el.Current.BoundingRectangle
    [V51C]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [V51C]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    [V51C]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    return $true
}
function Shot($name) {
    if (-not (Force-Fg)) { Write-Output "SHOT-FG-FAIL $name" }
    $b = (Get-Win).Current.BoundingRectangle
    $w = [int]$b.Width; $h = [int]$b.Height
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$b.Left, [int]$b.Top, 0, 0, $bmp.Size)
    $bmp.Save("$outDir\$name.png"); $g.Dispose(); $bmp.Dispose()
}

Write-Output "waiting warmup (22s)..."
Start-Sleep -Seconds 22
Shot "00_warmed_home"

# ---- A: 首开 ChatGPT（预热已就绪 → 应秒开）
$r = ClickId "tab_chatgpt"
Write-Output "A click: $r"
Start-Sleep -Milliseconds 150; Shot "A1_150ms"
Start-Sleep -Milliseconds 250; Shot "A2_400ms"
Start-Sleep -Milliseconds 600; Shot "A3_1000ms"
Start-Sleep -Seconds 1; Shot "A4_2000ms"

# ---- B: 文件页停 2.5s → 回 bili
$r = ClickId "tab_files"
Write-Output "B files click: $r"
Start-Sleep -Milliseconds 700; Shot "B1_files"
Start-Sleep -Seconds 2
$r = ClickId "tab_bili"
Write-Output "B bili click: $r"
Start-Sleep -Milliseconds 150; Shot "B2_back_150ms"
Start-Sleep -Milliseconds 350; Shot "B3_back_500ms"
Start-Sleep -Seconds 1; Shot "B4_back_1500ms"

# ---- C: 文件页首开 PDF（UIA 拿第一项坐标，物理双击）
$r = ClickId "tab_files"
Write-Output "C files click: $r"
Start-Sleep -Milliseconds 800
$fl = FindById "FileList"
if ($fl -ne $null) {
    $items = $fl.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem }
    if ($items.Count -eq 0) {
        $items = $fl.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    }
    Write-Output ("file items: " + $items.Count)
    if ($items.Count -gt 0) {
        if (-not (Force-Fg)) { Write-Output "FG-FAIL C" }
        $r = $items[0].Current.BoundingRectangle
        $cx = [int](($r.Left + $r.Right) / 2); $cy = [int](($r.Top + $r.Bottom) / 2)
        Write-Output ("file item rect: {0},{1}" -f $cx, $cy)
        [V51C]::SetCursorPos($cx, $cy) | Out-Null
        Start-Sleep -Milliseconds 120
        [V51C]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [V51C]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [V51C]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [V51C]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 200; Shot "C1_pdf_200ms"
        Start-Sleep -Milliseconds 400; Shot "C2_pdf_600ms"
        Start-Sleep -Seconds 1; Shot "C3_pdf_1600ms"
        Start-Sleep -Seconds 2; Shot "C4_pdf_3600ms"
    }
} else { Write-Output "MISS FileList" }

Write-Output "DONE"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
  Where-Object { $_.CommandLine -like '*focus-desktop-data*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Output "cleaned"
