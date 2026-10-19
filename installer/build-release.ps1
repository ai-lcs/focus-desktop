# Build the exact installer and portable ZIP into a fresh, isolated candidate directory.
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo "src\focus-desktop"
$project = Join-Path $src "focus-desktop.csproj"
$notices = Join-Path $repo "THIRD_PARTY_NOTICES.md"
$iss = Join-Path $repo "installer\focus-desktop.iss"

[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid project version: $version" }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo "release\$version-candidate"
}
$outputDir = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$outputParent = Split-Path -Parent $outputDir
$portableName = "focus-desktop-portable-$version.zip"
$setupName = "FocusDesktop-Setup-$version.exe"
$hashName = "v$version-sha256.txt"

if (-not (Test-Path -LiteralPath $notices -PathType Leaf)) { throw "Required notices file is missing: $notices" }
if (-not $SkipInstaller -and -not (Test-Path -LiteralPath $iss -PathType Leaf)) { throw "Inno Setup source is missing: $iss" }
if (Test-Path -LiteralPath $outputDir) { throw "OutputDirectory must be a new directory; refusing to mix or replace existing contents: $outputDir" }

$dotnetExe = $null
if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    $dotnetExe = Join-Path $env:DOTNET_ROOT "dotnet.exe"
} else {
    $candidates = @()
    $pathDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($pathDotnet) { $candidates += $pathDotnet.Source }
    $candidates += @(
        (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"),
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe")
    )
    $dotnetExe = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $dotnetExe) { throw "dotnet.exe not found (set DOTNET_ROOT or add dotnet to PATH)" }

$iscc = $null
if (-not $SkipInstaller) {
    $pathIscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($pathIscc) { $iscc = $pathIscc.Source }
    elseif (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")) {
        $iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
    }
    elseif (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")) {
        $iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    }
    if (-not $iscc) { throw "ISCC.exe not found" }
}

$workDir = Join-Path $outputParent ".build-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $outputParent -Force
if (Test-Path -LiteralPath $workDir) { throw "Refusing to reuse temporary build directory: $workDir" }
$publishDir = Join-Path $workDir "publish"
$portableStage = Join-Path $workDir "portable"
$payloadDir = Join-Path $workDir "payload"
$previousDotnetRoot = $env:DOTNET_ROOT
$previousTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$previousNoLogo = $env:DOTNET_NOLOGO

try {
    $null = New-Item -ItemType Directory -Path $publishDir, $portableStage, $payloadDir
    $env:DOTNET_ROOT = Split-Path -Parent $dotnetExe
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"

    Write-Host "== Publish Focus Desk $version (isolated scratch) ==" -ForegroundColor Cyan
    # Keep this enabled: WPF native libraries must be embedded in the single-file app.
    & $dotnetExe publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $publishedExe = Join-Path $publishDir "focus-desktop.exe"
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { throw "Published executable is missing: $publishedExe" }

    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $portableStage "focus-desktop.exe")
    Copy-Item -LiteralPath $notices -Destination (Join-Path $portableStage "THIRD_PARTY_NOTICES.md")
    $null = New-Item -ItemType File -Path (Join-Path $portableStage "portable.flag")
    if ((Get-FileHash -LiteralPath (Join-Path $portableStage "focus-desktop.exe") -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash) {
        throw "Portable executable differs from the exact published binary."
    }

    $zipStage = Join-Path $workDir $portableName
    Compress-Archive -LiteralPath @(
        (Join-Path $portableStage "focus-desktop.exe"),
        (Join-Path $portableStage "portable.flag"),
        (Join-Path $portableStage "THIRD_PARTY_NOTICES.md")
    ) -DestinationPath $zipStage -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipStage)
    try {
        $actualEntries = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        $expectedEntries = @("THIRD_PARTY_NOTICES.md", "focus-desktop.exe", "portable.flag") | Sort-Object
        if (Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries) {
            throw "Portable ZIP entries differ from the three expected files."
        }
    } finally {
        $archive.Dispose()
    }

    if (-not $SkipInstaller) {
        Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $payloadDir "focus-desktop.exe")
        Copy-Item -LiteralPath $notices -Destination (Join-Path $payloadDir "THIRD_PARTY_NOTICES.md")
        $payloadNames = @(Get-ChildItem -LiteralPath $payloadDir -Force | ForEach-Object { $_.Name } | Sort-Object)
        $expectedPayloadNames = @("focus-desktop.exe", "THIRD_PARTY_NOTICES.md") | Sort-Object
        if (Compare-Object -ReferenceObject $expectedPayloadNames -DifferenceObject $payloadNames) {
            throw "Installer payload contains files other than the executable and notices."
        }
        if ((Get-FileHash -LiteralPath (Join-Path $payloadDir "focus-desktop.exe") -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash) {
            throw "Installer executable differs from the exact published binary."
        }
        $definePayload = "/DPayloadDir=$payloadDir"
        Write-Host "== Compile Inno Setup installer ==" -ForegroundColor Cyan
        & $iscc $iss "/DVersion=$version" $definePayload "/O$workDir"
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
        $setupOutput = Join-Path $workDir $setupName
        if (-not (Test-Path -LiteralPath $setupOutput -PathType Leaf)) { throw "Installer output is missing: $setupOutput" }
    }

    $stagedSetupPath = Join-Path $workDir $setupName
    if (-not $SkipInstaller -and -not (Test-Path -LiteralPath $stagedSetupPath -PathType Leaf)) {
        throw "Staged installer output is missing: $stagedSetupPath"
    }
    $artifacts = @($zipStage)
    if (-not $SkipInstaller) { $artifacts += $stagedSetupPath }
    $hashLines = foreach ($artifact in $artifacts) {
        $digest = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $(Split-Path -Leaf $artifact)"
    }
    Set-Content -LiteralPath (Join-Path $workDir $hashName) -Value $hashLines -Encoding ASCII

    $fullWorkForCleanup = [IO.Path]::GetFullPath($workDir).TrimEnd('\')
    $cleanupTargets = @(
        @{ Actual = [IO.Path]::GetFullPath($publishDir).TrimEnd('\'); Expected = (Join-Path $fullWorkForCleanup 'publish') },
        @{ Actual = [IO.Path]::GetFullPath($portableStage).TrimEnd('\'); Expected = (Join-Path $fullWorkForCleanup 'portable') },
        @{ Actual = [IO.Path]::GetFullPath($payloadDir).TrimEnd('\'); Expected = (Join-Path $fullWorkForCleanup 'payload') }
    )
    foreach ($target in $cleanupTargets) {
        $expectedPath = [IO.Path]::GetFullPath($target.Expected).TrimEnd('\')
        if (-not $target.Actual.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing scratch cleanup because a resolved path differs from its fixed work-directory child: $($target.Actual)"
        }
    }
    Remove-Item -LiteralPath @($cleanupTargets | ForEach-Object { $_.Actual }) -Recurse -Force
    $stagedNames = @(Get-ChildItem -LiteralPath $workDir -Force | ForEach-Object { $_.Name } | Sort-Object)
    $expectedNames = @($portableName, $hashName)
    if (-not $SkipInstaller) { $expectedNames += $setupName }
    $expectedNames = @($expectedNames | Sort-Object)
    if (Compare-Object -ReferenceObject $expectedNames -DifferenceObject $stagedNames) {
        throw "Candidate output contains files other than the exact expected artifacts."
    }

    Write-Host "== Candidate artifacts ==" -ForegroundColor Green
    foreach ($artifact in $artifacts) {
        $digest = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "$digest  $(Split-Path -Leaf $artifact)"
    }
    Write-Host "SHA-256 list: $hashName"
    if (Test-Path -LiteralPath $outputDir) { throw "OutputDirectory appeared during the build; candidate remains staged: $workDir" }
    [IO.Directory]::Move($workDir, $outputDir)
    Write-Host "Candidate output: $outputDir"
} finally {
    if (Test-Path -LiteralPath $workDir) {
        $expectedPrefix = $outputParent.TrimEnd('\') + '\.build-'
        $fullWork = [IO.Path]::GetFullPath($workDir)
        if ($fullWork.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $fullWork -Recurse -Force
        } else {
            Write-Warning "Scratch path escaped the selected output directory; preserved it: $fullWork"
        }
    }
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetry
    $env:DOTNET_NOLOGO = $previousNoLogo
}
