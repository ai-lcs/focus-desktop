# diag-zorder.ps1 — 点击 Tab 后：哪个 WebView 在最顶（z 序生效验证）+ WebHost 可见性
$ErrorActionPreference = "Continue"
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
Start-Sleep -Seconds 20

function FindById($id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $w2 = $null
    foreach ($x in $all) { if ($x.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $w2 = $x } }
    if ($w2 -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 点 bili 后查 WebHost 内 Document 页面标题（UIA 只能看到最顶层的可访问内容）
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MC13 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
function ClickEl($el) {
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return "invoke" }
    catch {
        $pt = $el.GetClickablePoint()
        [MC13]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
        Start-Sleep -Milliseconds 120
        [MC13]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [MC13]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        return "coord"
    }
}

foreach ($tid in @("tab_bili","tab_chatgpt")) {
    $el = FindById($tid)
    $method = ClickEl $el
    Start-Sleep -Seconds 2
    # 找 pane 里 Document 元素名（顶层 WebView 的页面标题会暴露在最前）
    $docCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
    $w2 = $null
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    foreach ($x in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        if ($x.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $w2 = $x }
    }
    $docs = $w2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $docCond)
    $names = @()
    foreach ($doc in $docs) { $names += $doc.Current.Name }
    Write-Output ("$tid ($method) → 可见Document: " + ($names -join ' | '))
}

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
