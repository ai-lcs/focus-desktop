param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$installerPath = Join-Path $RepositoryRoot "installer\focus-desktop.iss"
$policyPath = Join-Path $RepositoryRoot "src\focus-desktop\Services\UninstallDataPolicy.cs"
$appPath = Join-Path $RepositoryRoot "src\focus-desktop\App.xaml.cs"
$testProject = Join-Path $RepositoryRoot "tests\UninstallDataPolicyTests\UninstallDataPolicyTests.csproj"
$installer = Get-Content -Raw -LiteralPath $installerPath
$policy = Get-Content -Raw -LiteralPath $policyPath
$app = Get-Content -Raw -LiteralPath $appPath
$testProgram = Get-Content -Raw -LiteralPath (Join-Path (Split-Path $testProject -Parent) "Program.cs")

function Assert-Contains([string]$Text, [string]$Pattern, [string]$Description) {
    if ($Text -notmatch $Pattern) { throw "Missing required safety check: $Description" }
}

function Assert-NotContains([string]$Text, [string]$Pattern, [string]$Description) {
    if ($Text -match $Pattern) { throw "Unsafe installer behavior remains: $Description" }
}

Assert-Contains $installer '#define AppName "Focus Desk"' "product name is Focus Desk"
Assert-NotContains $installer 'MB_YESNO|RemoveUserData\s*:=' "uninstall offers no keep-data choice"
Assert-Contains $installer 'AppId=\{\{50D19490-82AC-42D3-95B2-F8EBEA860817\}' "Dock has its own installation identity"
Assert-NotContains $installer 'AppId=\{\{7B3F9E2A' "legacy installation identity is never reused"
Assert-Contains $installer 'DefaultDirName=\{autopf\}\\FocusDesk\s' "new installations use the FocusDesk directory"
Assert-Contains $installer 'UsePreviousGroup=no' "upgrades use the new shortcut group name"
Assert-Contains $installer '\[InstallDelete\]' "obsolete Dock shortcuts are removed during upgrade"
Assert-Contains $installer 'function PrepareToInstall' "existing unmarked or portable installations are rejected"
Assert-Contains $installer 'dock-install.identity' "new installation carries its own marker"
[xml]$projectXml = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'src\focus-desktop\focus-desktop.csproj')
$expectedVersion = [regex]::Escape([string]$projectXml.Project.PropertyGroup.Version)
Assert-Contains $installer ('#define Version "' + $expectedVersion + '"') "Setup fallback version matches the application"
Assert-Contains $installer 'Parameters:\s*"--first-run-if-needed";\s*Flags: nowait runasoriginaluser' "first-run eligibility is evaluated after launch as the actual user"
Assert-NotContains $installer "ShouldLaunchFirstRun|LocalAppDataPath" "Setup does not guess the data owner from its elevated account"
Assert-Contains $app 'e.Args.Contains\("--first-run-if-needed"' "app processes the original-user first-run gate"
Assert-Contains $app "Directory.Exists\(Paths.DataDir\)" "the first-run gate checks the actual running user's data path"
$firstRunCheck = $app.IndexOf("--first-run-if-needed", [StringComparison]::Ordinal)
$desktopLock = $app.IndexOf("DesktopSessionCoordinator.TryAcquire", [StringComparison]::Ordinal)
if ($firstRunCheck -lt 0 -or $desktopLock -lt 0 -or $firstRunCheck -gt $desktopLock) {
    throw "The first-run data check must return before the app acquires desktop ownership."
}
Assert-Contains $installer "PrepareArguments\s*:=\s*'--prepare-uninstall --remove-user-data'" "every interactive and silent uninstall requests locked data cleanup"
Assert-Contains $installer "if\s+\(not\s+Executed\)\s+or\s+\(ResultCode\s*<>\s*0\)\s+then" "a failed helper blocks uninstall"
Assert-Contains $installer "Result\s*:=\s*False;\s*exit;" "helper failure returns an uninstall refusal"
Assert-NotContains $installer "CurUninstallStepChanged" "no post-helper uninstall callback may delete profile data"
Assert-NotContains $installer "DelTree\s*\(" "Inno never deletes profile data after the helper releases its locks"

Assert-Contains $policy "Environment\.SpecialFolder\.LocalApplicationData" "data path is derived from the current process profile"
Assert-Contains $policy "TryGetInteractiveSessionUserSid" "the interactive session identity is resolved"
Assert-Contains $policy "WtsConnectState\.Active" "disconnected or unknown sessions fail closed"
Assert-Contains $policy "WindowsIdentity\.GetCurrent\(\)" "the helper process token identity is checked"
Assert-Contains $policy "currentSid\.Equals\(sessionSid\)" "alternate administrator credentials are rejected"
Assert-Contains $policy "ownerSid\.Equals\(sessionSid\)" "the profile directory owner must match the interactive account"
Assert-Contains $policy "FileAttributes\.ReparsePoint" "reparse-point profile roots are rejected"
Assert-Contains $policy "portable\.flag" "portable data is not cleared by the installer helper"
Assert-Contains $policy "TryGetPathAttributes" "an inaccessible profile is not confused with an absent one"
Assert-Contains $policy "File\.GetAttributes\(path\)" "profile and portable-marker existence is inspected without Directory.Exists ambiguity"
Assert-NotContains $policy "Directory\.Exists\(dataDir\)" "profile inspection errors do not authorize a false successful clear"
Assert-Contains $policy "new NTAccount\(accountName\)\.Translate\(typeof\(SecurityIdentifier\)\)" "account lookup must resolve to a SID"
Assert-Contains $policy "internal static bool IsIdentityAuthorized\(" "the runtime identity decision is exposed to the narrow test harness"
Assert-Contains $testProgram "UninstallDataPolicy\.IsIdentityAuthorized\(" "tests invoke the production identity decision"

$dotnet = $null
$dotnetCandidates = @()
if ($env:USERPROFILE) { $dotnetCandidates += Join-Path $env:USERPROFILE ".dotnet\dotnet.exe" }
$pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if ($pathDotnet) { $dotnetCandidates += $pathDotnet }
foreach ($candidate in $dotnetCandidates) {
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    $null = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0) { $dotnet = $candidate; break }
}
if (-not $dotnet) { throw "dotnet was not found; the production policy matrix was not run." }

& $dotnet run --project $testProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Production uninstall policy matrix failed with exit code $LASTEXITCODE." }

Write-Output "Static installer checks passed. No product, installer, user profile, or desktop was opened."
