# verify-v051b.ps1 — v0.5.1 完整像素验证（UIA Invoke 点击，不抢鼠标）
# A: 预热后首开 ChatGPT 秒开无白闪；B: 文件页中转回 bili 无白闪；C: 文件页首开 PDF 深灰过渡
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class V51B {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[V51B]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$outDir = "D:\focus-desktop\tests\v051b"
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
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    catch { Write-Output "INVOKE-FAIL $id"; return $false }
}
function Shot($name) {
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
ClickId "tab_chatgpt" | Out-Null
Start-Sleep -Milliseconds 150; Shot "A1_150ms"
Start-Sleep -Milliseconds 250; Shot "A2_400ms"
Start-Sleep -Milliseconds 600; Shot "A3_1000ms"
Start-Sleep -Seconds 1; Shot "A4_2000ms"

# ---- B: 文件页停 2.5s → 回 bili
ClickId "tab_files" | Out-Null
Start-Sleep -Milliseconds 700; Shot "B1_files"
Start-Sleep -Seconds 2
ClickId "tab_bili" | Out-Null
Start-Sleep -Milliseconds 150; Shot "B2_back_150ms"
Start-Sleep -Milliseconds 350; Shot "B3_back_500ms"
Start-Sleep -Seconds 1; Shot "B4_back_1500ms"

# ---- C: 文件页首开 PDF（点击 FileList 第一项）
ClickId "tab_files" | Out-Null
Start-Sleep -Milliseconds 800
$fl = FindById "FileList"
if ($fl -ne $null) {
    $items = $fl.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Output ("file items: " + $items.Count)
    if ($items.Count -gt 0) {
        try { $items[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        catch { Write-Output "file invoke failed: $_" }
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
