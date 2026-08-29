$WshShell = New-Object -ComObject WScript.Shell
$Desktop = [Environment]::GetFolderPath("Desktop")

# 主程序快捷方式
$lnk = $WshShell.CreateShortcut("$Desktop\focus-desktop.lnk")
$lnk.TargetPath = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$lnk.WorkingDirectory = "D:\focus-desktop\release\focus-desktop"
$lnk.Description = "专注学习环境（全屏 kiosk）"
$lnk.IconLocation = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe,0"
$lnk.Save()

# 恢复入口快捷方式（任务栏没回来时双击）
$lnk2 = $WshShell.CreateShortcut("$Desktop\focus-desktop 恢复.lnk")
$lnk2.TargetPath = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe"
$lnk2.Arguments = "--restore"
$lnk2.WorkingDirectory = "D:\focus-desktop\release\focus-desktop"
$lnk2.Description = "恢复任务栏与系统状态（focus-desktop 异常退出后用）"
$lnk2.IconLocation = "D:\focus-desktop\release\focus-desktop\focus-desktop.exe,0"
$lnk2.Save()

Write-Output "shortcuts created:"
Get-ChildItem "$Desktop\focus-desktop*.lnk" | Select-Object -ExpandProperty Name
