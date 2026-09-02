# build-release.ps1 — Public v1 一键构建：publish → portable.zip → Inno Setup → 双产物落 release/
# 用法：powershell -ExecutionPolicy Bypass -File installer/build-release.ps1 [-SkipInstaller]
# 路径策略（F5）：仓库根按脚本位置推导（任意 clone 位置可用）；dotnet 依次解析
#   env:DOTNET_ROOT → PATH → 常见安装位置；ISCC 走 PATH → 默认安装路径。
param([switch]$SkipInstaller)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot          # installer\ 的上一级 = 仓库根
$src = Join-Path $repo "src\focus-desktop"
$releaseDir = Join-Path $repo "release"
$portableDir = Join-Path $releaseDir "focus-desktop"
$payloadDir = Join-Path $repo "installer\payload"
$version = "1.0.1"

# --- dotnet 解析：DOTNET_ROOT → PATH → 常见安装位置（vs 纯写死 Kevin 本机路径） ---
$dotnetExe = $null
if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    $dotnetExe = Join-Path $env:DOTNET_ROOT "dotnet.exe"
} else {
    $candidates = @()
    $pathDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($pathDotnet) { $candidates += $pathDotnet.Source }
    $candidates += @(
        "$env:USERPROFILE\.dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    )
    $dotnetExe = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $dotnetExe) { throw "dotnet.exe not found (set DOTNET_ROOT or add dotnet to PATH)" }
$env:DOTNET_ROOT = Split-Path -Parent $dotnetExe

# --- ISCC 解析：PATH → 默认安装位置 ---
$iscc = $null
$pathIscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($pathIscc) { $iscc = $pathIscc.Source }
elseif (Test-Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") { $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" }
elseif (Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }

Write-Host "== 1/4 publish（自包含单文件 = portable 目录本体）==" -ForegroundColor Cyan
# 关键：IncludeNativeLibrariesForSelfExtract=true 把 WPF 本机库（PresentationNative/wpfgfx/D3DCompiler/PenImc）
# 打进单文件——不带此参数的"单文件"exe 落到干净目录会 DllNotFoundException（WPF SetWindowLongPtr 处崩），
# 只有旁边残留散装 DLL 时才能跑（portable 目录历史混合态掩盖过此 bug，T10 installed 首跑实测定罪）。
# 输出目录先清空（保留 focus-desktop-data 用户数据）：防旧散文件混入 zip/安装包，也防"混合态可跑"的假绿。
Get-ChildItem $portableDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike "*.sha256" -and $_.Name -ne "rel-smoke.txt" -and $_.Name -ne "portable.flag" } | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item "$portableDir\runtimes" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$portableDir\zh-Hans" -Recurse -Force -ErrorAction SilentlyContinue
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
& $dotnetExe publish (Join-Path $src "focus-desktop.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $portableDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Write-Host "   exe OK: $portableDir\focus-desktop.exe"

Write-Host "== 2/4 portable.flag + zip ==" -ForegroundColor Cyan
# portable.flag = 双布局分流标记（zip 用户解压即用，数据在 exe 旁）
Set-Content -Path (Join-Path $portableDir "portable.flag") -Value "" -Encoding ASCII
$zipPath = Join-Path $releaseDir "focus-desktop-portable-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$portableDir\focus-desktop.exe", "$portableDir\portable.flag" -DestinationPath $zipPath
Write-Host "   zip OK: $zipPath"

if ($SkipInstaller) { Write-Host "Done (installer skipped)."; exit 0 }

Write-Host "== 3/4 installer payload（无 portable.flag → LocalAppData 布局）==" -ForegroundColor Cyan
if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir | Out-Null
Copy-Item (Join-Path $portableDir "focus-desktop.exe") $payloadDir
$notices = Join-Path $repo "THIRD_PARTY_NOTICES.md"
if (Test-Path $notices) { Copy-Item $notices $payloadDir }
Write-Host "   payload OK"

Write-Host "== 4/4 Inno Setup 编译 ==" -ForegroundColor Cyan
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found: $iscc" }
& $iscc (Join-Path $repo "installer\focus-desktop.iss") "/DVersion=$version"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Write-Host "   installer OK: $releaseDir\FocusDesktop-Setup-$version.exe"
Write-Host "== DONE ==" -ForegroundColor Green
