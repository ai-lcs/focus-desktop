param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$appSource = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot "tests\fixtures\DockTestApp\App.xaml.cs")
$windowSource = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot "tests\fixtures\DockTestApp\FixtureWindow.cs")

function Assert-Contains([string]$Text, [string]$Pattern, [string]$Description) {
    if ($Text -notmatch $Pattern) { throw "Missing late-window fixture behavior: $Description" }
}

Assert-Contains $appSource "--fixture-start-delay-ms" "process/window creation can be delayed"
Assert-Contains $appSource "--fixture-start-minimized" "the previous minimized case remains available"
Assert-Contains $appSource "--fixture-activate-after-show-delay-ms" "the fixture can request delayed self-activation"
Assert-Contains $appSource "new FixtureWindow\(_options, i\)" "default startup creates an ordinary fixture window"
Assert-Contains $appSource "window\.Show\(\)" "default startup uses a normal WPF Show"
Assert-Contains $windowSource 'Activated\s*\+=\s*\(_, _\)\s*=>\s*RecordEvent\("Activated"\)' "WPF activation events are logged"
Assert-Contains $windowSource "DelayedActivateRequested" "fixture-owned delayed activation requests are distinguished"
Assert-Contains $windowSource "GetForegroundWindow\(\)" "foreground HWND is captured with the fixture event"
Assert-Contains $windowSource "GetWindowThreadProcessId\(foreground, out var foregroundProcessId\)" "foreground PID is captured with the fixture event"
Assert-Contains $windowSource "IsMinimized" "normal and minimized states are recorded per event"
Assert-Contains $windowSource "events\.jsonl" "event observations are written to a disposable per-window log"

Write-Output "Late-window fixture static checks passed. Existing minimized behavior remains optional; normal Show is the default."
