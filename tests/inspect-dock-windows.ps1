param([string]$OutFile)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "DockWindowSnapshot.cs"
Add-Type -Path $source
$snapshot = [DockWindowSnapshot]::Capture()
$json = $snapshot | ConvertTo-Json -Depth 8

if ($OutFile) {
    $destination = [IO.Path]::GetFullPath($OutFile)
    $parent = Split-Path -Parent $destination
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -LiteralPath $destination -Value $json -Encoding UTF8
    Write-Output "Snapshot written to $destination"
} else {
    Write-Output $json
}
