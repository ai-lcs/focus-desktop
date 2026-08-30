# shot-home.ps1 — DPI 感知截屏：SetProcessDPIAware 后按物理分辨率全屏截图
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiFix {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[DpiFix]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$exe = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 8

$ss = "D:\focus-desktop\tests\shot-home.png"
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output ("物理屏幕: " + $bounds.Width + "x" + $bounds.Height)
$b = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen(0, 0, 0, 0, $b.Size)
$b.Save($ss, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $b.Dispose()
Write-Output "shot: $ss"

Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1500
Start-Process $exe -ArgumentList "--restore" -WorkingDirectory (Split-Path $exe) | Out-Null
Start-Sleep -Seconds 2
Get-Process "focus-desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "END"
