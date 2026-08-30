# repro-pomo-run.ps1 — 复现番茄钟运行态: UIA 点 45 -> 开始 -> 截图
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiF2 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiF2]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
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
if ($win -eq $null) { Write-Output "WIN NOT FOUND"; exit 1 }

function ClickByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($el -ne $null) {
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    }
    return $false
}

# 点 45 模式
$r1 = ClickByName("45")
Write-Output "click 45: $r1"
Start-Sleep -Milliseconds 500
# 点 开始（番茄钟的开始；自由计时器也有一个"开始"——FindFirst 会拿第一个。
# 顺序保险：先点 45 已把模式改了，两个开始按钮都叫"开始"。改为点"暂停"出现与否判断。
$r2 = ClickByName("开始")
Write-Output "click start: $r2"
Start-Sleep -Seconds 4

# 截图
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen(0, 0, 0, 0, $b.Size)
$b.Save("D:\focus-desktop\tests\pomo-run.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $b.Dispose()
Write-Output "shot saved"

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
