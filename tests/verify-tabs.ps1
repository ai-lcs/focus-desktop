# verify-tabs.ps1 — v0.3 UIA assertions: + button / multi-open tabs / pomodoro text / volume pct
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
function Log($m) { Write-Output $m }

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList "--preview" -PassThru
Start-Sleep -Seconds 7

$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i = 0; $i -lt 10; $i++) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($win -ne $null) { break }
    Start-Sleep -Seconds 1
}
if ($win -eq $null) { Log "FAIL main window"; Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force; exit 1 }
Log "PASS main window"

function FindByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function FindById($id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 1. base tabs + add button
foreach ($tid in @("tab_home","tab_files","tab_bili","tab_chatgpt","tab_gemini","tab_deepseek","tab_add")) {
    if (FindById($tid) -ne $null) { Log "PASS $tid" } else { Log "FAIL $tid missing" }
}

# 2. click + -> menu -> click ChatGPT item -> tab_chatgpt-2 appears
$add = FindById("tab_add")
if ($add) {
    $add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $menu = FindByName("ChatGPT  新标签页")
    if ($menu -ne $null) {
        Log "PASS add-menu popup with ChatGPT item"
        $menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 6
        if (FindById("tab_chatgpt-2") -ne $null) { Log "PASS second ChatGPT tab created" }
        else { Log "FAIL tab_chatgpt-2 missing" }
    } else { Log "FAIL add-menu did not popup" }
} else { Log "SKIP add missing" }

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

# 4. volume pct
$vp = FindByName("50")
if ($vp -ne $null) { Log "PASS volume pct 50" } else { Log "FAIL volume pct missing" }

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Log "END"
