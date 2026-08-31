# verify-v051.ps1 — v0.5.1 三场景像素验证
# A: 预热后首开 ChatGPT 应秒开无白闪；B: 文件页中转回 bili 无白闪；C: PDF 首开深灰过渡非白屏
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class V51 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[V51]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\v051"
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
function ClickId($id) {
    $el = FindById $id
    if ($el -eq $null) { Write-Output "MISS $id"; return $false }
    [V51]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    $r = $el.Current.BoundingRectangle
    [V51]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [V51]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [V51]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    return $true
}
function Shot($name) {
    $b = (Get-Win).Current.BoundingRectangle
    $w = [int]$b.Width; $h = [int]$b.Height
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$b.Left, [int]$b.Top, 0, 0, $bmp.Size)
    $bmp.Save("$outDir\$name.png"); $g.Dispose(); $bmp.Dispose()
}

Write-Output "waiting warmup (20s)..."
Start-Sleep -Seconds 20
Shot "00_warmed"

# A: 首开 ChatGPT
Write-Output "A: click tab_chatgpt"
ClickId "tab_chatgpt" | Out-Null
Start-Sleep -Milliseconds 100; Shot "A1_100ms"
Start-Sleep -Milliseconds 250; Shot "A2_350ms"
Start-Sleep -Milliseconds 400; Shot "A3_750ms"
Start-Sleep -Seconds 1; Shot "A4_1750ms"

# B: 文件页停 2.5s → 回 bili
Write-Output "B: files -> back bili"
ClickId "tab_files" | Out-Null
Start-Sleep -Milliseconds 600; Shot "B1_files"
Start-Sleep -Seconds 2
ClickId "tab_bili" | Out-Null
Start-Sleep -Milliseconds 100; Shot "B2_back_100ms"
Start-Sleep -Milliseconds 300; Shot "B3_back_400ms"
Start-Sleep -Seconds 1; Shot "B4_back_1400ms"

# C: PDF 首开（双击文件列表第一项）。先回文件页找文件项
ClickId "tab_files" | Out-Null
Start-Sleep -Milliseconds 800
# 文件项没有 AutomationId，用 ListBox 第一个可点击子元素；退化方案：直接点击文件列表中央
$fl = FindById "FileList"
if ($fl -ne $null) {
    $items = $fl.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Output ("file items: " + $items.Count)
    if ($items.Count -gt 0) {
        $r = $items[0].Current.BoundingRectangle
        [V51]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
        Start-Sleep -Milliseconds 120
        [V51]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [V51]::mouse_event(4,0,0,0,[UIntPtr]::Zero)  # double
        Start-Sleep -Milliseconds 60
        [V51]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [V51]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 150; Shot "C1_pdf_150ms"
        Start-Sleep -Milliseconds 350; Shot "C2_pdf_500ms"
        Start-Sleep -Seconds 1; Shot "C3_pdf_1500ms"
        Start-Sleep -Seconds 2; Shot "C4_pdf_3500ms"
    }
} else { Write-Output "MISS FileList" }

Write-Output "DONE"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
  Where-Object { $_.CommandLine -like '*focus-desktop-data*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Output "cleaned"
