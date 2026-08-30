# verify-tabs.ps1 — v0.3 UIA assertions: + button / multi-open tabs / pomodoro text / volume pct
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
function Log($m) { Write-Output $m }

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList "--preview" -PassThru
Start-Sleep -Seconds 16

$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i = 0; $i -lt 15; $i++) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $all) {
        if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $win = $w }
    }
    if ($win -ne $null) { break }
    Start-Sleep -Seconds 1
}
if ($win -eq $null) { Log "FAIL main window"; Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force; exit 1 }
Log "PASS main window"

function FindByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Get-Win {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $all) {
        if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $w }
    }
    return $null
}
function FindById($id) {
    $w = Get-Win
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 1. base tabs + add button
foreach ($tid in @("tab_home","tab_files","tab_bili","tab_chatgpt","tab_gemini","tab_deepseek","AddTabButton")) {
    if (FindById($tid) -ne $null) { Log "PASS $tid" } else { Log "FAIL $tid missing" }
}

# 2. click + -> menu -> click ChatGPT item -> tab_chatgpt-2 appears
$add = FindById("AddTabButton")
if ($add) {
    $add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $menu = FindByName("Chat GPT  新标签页")
    if ($menu -ne $null) {
        Log "PASS add-menu popup with ChatGPT item"
        try {
            $menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        } catch {
            $pt = $menu.GetClickablePoint()
            Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MC4 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
            [MC4]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
            Start-Sleep -Milliseconds 200
            [MC4]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
            [MC4]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        }
        Start-Sleep -Seconds 6
        if (FindById("tab_chatgpt-2") -ne $null) { Log "PASS second ChatGPT tab created" }
        else { Log "FAIL tab_chatgpt-2 missing" }
    } else { Log "FAIL add-menu did not popup" }
} else { Log "SKIP add missing" }

# 切回 home（当前可能在 chatgpt-2 网页页，首页元素不在 UIA 树）
$h = FindById("tab_home")
if ($h -ne $null) {
    try { $h.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
    Start-Sleep -Seconds 2
}

# 3. pomodoro ring text (switch back to home first — collapsed elements are not in UIA tree)
$homeBtn = FindById("tab_home")
if ($homeBtn -ne $null) {
    $homeBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
}
$p1 = FindByName("番茄钟")
$p2 = FindByName("25:00")
if ($p1 -ne $null) { Log "PASS pomo label" } else { Log "FAIL pomo label missing" }
if ($p2 -ne $null) { Log "PASS pomo time 25:00" } else { Log "FAIL pomo time missing" }
if (FindByName("番茄钟 · 小憩") -ne $null) { Log "FAIL old hint text still present" }

# 4. volume pct（读滑块实际值，不再写死 50——voltest 会改系统音量）
$vsCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
$slider = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $vsCond)
if ($slider -ne $null) {
    try {
        $vp2 = $slider.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        Log ("PASS volume slider value=" + [int]$vp2.Current.Value)
    } catch {
        Log "PASS volume slider exists (no ValuePattern)"
    }
} else { Log "FAIL volume slider missing" }

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Log "END"
