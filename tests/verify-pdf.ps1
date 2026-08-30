# verify-pdf.ps1 — 端到端验证: 学习文件页(正确目录) -> 点 PDF 文件 -> PDF Tab 打开内容
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
if ($win -eq $null) { Log "FAIL main window"; exit 1 }
Log "PASS main window"

function FindById($id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function FindByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 1. "+" 按钮唯一性（数 AddTabButton 的 AutomationId + 全树 "+" 按钮数）
$plusCond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "+")),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
$plusAll = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $plusCond)
Log ("plus buttons count: " + $plusAll.Count + " (expect 1)")
if ($plusAll.Count -eq 1) { Log "PASS single plus button" } else { Log "FAIL plus count" }

# 2. 学习文件页: 点 Tab -> 检查目录路径文本 + PDF 文件项存在
$filesTab = FindById("tab_files")
if ($filesTab -eq $null) {
    # 兜底：按名字找 Button 类型
    $c2 = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "📂 学习文件")),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $filesTab = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c2)
}
if ($filesTab -ne $null) {
    $filesTab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 3
    # 找 PDF 文件名（学习目录里的简历 PDF）
    $pdfCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "李承晟_数据工程实习生_浙江理工大学.pdf")
    $pdfItem = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $pdfCond)
    if ($pdfItem -eq $null) { Log "FAIL pdf file not listed"; }
    if ($pdfItem -ne $null) {
        Log "PASS pdf file listed"
        # 3. 点击 PDF 文件（Invoke 不支持则坐标点击）
        try {
            $pdfItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        } catch {
            $pt = $pdfItem.GetClickablePoint()
            Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MC3 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
            [MC3]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
            Start-Sleep -Milliseconds 200
            [MC3]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
            [MC3]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        }
        Start-Sleep -Seconds 6
        # 4. Tab 条出现以文件名命名的 Tab
        $pdfTab = FindByName("李承晟_数据工程实习生_浙江理工大学")
        if ($pdfTab -ne $null) { Log "PASS pdf tab created with filename" } else { Log "FAIL pdf tab missing" }
        # 5. WebView 宿主可见（内容区在显示网页/PDF）
        # 通过检查窗口非 home 元素仍存在推断
    } else { Log "FAIL pdf file not listed in files view" }
} else { Log "FAIL files tab not found" }

# 6. 截图确认 PDF 内容渲染
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF6 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiF6]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Start-Sleep -Seconds 3
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen(0, 0, 0, 0, $b.Size)
$b.Save("D:\focus-desktop\tests\pdf-open.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $b.Dispose()
Log "shot saved"

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Log "END"
