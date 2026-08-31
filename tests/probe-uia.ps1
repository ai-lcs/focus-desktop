# probe-uia.ps1 — 切换后 UIA 状态取证：点击 Tab 后按钮高亮态 + Document 树
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PU1 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[PU1]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
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
Start-Sleep -Seconds 5
[PU1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null

function FindById($id) {
    $w = Get-Win
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
function ClickEl($el) {
    [PU1]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    $r = $el.Current.BoundingRectangle
    Write-Output ("  click at ({0},{1})" -f [int](($r.Left+$r.Right)/2), [int](($r.Top+$r.Bottom)/2))
    [PU1]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 100
    [PU1]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [PU1]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
function Dump-TabState($tag) {
    $w = Get-Win
    if ($w -eq $null) { Write-Output "$tag NO WINDOW"; return }
    $out = "$tag |"
    foreach ($tid in @("tab_home","tab_files","tab_bili","tab_chatgpt","tab_gemini","tab_deepseek")) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $tid)
        $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
        if ($el -eq $null) { $out += " $tid:ABSENT"; continue }
        # 高亮判据：按钮内文本颜色不可直接读 → 用 ItemStatus/HelpText 不行；读第一个 Text 子元素的 Foreground 不现实
        # 改用下划线元素存在性：激活态有 underline (Visibility)。读 BoundingRectangle 高度变化？
        # 最简单：读按钮的 Name + IsEnabled + HasKeyboardFocus
        $nm = $el.Current.Name
        $out += (" {0}[{1}]" -f $tid, $nm.Substring(0, [Math]::Min(14, $nm.Length)))
    }
    Write-Output $out
    # Document 树（WebView2 内容）
    $cd = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
    $docs = $w.FindAll([System.Windows.Automation.TreeScope]::Subtree, $cd)
    Write-Output ("  Documents: {0}" -f $docs.Count)
    foreach ($doc in $docs) {
        $r = $doc.Current.BoundingRectangle
        Write-Output ("    doc name='{0}' rect={1},{2},{3}x{4} offscreen={5}" -f $doc.Current.Name, $r.Left, $r.Top, $r.Width, $r.Height, $doc.Current.IsOffscreen)
    }
}

Write-Output "=== initial (home) ==="
Dump-TabState "S0"

Write-Output "=== click bili ==="
ClickEl (FindById "tab_bili")
Start-Sleep -Seconds 3
Dump-TabState "S1-bili"

Write-Output "=== click chatgpt ==="
ClickEl (FindById "tab_chatgpt")
Start-Sleep -Seconds 3
Dump-TabState "S2-chatgpt"

Write-Output "=== click bili again ==="
ClickEl (FindById "tab_bili")
Start-Sleep -Seconds 3
Dump-TabState "S3-bili2"

Write-Output "PROBE_DONE"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
