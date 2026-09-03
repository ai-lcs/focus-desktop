# verify-watchdog.ps1 - independent watchdog recovery test
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TB {
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@

function Log($m) { Write-Output ("{0} {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $m) }
function TaskbarVisible { $h = [TB]::FindWindow("Shell_TrayWnd", $null); return ($h -ne [IntPtr]::Zero) -and [TB]::IsWindowVisible($h) }
function Show-Taskbar { $h = [TB]::FindWindow("Shell_TrayWnd", $null); if ($h -ne [IntPtr]::Zero) { [void][TB]::ShowWindow($h, 5) } }

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $repo "release\focus-desktop\focus-desktop.exe"
$dataDir = Join-Path $repo "release\focus-desktop\focus-desktop-data"
$watchdogExe = Join-Path (Split-Path $exe) "focus-desktop-watchdog.exe"
$configFile = Join-Path $dataDir "config.json"
$setupFlag = Join-Path $dataDir "setup_done.flag"
$stateFile = Join-Path $dataDir "session_state.json"
$backupDir = Join-Path $env:TEMP "focus-desktop-watchdog-test-backup"
$hadConfig = Test-Path $configFile
$hadSetup = Test-Path $setupFlag
$hadState = Test-Path $stateFile
$failed = $false

function Stop-TestProcesses {
    $testPaths = @([IO.Path]::GetFullPath($exe), [IO.Path]::GetFullPath($watchdogExe))
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and ($testPaths -contains [IO.Path]::GetFullPath($_.ExecutablePath)) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
if ($hadConfig) { Copy-Item $configFile (Join-Path $backupDir "config.json") -Force }
if ($hadSetup) { Copy-Item $setupFlag (Join-Path $backupDir "setup_done.flag") -Force }
if ($hadState) { Copy-Item $stateFile (Join-Path $backupDir "session_state.json") -Force }

try {
    Show-Taskbar
    Stop-TestProcesses
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $studyFolderJson = $repo.Replace('\', '\\')
    Set-Content -Path $configFile -Value "{`"studyFolder`":`"$studyFolderJson`",`"whitelist`":[`"chatgpt.com`"],`"loginDomains`":[],`"focusQuote`":`"verify`",`"exitPhrase`":`"verify`"}" -Encoding UTF8
    Set-Content -Path $setupFlag -Value "verify" -Encoding UTF8

    Log "=== kill main process; independent watchdog must restore taskbar ==="
    $testStartedAt = Get-Date
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    Start-Sleep -Seconds 6

    $tbHidden = -not (TaskbarVisible)
    $wd = Get-Process "focus-desktop-watchdog" -ErrorAction SilentlyContinue | Select-Object -First 1
    Log ("taskbar hidden = $tbHidden (expected True)")
    Log ("independent watchdog exists = " + [bool]$wd + " (expected True)")

    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Log "main process pid=$($p.Id) killed; waiting for watchdog..."

    $recovered = $false
    $flagClean = $false
    $wdHit = $false
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 1000
        $recovered = TaskbarVisible
        $flagAfter = Get-Content $stateFile -Raw -ErrorAction SilentlyContinue
        $flagClean = $flagAfter -match '"focus_mode_active":\s*false'
        $wdLog = Get-ChildItem (Join-Path $dataDir "logs\crash-*.log") -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $testStartedAt } |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($wdLog) { $wdHit = (Get-Content $wdLog.FullName -Raw).Contains("watchdog-recovered") }
        if ($recovered -and $flagClean -and $wdHit) { Log ("watchdog recovery completed after $($i+1)s"); break }
    }

    $aliasWasIndependent = $null -ne $wd -and $wd.ProcessName -eq "focus-desktop-watchdog"
    $failed = -not ($tbHidden -and $aliasWasIndependent -and $recovered -and $flagClean -and $wdHit)
    Log ("=== result: hidden=$tbHidden independent=$aliasWasIndependent recovered=$recovered flagClean=$flagClean watchdogLog=$wdHit ===")
}
finally {
    Show-Taskbar
    Stop-TestProcesses
    Remove-Item $watchdogExe -Force -ErrorAction SilentlyContinue
    if ($hadConfig) { Copy-Item (Join-Path $backupDir "config.json") $configFile -Force } else { Remove-Item $configFile -Force -ErrorAction SilentlyContinue }
    if ($hadSetup) { Copy-Item (Join-Path $backupDir "setup_done.flag") $setupFlag -Force } else { Remove-Item $setupFlag -Force -ErrorAction SilentlyContinue }
    if ($hadState) { Copy-Item (Join-Path $backupDir "session_state.json") $stateFile -Force } else { Remove-Item $stateFile -Force -ErrorAction SilentlyContinue }
    Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    Show-Taskbar
}

if ($failed) { exit 1 }
