[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Preflight", "Install", "Upgrade", "UninstallKeep", "Reinstall", "UninstallClear", "Restore")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$TestRoot,

    [string]$SetupPath,
    [string]$ExpectedSetupSha256,
    [string]$ExpectedMachineInstallPath,
    [switch]$ConfirmProfileIsolation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $workspace "evidence\focus-desk-install-tests")).TrimEnd('\')
$testRootFull = [IO.Path]::GetFullPath($TestRoot).TrimEnd('\')
$testRootPrefix = $evidenceRoot + '\'
if (-not $testRootFull.StartsWith($testRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TestRoot must be a child of the workspace evidence folder: $evidenceRoot"
}
if ($testRootFull.Substring($testRootPrefix.Length).Contains('\')) {
    throw "Use one dedicated run folder directly under: $evidenceRoot"
}

$runName = Split-Path -Leaf $testRootFull
if ($runName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw "TestRoot's final folder must use a simple run name (letters, digits, dot, underscore, hyphen)."
}

$localAppDataPath = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)).TrimEnd('\')
$dataPath = [IO.Path]::GetFullPath((Join-Path $localAppDataPath "focus-desktop")).TrimEnd('\')
$expectedDataPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "focus-desktop")).TrimEnd('\')
if (-not $dataPath.Equals($expectedDataPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved user-data target differs from the one explicitly approved by this script."
}

$installPath = [IO.Path]::GetFullPath((Join-Path $testRootFull "app")).TrimEnd('\')
$statePath = Join-Path $testRootFull "state.json"
$uninstallKeyPath = "Software\Microsoft\Windows\CurrentVersion\Uninstall\{7B3F9E2A-6C1D-4E8B-9F5A-F2D8C4A1E6B0}_is1"
function Assert-NoReparsePointInExistingPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    $relative = $full.Substring($root.Length)
    foreach ($part in ($relative -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to use a path that includes a junction or symbolic link: $current"
            }
        }
    }
}

function Get-TreeFingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [pscustomobject]@{ Exists = $false; Hash = ""; FileCount = 0; Bytes = [long]0 }
    }

    Assert-NoReparsePointInExistingPath $Path
    $base = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $records = [System.Collections.Generic.List[string]]::new()
    $fileCount = 0
    [long]$totalBytes = 0
    foreach ($item in (Get-ChildItem -LiteralPath $base -Force -Recurse -ErrorAction Stop)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to fingerprint a user-data tree containing a junction or symbolic link."
        }
        $relative = $item.FullName.Substring($base.Length).TrimStart('\')
        $relativeKey = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($relative))
        if ($item.PSIsContainer) {
            $records.Add("D|$relativeKey")
        } else {
            $digest = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $records.Add("F|$relativeKey|$($item.Length)|$digest")
            $fileCount++
            $totalBytes += [long]$item.Length
        }
    }
    $records.Sort([StringComparer]::Ordinal)
    $canonical = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $records))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $treeHash = [BitConverter]::ToString($sha.ComputeHash($canonical)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ Exists = $true; Hash = $treeHash; FileCount = $fileCount; Bytes = $totalBytes }
}

function Get-RegistrySnapshot([Microsoft.Win32.RegistryHive]$Hive) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey($uninstallKeyPath, $false)
        if ($null -eq $key) { return [pscustomobject]@{ Exists = $false; Values = [ordered]@{} } }
        try {
            $values = [ordered]@{}
            foreach ($name in @("DisplayName", "DisplayVersion", "InstallLocation", "UninstallString", "QuietUninstallString", "Publisher")) {
                $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $values[$name] = if ($null -eq $value) { $null } else { [string]$value }
            }
            return [pscustomobject]@{ Exists = $true; Values = $values }
        } finally { $key.Dispose() }
    } finally { $base.Dispose() }
}

function Get-RegistryFingerprint($Snapshot) {
    $json = ConvertTo-Json -InputObject $Snapshot -Depth 6 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Assert-OriginalMachineInstallUnchanged($State) {
    $current = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::LocalMachine)
    if ((Get-RegistryFingerprint $current) -ne $State.HklmFingerprint) {
        throw "The original machine-wide Focus Desk uninstall registration changed; stopping without repair."
    }
}

function Assert-ExpectedOriginalMachineInstall($Snapshot, [string]$ExpectedPath) {
    if ([string]::IsNullOrWhiteSpace($ExpectedPath) -or -not [IO.Path]::IsPathRooted($ExpectedPath)) {
        throw "Supply an absolute -ExpectedMachineInstallPath confirmed for this machine."
    }
    if (-not $Snapshot.Exists) { throw "The known machine-wide Focus Desk install registration is missing." }
    $registeredPath = [string]$Snapshot.Values.InstallLocation
    if ([string]::IsNullOrWhiteSpace($registeredPath) -or
        -not [IO.Path]::GetFullPath($registeredPath).TrimEnd('\').Equals(
            [IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The original machine-wide install does not point to the reviewed path $ExpectedPath."
    }
}

function Get-FocusDeskProcessIds {
    $matches = @(Get-CimInstance -ClassName Win32_Process -Filter "Name='focus-desktop.exe'" -ErrorAction Stop)
    return @($matches | ForEach-Object { [int]$_.ProcessId })
}

function Get-ProfileWebViewProcessIds {
    $matches = @(Get-CimInstance -ClassName Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction Stop)
    $pids = [System.Collections.Generic.List[int]]::new()
    foreach ($process in $matches) {
        $commandLine = [string]$process.CommandLine
        if ([string]::IsNullOrWhiteSpace($commandLine)) {
            throw "Cannot verify WebView2 profile use for PID $($process.ProcessId); no command line is readable."
        }
        if ($commandLine.IndexOf($dataPath, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $pids.Add([int]$process.ProcessId)
        }
    }
    return @($pids)
}

function Assert-ProfileIdle {
    $focusPids = @(Get-FocusDeskProcessIds)
    if ($focusPids.Count -gt 0) {
        throw "Focus Desk processes are still running (PID list: $($focusPids -join ', ')); close them normally before moving/restoring data. Nothing was terminated."
    }
    $webViewPids = @(Get-ProfileWebViewProcessIds)
    if ($webViewPids.Count -gt 0) {
        throw "WebView2 still references the Focus Desk data directory (PID list: $($webViewPids -join ', ')); close the owning app normally. Nothing was terminated."
    }
}

function Get-ShortcutTargets {
    $userPrograms = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) "Microsoft\Windows\Start Menu\Programs"
    $commonPrograms = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "Microsoft\Windows\Start Menu\Programs"
    $targets = [System.Collections.Generic.List[string]]::new()
    foreach ($desktop in @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
    )) {
        if (-not [string]::IsNullOrWhiteSpace($desktop)) {
            $targets.Add((Join-Path $desktop "Focus Desk.lnk"))
            foreach ($link in (Get-ChildItem -LiteralPath $desktop -Filter "Focus Desk*.lnk" -File -ErrorAction SilentlyContinue)) {
                $targets.Add($link.FullName)
            }
        }
    }
    foreach ($programs in @($userPrograms, $commonPrograms)) {
        $group = Join-Path $programs "Focus Desk"
        $targets.Add((Join-Path $group "Focus Desk.lnk"))
        $targets.Add((Join-Path $group "Focus Desk 恢复.lnk"))
        if (Test-Path -LiteralPath $group -PathType Container) {
            foreach ($link in (Get-ChildItem -LiteralPath $group -Filter "*.lnk" -File -ErrorAction SilentlyContinue)) {
                $targets.Add($link.FullName)
            }
        }
    }
    return @($targets | Sort-Object -Unique)
}

function Save-ShortcutSnapshot([string]$BackupDirectory) {
    $null = New-Item -ItemType Directory -Path $BackupDirectory -Force
    $items = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($path in (Get-ShortcutTargets)) {
        $index++
        $exists = Test-Path -LiteralPath $path -PathType Leaf
        $backup = $null
        $hash = $null
        if ($exists) {
            $backup = Join-Path $BackupDirectory ("shortcut-{0:D3}.lnk" -f $index)
            Copy-Item -LiteralPath $path -Destination $backup
            $hash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $items.Add([pscustomobject]@{ Path = $path; Existed = $exists; Backup = $backup; Sha256 = $hash })
    }
    return @($items)
}

function Restore-ShortcutSnapshot($State) {
    $generatedDirectory = Join-Path $testRootFull "generated-shortcuts"
    $null = New-Item -ItemType Directory -Path $generatedDirectory -Force
    $index = 0
    foreach ($item in $State.Shortcuts) {
        $index++
        if ($item.Existed) {
            if ((Get-FileHash -LiteralPath $item.Backup -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Sha256) {
                throw "A preserved shortcut backup failed its SHA-256 check: $($item.Backup)"
            }
            if ((Test-Path -LiteralPath $item.Path -PathType Leaf) -and
                (Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $item.Sha256) {
                continue
            }
            $parent = Split-Path -Parent $item.Path
            $null = New-Item -ItemType Directory -Path $parent -Force
            Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Force
            if (-not (Test-Path -LiteralPath $item.Path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Sha256) {
                throw "Restored shortcut failed its SHA-256 check: $($item.Path)"
            }
        } elseif (Test-Path -LiteralPath $item.Path -PathType Leaf) {
            $shell = New-Object -ComObject WScript.Shell
            try {
                $link = $shell.CreateShortcut([string]$item.Path)
                $targetPath = [string]$link.TargetPath
                if ([string]::IsNullOrWhiteSpace($targetPath) -or
                    -not [IO.Path]::GetFullPath($targetPath).TrimEnd('\').Equals(
                        (Join-Path $installPath "focus-desktop.exe"), [StringComparison]::OrdinalIgnoreCase)) {
                    throw "A new Focus Desk shortcut was changed or does not point to the isolated install; it was left untouched: $($item.Path)"
                }
                $archived = Join-Path $generatedDirectory ("{0:D3}-{1}" -f $index, (Split-Path -Leaf $item.Path))
                if (Test-Path -LiteralPath $archived) { throw "Generated-shortcut evidence already exists; refusing to overwrite it." }
                Move-Item -LiteralPath $item.Path -Destination $archived
            } finally {
                if ($null -ne $shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) {
                    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
                }
            }
        }
    }
}

function Write-State($State) {
    $json = ConvertTo-Json -InputObject $State -Depth 10
    $temporary = $statePath + ".tmp"
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        [IO.File]::Replace($temporary, $statePath, [NullString]::Value, $true)
    } else {
        [IO.File]::Move($temporary, $statePath)
    }
}

function Read-State {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "No test state found: $statePath" }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ([int]$state.Schema -ne 2) { throw "Unsupported or stale test state schema; use a new evidence run folder." }
    if ([IO.Path]::GetFullPath([string]$state.TestRoot).TrimEnd('\') -ne $testRootFull -or
        [IO.Path]::GetFullPath([string]$state.DataPath).TrimEnd('\') -ne $dataPath -or
        [IO.Path]::GetFullPath([string]$state.InstallPath).TrimEnd('\') -ne $installPath) {
        throw "Saved state paths do not match the validated test and profile paths."
    }
    $evidenceCopy = [IO.Path]::GetFullPath([string]$state.OriginalEvidenceCopy)
    if (-not $evidenceCopy.Equals((Join-Path $testRootFull "original-user-data\focus-desktop"), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Saved original-data evidence path differs from the fixed test location."
    }
    if ($state.OriginalBackupPath) {
        $backupPath = [IO.Path]::GetFullPath([string]$state.OriginalBackupPath).TrimEnd('\')
        if ($backupPath -notmatch ([Regex]::Escape($dataPath) + '\.docktest-original-[0-9a-f]{32}$')) {
            throw "Saved original-profile backup path is not the expected LocalAppData sibling."
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$state.ExpectedMachineInstallPath) -or
        -not [IO.Path]::GetFullPath([string]$state.HklmBefore.Values.InstallLocation).TrimEnd('\').Equals(
            [IO.Path]::GetFullPath([string]$state.ExpectedMachineInstallPath).TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Saved original machine-install path does not match its initial registry snapshot."
    }
    $allowedShortcutPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in (Get-ShortcutTargets)) { $null = $allowedShortcutPaths.Add([IO.Path]::GetFullPath($path)) }
    foreach ($shortcut in $state.Shortcuts) {
        if (-not $allowedShortcutPaths.Contains([IO.Path]::GetFullPath([string]$shortcut.Path))) {
            throw "Saved shortcut path is outside the package's known shortcut locations."
        }
        if ($shortcut.Existed) {
            $backup = [IO.Path]::GetFullPath([string]$shortcut.Backup)
            if (-not $backup.StartsWith((Join-Path $testRootFull "shortcut-backups\"), [StringComparison]::OrdinalIgnoreCase) -or
                (Split-Path -Leaf $backup) -notmatch '^shortcut-\d{3}\.lnk$') {
                throw "Saved shortcut backup escaped its evidence folder."
            }
        }
    }
    return $state
}

function Assert-SetupArtifact {
    if ([string]::IsNullOrWhiteSpace($SetupPath) -or [string]::IsNullOrWhiteSpace($ExpectedSetupSha256)) {
        throw "Supply both -SetupPath and -ExpectedSetupSha256 for package installation or upgrade."
    }
    $fullSetup = [IO.Path]::GetFullPath($SetupPath)
    if (-not (Test-Path -LiteralPath $fullSetup -PathType Leaf)) { throw "Setup file not found: $fullSetup" }
    if ((Split-Path -Leaf $fullSetup) -notmatch '^FocusDesktop-Setup-\d+\.\d+\.\d+\.exe$') {
        throw "SetupPath must be a versioned FocusDesktop-Setup-*.exe artifact."
    }
    $actual = (Get-FileHash -LiteralPath $fullSetup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSetupSha256.ToLowerInvariant()) { throw "Setup SHA-256 does not match the expected artifact hash." }
    return [pscustomobject]@{ Path = $fullSetup; Sha256 = $actual }
}

function Quote-Argument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-Setup([string]$Setup, [string]$LogPath) {
    $arguments = @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER",
        "/NOCLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS",
        "/DIR=$(Quote-Argument $installPath)", "/LOG=$(Quote-Argument $LogPath)"
    ) -join ' '
    $process = Start-Process -FilePath $Setup -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Isolated setup exited with code $($process.ExitCode); see $LogPath" }
}

function Invoke-TestUninstaller([string]$Mode) {
    $uninstaller = Join-Path $installPath "unins000.exe"
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "Isolated uninstaller is missing: $uninstaller" }
    $logPath = Join-Path $testRootFull "uninstall-$Mode.log"
    $arguments = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$(Quote-Argument $logPath)")
    if ($Mode -eq "Clear") { $arguments += "/REMOVEUSERDATA" }
    $process = Start-Process -FilePath $uninstaller -ArgumentList ($arguments -join ' ') -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Isolated uninstaller exited with code $($process.ExitCode); see $logPath" }
}

function Assert-IsolatedRegistration($State) {
    $user = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)
    if (-not $user.Exists) { throw "The expected current-user uninstall registration is missing. The machine-wide install was not targeted." }
    $registeredPath = [string]$user.Values.InstallLocation
    if ([string]::IsNullOrWhiteSpace($registeredPath) -or
        -not [IO.Path]::GetFullPath($registeredPath).TrimEnd('\').Equals($installPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Current-user registration does not point exactly to the isolated install directory."
    }
    Assert-OriginalMachineInstallUnchanged $State
}

function Copy-TreeWithoutDeleting([string]$Source, [string]$Destination, [string]$LogPath) {
    $null = New-Item -ItemType Directory -Path $Destination -Force
    $robocopy = Join-Path $env:SystemRoot "System32\robocopy.exe"
    if (-not (Test-Path -LiteralPath $robocopy -PathType Leaf)) { throw "robocopy.exe is unavailable; refusing a partial profile restore." }
    & $robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /XJ /NFL /NDL /NJH /NJS /NP *> $LogPath
    $code = $LASTEXITCODE
    if ($code -ge 8) { throw "Profile restore copy failed with robocopy code $code; see $LogPath" }
}

if ($Action -eq "Preflight") {
    Assert-NoReparsePointInExistingPath $workspace
    Assert-NoReparsePointInExistingPath $testRootFull
    $hklm = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::LocalMachine)
    Assert-ExpectedOriginalMachineInstall $hklm $ExpectedMachineInstallPath
    $hkcu = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)
    if ($hkcu.Exists) { throw "An existing current-user registration with this AppId must be reviewed before testing." }
    $focusPids = @(Get-FocusDeskProcessIds)
    $webViewPids = @(Get-ProfileWebViewProcessIds)
    Write-Host "Preflight passed. HKLM install version: $($hklm.Values.DisplayVersion); location: $($hklm.Values.InstallLocation)"
    Write-Host "HKCU registration: absent; Focus Desk process count: $($focusPids.Count); matching WebView2 process count: $($webViewPids.Count)"
    Write-Host "User-data target: $dataPath"
    exit 0
}

if ($Action -eq "Install") {
    if (-not $ConfirmProfileIsolation) { throw "Install requires -ConfirmProfileIsolation after reviewing this script and the exact test paths." }
    if (Test-Path -LiteralPath $testRootFull) { throw "Refusing to reuse existing evidence folder: $testRootFull" }
    if ([string]::IsNullOrWhiteSpace($ExpectedMachineInstallPath)) {
        throw "Install requires -ExpectedMachineInstallPath with the reviewed existing HKLM path."
    }
    $artifact = Assert-SetupArtifact
    Assert-NoReparsePointInExistingPath $workspace
    Assert-NoReparsePointInExistingPath $testRootFull
    Assert-ProfileIdle
    $hklm = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::LocalMachine)
    Assert-ExpectedOriginalMachineInstall $hklm $ExpectedMachineInstallPath
    $hkcu = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)
    if ($hkcu.Exists) { throw "A current-user registration with this AppId already exists; no installation was attempted." }

    $null = New-Item -ItemType Directory -Path $testRootFull -Force
    $shortcutBackupDir = Join-Path $testRootFull "shortcut-backups"
    $shortcuts = Save-ShortcutSnapshot $shortcutBackupDir
    $originalExisted = Test-Path -LiteralPath $dataPath -PathType Container
    $originalFingerprint = Get-TreeFingerprint $dataPath
    $backupSuffix = [Guid]::NewGuid().ToString('N')
    $originalBackupPath = if ($originalExisted) { "$dataPath.docktest-original-$backupSuffix" } else { $null }
    $evidenceCopy = Join-Path $testRootFull "original-user-data\focus-desktop"
    $state = [pscustomobject]@{
        Schema = 2
        Status = "Prepared"
        TestRoot = $testRootFull
        InstallPath = $installPath
        SetupPath = $artifact.Path
        SetupSha256 = $artifact.Sha256
        DataPath = $dataPath
        OriginalProfileExisted = $originalExisted
        OriginalProfileFingerprint = $originalFingerprint
        OriginalEvidenceCopy = $evidenceCopy
        OriginalBackupPath = $originalBackupPath
        ProfileMoved = $false
        HklmBefore = $hklm
        HklmFingerprint = Get-RegistryFingerprint $hklm
        ExpectedMachineInstallPath = [IO.Path]::GetFullPath($ExpectedMachineInstallPath).TrimEnd('\')
        Shortcuts = $shortcuts
        TestProfileFingerprint = $null
    }
    Write-State $state

    if ($originalExisted) {
        $evidenceParent = Split-Path -Parent $evidenceCopy
        $null = New-Item -ItemType Directory -Path $evidenceParent -Force
        Copy-Item -LiteralPath $dataPath -Destination $evidenceParent -Recurse -Force
        $evidenceFingerprint = Get-TreeFingerprint $evidenceCopy
        if ($evidenceFingerprint.Hash -ne $originalFingerprint.Hash) {
            throw "Offline evidence copy did not match the original profile. The original has not been moved."
        }
        if (Test-Path -LiteralPath $originalBackupPath) { throw "Offline backup sibling already exists; refusing to move user data." }
        [IO.Directory]::Move($dataPath, $originalBackupPath)
        $state.ProfileMoved = $true
        $state.Status = "ProfileIsolated"
        Write-State $state
        $offlineFingerprint = Get-TreeFingerprint $originalBackupPath
        if ($offlineFingerprint.Hash -ne $originalFingerprint.Hash -or (Test-Path -LiteralPath $dataPath)) {
            throw "Original profile move did not verify. Stop and use Restore; no process was terminated."
        }
    }

    $state.Status = "Installing"
    Write-State $state
    Invoke-Setup $artifact.Path (Join-Path $testRootFull "setup-install.log")
    Assert-IsolatedRegistration $state
    $state.Status = "Installed"
    Write-State $state
    Write-Host "Isolated install completed. HKCU points to $installPath; original HKLM registration is unchanged."
    Write-Host "Keep this app/profile isolated until Restore completes. Evidence: $testRootFull"
    exit 0
}

$state = Read-State
Assert-OriginalMachineInstallUnchanged $state

switch ($Action) {
    "Upgrade" {
        Assert-ProfileIdle
        if ($state.Status -notin @("Installed", "Upgraded")) { throw "Upgrade is allowed only while the isolated install is present." }
        $artifact = Assert-SetupArtifact
        if ($artifact.Sha256 -ne $state.SetupSha256) { throw "Upgrade artifact differs from the exact package tested during Install." }
        Assert-IsolatedRegistration $state
        $profileBefore = Get-TreeFingerprint $dataPath
        Invoke-Setup $artifact.Path (Join-Path $testRootFull "setup-upgrade.log")
        Assert-IsolatedRegistration $state
        $profileAfter = Get-TreeFingerprint $dataPath
        if ($profileBefore.Exists -ne $profileAfter.Exists -or
            ($profileBefore.Exists -and $profileBefore.Hash -ne $profileAfter.Hash)) {
            throw "Test profile changed across isolated upgrade; the before and after copies remain available for review."
        }
        $state.Status = "Upgraded"
        Write-State $state
        Write-Host "Isolated upgrade completed with the same SHA-256 package; the user-data tree is unchanged."
    }
    "UninstallKeep" {
        Assert-ProfileIdle
        if ($state.Status -notin @("Installed", "Upgraded")) { throw "Keep-data uninstall is allowed only while the isolated install is present." }
        Assert-IsolatedRegistration $state
        $profileBefore = Get-TreeFingerprint $dataPath
        Invoke-TestUninstaller "Keep"
        Assert-ProfileIdle
        $user = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)
        if ($user.Exists) { throw "Current-user uninstall registration remains after isolated uninstall." }
        Assert-OriginalMachineInstallUnchanged $state
        $profileAfter = Get-TreeFingerprint $dataPath
        if ($profileBefore.Exists -ne $profileAfter.Exists -or
            ($profileBefore.Exists -and $profileBefore.Hash -ne $profileAfter.Hash)) {
            throw "Test profile changed across keep-data uninstall; the previous and current copies remain available for review."
        }
        if ($profileBefore.Exists) {
            $state.TestProfileFingerprint = $profileAfter
        }
        $state.Status = "UninstalledKeep"
        Write-State $state
        Write-Host "Isolated uninstall completed without /REMOVEUSERDATA. Test profile was retained when present."
    }
    "Reinstall" {
        Assert-ProfileIdle
        if ($state.Status -ne "UninstalledKeep") { throw "Reinstall is allowed only after UninstallKeep in this same test run." }
        if ((Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)).Exists) {
            throw "Current-user registration already exists; refusing a second install."
        }
        $artifact = Assert-SetupArtifact
        if ($artifact.Sha256 -ne $state.SetupSha256) { throw "Reinstall must use the exact previously tested setup artifact." }
        if ($state.OriginalProfileExisted) {
            $offline = [IO.Path]::GetFullPath([string]$state.OriginalBackupPath).TrimEnd('\')
            if (-not (Test-Path -LiteralPath $offline -PathType Container) -or
                (Get-TreeFingerprint $offline).Hash -ne $state.OriginalProfileFingerprint.Hash) {
                throw "Verified original-profile backup is unavailable; refusing to reinstall."
            }
        }
        $testProfile = Get-TreeFingerprint $dataPath
        if ($null -eq $state.TestProfileFingerprint -or
            $testProfile.Exists -ne $state.TestProfileFingerprint.Exists -or
            ($testProfile.Exists -and $testProfile.Hash -ne $state.TestProfileFingerprint.Hash)) {
            throw "Retained test profile differs from the verified UninstallKeep profile; refusing to continue."
        }
        if (-not $testProfile.Exists) { throw "The app did not create a test profile; run the isolated app before the keep/clear cycle." }
        $state.Status = "ReinstallingForClear"
        Write-State $state
        Invoke-Setup $artifact.Path (Join-Path $testRootFull "setup-reinstall-for-clear.log")
        Assert-IsolatedRegistration $state
        $profileAfter = Get-TreeFingerprint $dataPath
        if (-not $profileAfter.Exists -or $profileAfter.Hash -ne $testProfile.Hash) {
            throw "Test profile changed across isolated reinstall; the retained and current copies remain available for review."
        }
        $state.Status = "ReinstalledForClear"
        Write-State $state
        Write-Host "Same package reinstalled in the isolated folder; the retained user-data tree is unchanged."
    }
    "UninstallClear" {
        Assert-ProfileIdle
        if ($state.Status -ne "ReinstalledForClear") { throw "Clear-uninstall requires a same-run Reinstall after UninstallKeep." }
        Assert-IsolatedRegistration $state
        if ($state.OriginalProfileExisted) {
            $offline = [IO.Path]::GetFullPath([string]$state.OriginalBackupPath).TrimEnd('\')
            if (-not (Test-Path -LiteralPath $offline -PathType Container) -or
                (Get-TreeFingerprint $offline).Hash -ne $state.OriginalProfileFingerprint.Hash) {
                throw "Verified original-profile backup is unavailable; refusing /REMOVEUSERDATA."
            }
            $state.ProfileMoved = $true
            Write-State $state
        }
        $clearEvidence = Join-Path $testRootFull "test-profile-before-clear\focus-desktop"
        if (Test-Path -LiteralPath $dataPath -PathType Container) {
            if (Test-Path -LiteralPath $clearEvidence) { throw "Clear evidence already exists; refusing to overwrite it." }
            $clearParent = Split-Path -Parent $clearEvidence
            $null = New-Item -ItemType Directory -Path $clearParent -Force
            Copy-Item -LiteralPath $dataPath -Destination $clearParent -Recurse -Force
            $beforeClear = Get-TreeFingerprint $dataPath
            if ((Get-TreeFingerprint $clearEvidence).Hash -ne $beforeClear.Hash) {
                throw "Test-profile evidence copy failed; /REMOVEUSERDATA was not run."
            }
            $state.TestProfileFingerprint = $beforeClear
            Write-State $state
        }
        Invoke-TestUninstaller "Clear"
        Assert-ProfileIdle
        if (Test-Path -LiteralPath $dataPath) { throw "Isolated /REMOVEUSERDATA left the test profile directory present." }
        $user = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)
        if ($user.Exists) { throw "Current-user uninstall registration remains after isolated clear-uninstall." }
        Assert-OriginalMachineInstallUnchanged $state
        $state.Status = "UninstalledClear"
        Write-State $state
        Write-Host "Isolated clear-uninstall removed only the test profile; its pre-clear evidence is retained."
    }
    "Restore" {
        Assert-ProfileIdle
        $user = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)
        if ($user.Exists) {
            Assert-IsolatedRegistration $state
            Invoke-TestUninstaller "Keep"
            $user = Get-RegistrySnapshot ([Microsoft.Win32.RegistryHive]::CurrentUser)
            if ($user.Exists) { throw "Isolated uninstall registration remains; refusing to restore the original profile." }
        }
        Assert-ProfileIdle

        $offlineExists = $state.OriginalBackupPath -and (Test-Path -LiteralPath ([string]$state.OriginalBackupPath) -PathType Container)
        if ($state.OriginalProfileExisted -and ($state.ProfileMoved -or $offlineExists)) {
            $offline = [IO.Path]::GetFullPath([string]$state.OriginalBackupPath).TrimEnd('\')
            if (-not (Test-Path -LiteralPath $offline -PathType Container)) {
                throw "Original offline profile backup is missing: $offline"
            }
            if ((Get-TreeFingerprint $offline).Hash -ne $state.OriginalProfileFingerprint.Hash) {
                throw "Original offline profile backup failed its hash check; it has not been altered."
            }
            if (Test-Path -LiteralPath $dataPath -PathType Container) {
                $currentProfile = Get-TreeFingerprint $dataPath
                if ($currentProfile.Hash -eq $state.OriginalProfileFingerprint.Hash) {
                    $null = New-Item -ItemType Directory -Path $dataPath -Force
                } else {
                $testProfileEvidence = Join-Path $testRootFull "test-profile-after-run\focus-desktop"
                if (Test-Path -LiteralPath $testProfileEvidence) { throw "Test-profile evidence destination already exists; refusing to overwrite it." }
                $null = New-Item -ItemType Directory -Path (Split-Path -Parent $testProfileEvidence) -Force
                Move-Item -LiteralPath $dataPath -Destination (Split-Path -Parent $testProfileEvidence)
                if ((Get-TreeFingerprint $testProfileEvidence).Exists -eq $false) {
                    throw "Test profile could not be preserved to evidence before restoration."
                }
                }
            }
            if (-not (Test-Path -LiteralPath $dataPath -PathType Container) -or
                (Get-TreeFingerprint $dataPath).Hash -ne $state.OriginalProfileFingerprint.Hash) {
                Copy-TreeWithoutDeleting $offline $dataPath (Join-Path $testRootFull "restore-profile-copy.log")
            }
            if ((Get-TreeFingerprint $dataPath).Hash -ne $state.OriginalProfileFingerprint.Hash) {
                throw "Restored user profile failed its aggregate hash check. Original offline backup remains at $offline"
            }
        } elseif (-not $state.OriginalProfileExisted -and (Test-Path -LiteralPath $dataPath -PathType Container)) {
            $testProfileEvidence = Join-Path $testRootFull "test-profile-after-run\focus-desktop"
            if (Test-Path -LiteralPath $testProfileEvidence) { throw "Test-profile evidence destination already exists; refusing to overwrite it." }
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $testProfileEvidence) -Force
            Move-Item -LiteralPath $dataPath -Destination (Split-Path -Parent $testProfileEvidence)
        } elseif ($state.OriginalProfileExisted) {
            $currentOriginal = Get-TreeFingerprint $dataPath
            if (-not $currentOriginal.Exists -or $currentOriginal.Hash -ne $state.OriginalProfileFingerprint.Hash) {
                throw "Original profile has no verified offline backup and is not unchanged at its original path; refusing to mark Restore complete."
            }
        }

        Restore-ShortcutSnapshot $state
        Assert-OriginalMachineInstallUnchanged $state
        $state.Status = "Restored"
        Write-State $state
        Write-Host "Original profile, shortcuts, and HKLM registration were restored/verified."
        Write-Host "Offline backup and test evidence are retained under $testRootFull; no evidence folder was deleted."
    }
}
