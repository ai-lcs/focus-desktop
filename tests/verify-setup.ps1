# verify-setup.ps1 — T6: Public v1 Setup Wizard 回归（UIA 驱动全流程 + 原子性 + 幂等 + legacy 不弹向导）
# 运行前提：release exe 已构建。测试全程操作临时 DataDir 副本，结束恢复 Kevin 真实 config。
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName UIAutomationClient
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SetupTaskbarGuard {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public static bool ShowTaskbar() {
        var h = FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero) return false;
        ShowWindow(h, 5);
        return IsWindowVisible(h);
    }
}
"@

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $repo "release\focus-desktop\focus-desktop.exe"
$dataDir = Join-Path $repo "release\focus-desktop\focus-desktop-data"
$cfg = Join-Path $dataDir "config.json"
$setupFlag = Join-Path $dataDir "setup_done.flag"
$backupCfg = "$env:LOCALAPPDATA\Temp\fd-config-backup.json"
$backupFlag = "$env:LOCALAPPDATA\Temp\fd-setupflag-backup"
$watchdogAlias = Join-Path (Split-Path $exe) "focus-desktop-watchdog.exe"

$script:pass = 0; $script:fail = 0
$script:appPids = @{}
function Log($m) { Write-Output $m }
function Check($name, $cond) {
    if ($cond) { $script:pass++; Log "PASS $name" } else { $script:fail++; Log "FAIL $name" }
}

# --- 备份真实配置（Kevin 机器上 DataDir 是活的） ---
if (Test-Path $cfg) { Copy-Item $cfg $backupCfg -Force } else { Remove-Item $backupCfg -ErrorAction SilentlyContinue }
if (Test-Path $setupFlag) { Copy-Item $setupFlag $backupFlag -Force } else { Remove-Item $backupFlag -ErrorAction SilentlyContinue }

function Kill-App {
    foreach ($appProcessId in @($script:appPids.Keys)) {
        Stop-Process -Id $appProcessId -Force -ErrorAction SilentlyContinue
        [void]$script:appPids.Remove($appProcessId)
    }
    # 专注态主进程被强杀后，保留独立 watchdog 至少一个轮询周期，让它完成恢复。
    Start-Sleep -Milliseconds 2500
}

function Stop-TestProcesses {
    $testPaths = @([IO.Path]::GetFullPath($exe), [IO.Path]::GetFullPath($watchdogAlias))
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and ($testPaths -contains [IO.Path]::GetFullPath($_.ExecutablePath)) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Show-Taskbar {
    [void][SetupTaskbarGuard]::ShowTaskbar()
}

function Get-Win($procId) {
    # 修：向导/预览浮窗与主窗同为顶层 Window 时，取「Name 非空的主窗」而不是第一个命中
    # （SetupWizard 是独立 Window（name=focus-desktop 首次配置），浮动返回窗 name=SetupBackToWizardButton，
    #  它们与主窗 (name=focus-desktop) 并列；旧写法 foreach-return 先到谁取决于枚举顺序 → 全套断言随机失败）
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $main = $null
    foreach ($w in $all) {
        if ($w.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
        if ($w.Current.Name -eq "focus-desktop") { return $w }   # 主窗精确名
        if ($main -eq $null -and $w.Current.Name -ne "") { $main = $w }
    }
    if ($main -ne $null) { return $main }
    foreach ($w in $all) {
        if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $w }
    }
    return $null
}

function FindById($procId, $id) {
    $w = Get-Win $procId
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Click-Id($procId, $id) {
    $el = FindById $procId $id
    if ($el -eq $null) { return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    catch { return $false }
}

function FindByName($procId, $name) {
    $w = Get-Win $procId
    if ($w -eq $null) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Get-Text($procId, $id) {
    $el = FindById $procId $id
    if ($el -eq $null) { return $null }
    try { return $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return $el.Current.Name }
}

# Wait-App 必须在函数定义之后（原位删除）
function Wait-App($secs) {
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    $script:appPids[$p.Id] = $true
    Start-Sleep -Seconds $secs
    # 等窗口真起来（单文件首跑要自解压本机库，固定 sleep 不够）：轮询 UIA 主窗口至 secs 上限
    $deadline = (Get-Date).AddSeconds($secs)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Win $p.Id) -ne $null) { return $p }
        Start-Sleep -Milliseconds 800
    }
    return $p
}

try {
Show-Taskbar
Stop-TestProcesses
Show-Taskbar

# ============ 11. 中途强杀幂等（先做：不依赖向导 UI 交互） ============
Kill-App
Remove-Item $cfg -ErrorAction SilentlyContinue
Remove-Item $setupFlag -ErrorAction SilentlyContinue
$p = Wait-App 8
$next = FindById $p.Id "SetupNextButton"
Check "wizard appears on fresh start" ($next -ne $null)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$topWindows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$visibleMain = $false
foreach ($window in $topWindows) {
    if ($window.Current.Name -eq "focus-desktop" -and -not $window.Current.IsOffscreen) { $visibleMain = $true }
}
Check "main stays hidden during first-run wizard" (-not $visibleMain)
# 填到步骤3再杀：两步 Next
if ($next -ne $null) {
    [void](Click-Id $p.Id "SetupNextButton"); Start-Sleep -Milliseconds 600
    [void](Click-Id $p.Id "SetupNextButton"); Start-Sleep -Milliseconds 600
}
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
[void]$script:appPids.Remove($p.Id)
Start-Sleep -Milliseconds 500
$noHalf = -not (Test-Path $cfg) -or -not ((Get-Content $cfg -Raw -ErrorAction SilentlyContinue) -match '"configured":\s*true')
Check "kill mid-wizard leaves no half-written config" $noHalf
Kill-App

# ============ 1-9. 完整向导流程 ============
Remove-Item $cfg -ErrorAction SilentlyContinue
Remove-Item $setupFlag -ErrorAction SilentlyContinue
$p = Wait-App 8
$pid1 = $p.Id
$next = FindById $pid1 "SetupNextButton"
Check "wizard after fresh restart (idempotent)" ($next -ne $null)

# 步骤1 默认路径非空（FolderBox 只读文本，ValuePattern 读值）
$folderText = Get-Text $pid1 "SetupPathText"
Check "step1 folder default" ($folderText -ne $null -and $folderText.Length -gt 3)

# 步骤1 → 步骤2
Check "step1 next works" (Click-Id $pid1 "SetupNextButton")
Start-Sleep -Milliseconds 600

# 步骤2 preset 勾选默认（v1.0.3：五站含 notebooklm）
foreach ($sid in @("SetupPreset_bili","SetupPreset_chatgpt","SetupPreset_gemini","SetupPreset_deepseek","SetupPreset_notebooklm")) {
    $cb = FindById $pid1 $sid
    $ok = $cb -ne $null -and $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq "On"
    Check "preset $sid checked" $ok
}

# 步骤2 添加自定义站点 notion.so（UIA SetValue 在部分 WPF TextBox 上不触发 Text 变更——
# 改走真实击键路径：Focus 元素后 SendKeys）
Add-Type -AssemblyName System.Windows.Forms
$customUrl = FindById $pid1 "SetupCustomUrlInput"
if ($customUrl -ne $null) {
    try {
        # UIA ValuePattern SetValue（第一轮实测该路径生效且 Text 同步；SendKeys 需前台不可靠）
        $vp = $customUrl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue("notion.so")
        Start-Sleep -Milliseconds 300
        Check "custom url input accepts value" ($vp.Current.Value -eq "notion.so")
    } catch { Check "custom url input accepts value" $false }
} else { Check "custom url input exists" $false }

# v1.0.3：简称必填——不填 title 直接点添加应报错
[void](Click-Id $pid1 "SetupCustomAddButton")
Start-Sleep -Milliseconds 400
$titleRequiredShown = $false
for ($i = 0; $i -lt 8; $i++) {
    Start-Sleep -Milliseconds 250
    if ((FindByName $pid1 "请填写网站简称（将显示在首页和标签栏）。") -ne $null) { $titleRequiredShown = $true; break }
}
Check "custom title required (v1.0.3)" $titleRequiredShown

# 填写简称后再添加（v1.0.3：简称必填 ≤8 字符）
$customTitle = FindById $pid1 "SetupCustomTitleInput"
if ($customTitle -ne $null) {
    try {
        $tvp = $customTitle.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $tvp.SetValue("Notion")
        Start-Sleep -Milliseconds 300
        Check "custom title input accepts value" ($tvp.Current.Value -eq "Notion")
    } catch { Check "custom title input accepts value" $false }
} else { Check "custom title input exists" $false }

Check "custom add works" (Click-Id $pid1 "SetupCustomAddButton")
Start-Sleep -Milliseconds 600
# Border 行元素无 UIA peer（WFP Border 不暴露），但其子 TextBlock 可见：按 Name 找 Notion
$notionTb = FindByName $pid1 "Notion"
Check "custom site appears in list" ($notionTb -ne $null)

# 步骤2 添加失败用例（撞 preset 子域）→ 统一错误提示可见
# v1.0.3：title 框在成功添加后被清空——失败用例需先重填简称，否则先弹「请填写简称」
$customUrl2 = FindById $pid1 "SetupCustomUrlInput"
if ($customUrl2 -ne $null) {
    try { $customUrl2.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("sub.bilibili.com") } catch { }
}
$customTitle2 = FindById $pid1 "SetupCustomTitleInput"
if ($customTitle2 -ne $null) {
    try { $customTitle2.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("SubBili") } catch { }
}
[void](Click-Id $pid1 "SetupCustomAddButton")
# 错误 TextBlock 从 Collapsed 变 Visible 需要 UIA 树刷新——轮询重试至 2s
# （title=SubBili 已填——撞 preset 子域走 URL 校验分支）
$errFound = $false
for ($i = 0; $i -lt 8; $i++) {
    Start-Sleep -Milliseconds 250
    if ((FindByName $pid1 "网址无效或与已有站点重复") -ne $null) { $errFound = $true; break }
}
Check "invalid custom site shows unified error" $errFound

# 步骤2 → 步骤3
Check "step2 next works" (Click-Id $pid1 "SetupNextButton")
Start-Sleep -Milliseconds 600

# 步骤3 默认值（ValuePattern 读输入框值）
$quoteText = Get-Text $pid1 "SetupFocusQuoteInput"
$exitText = Get-Text $pid1 "SetupExitPhraseInput"
Check "step3 quote default" ($quoteText -ne $null -and $quoteText.Length -gt 2)
Check "step3 exit phrase default" ($exitText -ne $null -and $exitText.Length -gt 10)

# 步骤3 非法番茄钟拦截：填 0
$pomoW = FindById $pid1 "SetupPomoWork"
if ($pomoW -ne $null) {
    try { $pomoW.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("0") } catch { }
    [void](Click-Id $pid1 "SetupNextButton")
    Start-Sleep -Milliseconds 500
    $still3 = (FindById $pid1 "SetupPomoWork") -ne $null -and -not (FindById $pid1 "SetupPomoWork").Current.IsOffscreen
    Check "invalid pomodoro blocks next" $still3
    try { (FindById $pid1 "SetupPomoWork").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("25") } catch { }
} else { Check "pomo work input exists" $false }

# 步骤3（末步）→ 点「预览首页」（v1.0.3：向导 3 步，专注设置即末步）
Start-Sleep -Milliseconds 600
Check "preview button works" (Click-Id $pid1 "SetupPreviewButton")
Start-Sleep -Milliseconds 800
$wizardGone = (FindById $pid1 "SetupNextButton") -eq $null
$noCfgYet = -not (Test-Path $cfg) -or -not ((Get-Content $cfg -Raw -ErrorAction SilentlyContinue) -match '"configured":\s*true')
Check "preview hides wizard without side effects" ($wizardGone -and $noCfgYet)
# 返回向导（返回浮动窗带 AutomationId=SetupBackToWizardButton，顶级窗口）
$backOk = $false
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cnd = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid1)
$allWin = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cnd)
foreach ($w in $allWin) {
    $cnd2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SetupBackToWizardButton")
    $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cnd2)
    if ($btn -eq $null -and $w.Current.AutomationId -eq "SetupBackToWizardButton") { $btn = $w }
    if ($btn -ne $null) {
        try {
            $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $backOk = $true
        } catch {
            # 窗口本体无 InvokePattern：击它中心点（物理坐标，2560×1600@200% → UIA 矩形已是物理像素）
            try {
                $r = $btn.Current.BoundingRectangle
                $pt = $btn.GetClickablePoint()
                Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WzClick {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
                [WzClick]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
                Start-Sleep -Milliseconds 200
                [WzClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
                [WzClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
                $backOk = $true
            } catch { }
        }
        break
    }
}
Check "return to wizard" $backOk
Start-Sleep -Milliseconds 600

# 完成并开始使用（原子提交）
Check "finish button works" (Click-Id $pid1 "SetupNextButton")
Start-Sleep -Seconds 2

$cfgText = ""
if (Test-Path $cfg) { $cfgText = Get-Content $cfg -Raw }
Check "config written atomically" ($cfgText -match '"configured":\s*true')
Check "config schemaVersion 2" ($cfgText -match '"schemaVersion":\s*2')
Check "config sites include presets" ($cfgText -match '"bili"' -and $cfgText -match '"deepseek"' -and $cfgText -match '"notebooklm"')
Check "config sites include custom notion" ($cfgText -match 'notion\.so')
Check "setup_done flag waits for start focus" (-not (Test-Path $setupFlag))
$loginHint = $null
try { $loginHint = FindById $pid1 "SetupNextButton" } catch { }
# 向导关闭后 banner 出现（名字找）
$bannerFound = $false
$w2 = Get-Win $pid1
if ($w2 -ne $null) {
    $cnd2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "开始专注")
    $bannerFound = $w2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cnd2) -ne $null
}
Check "login guidance banner after wizard" $bannerFound

Kill-App

# ============ 10. 提交后重启不进向导 ============
$p = Wait-App 10
$noWizard = (FindById $p.Id "SetupNextButton") -eq $null
Check "no wizard after configured restart" $noWizard
# tab 条与提交配置一致（v1.0.3：notebooklm 第 5 preset tab 也应在）
$tabs = 0
$nbTab = $null
try {
    $w3 = Get-Win $p.Id
    if ($w3 -ne $null) {
        $cnd3 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "tab_site")
        if ($w3.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cnd3) -ne $null) { $tabs = 1 }
        $cnd4 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "tab_notebooklm")
        $nbTab = $w3.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cnd4)
    }
} catch { }
Check "custom site tab (tab_site) after restart" ($tabs -eq 1)
Check "notebooklm tab (tab_notebooklm) after restart (v1.0.3)" ($nbTab -ne $null)
Kill-App

# ============ 12. Legacy 配置不进向导 ============
# 用内嵌 legacy fixture（v1 纯净形：无 schemaVersion/configured/sites）——动态备份会被
# 前序步骤的 Remove-Item + app 启动 Save(configured:false) 污染（T6 调试实测定罪）
$legacyFixture = @'
{
  "studyFolder":  "D:\\杂文件\\focus",
  "exitPhrase":  "我发誓我确实有事需要离开这个环境，我要马上回来。",
  "whitelist":  ["chatgpt.com", "gemini.google.com", "aistudio.google.com", "deepseek.com", "bilibili.com"],
  "loginDomains":  ["accounts.google.com", "auth.openai.com", "cdn.auth0.com", "passport.bilibili.com", "login.live.com"],
  "focusQuote":  "你想成为怎样的人？",
  "pomodoroWorkMinutes": 25,
  "pomodoroShortBreakMinutes": 5,
  "pomodoroLongBreakMinutes": 15,
  "pomodoroCyclesUntilLong": 4
}
'@
Set-Content -Path $cfg -Value $legacyFixture -Encoding UTF8
$p = Wait-App 10
$noWizardLegacy = (FindById $p.Id "SetupNextButton") -eq $null
Check "legacy config skips wizard" $noWizardLegacy
$legacyTabs = @("tab_bili","tab_chatgpt","tab_gemini","tab_deepseek","tab_notebooklm")
$legacyOk = $true
foreach ($tid in $legacyTabs) {
    if ((FindById $p.Id $tid) -eq $null) { $legacyOk = $false }
}
Check "legacy tab set matches v1.0.3 (5 presets)" $legacyOk
Kill-App

# ============ 测试主体结束 ============
}
finally {
Kill-App
Show-Taskbar
Stop-TestProcesses
Show-Taskbar

# 清理：孤儿 webview + 恢复配置。无论断言/交互在哪一步失败都必须执行。
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*focus-desktop-data*" } | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}
Remove-Item $watchdogAlias -Force -ErrorAction SilentlyContinue
if (Test-Path $backupCfg) { Copy-Item $backupCfg $cfg -Force } elseif (Test-Path $cfg) { Remove-Item $cfg -Force }
if (Test-Path $backupFlag) { Copy-Item $backupFlag $setupFlag -Force } elseif (Test-Path $setupFlag) { Remove-Item $setupFlag -Force }
Remove-Item $backupCfg, $backupFlag -ErrorAction SilentlyContinue
Show-Taskbar
}

Log ("== RESULT: " + $script:pass + " pass / " + $script:fail + " fail ==")
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
