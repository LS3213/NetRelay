[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$DotNetPath = "$env:USERPROFILE\.dotnet\dotnet.exe",
    [string]$InnoSetupPath = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$deliveryRoot = Join-Path $root "artifacts\delivery\$Runtime"
$publishRoot = Join-Path $deliveryRoot "publish"
$installerRoot = Join-Path $deliveryRoot "installer"
$updateZip = Join-Path $deliveryRoot "$Runtime.zip"
$protocolPath = Join-Path $root "src\NetRelay.Contracts\Protocol.cs"

if (-not (Test-Path -LiteralPath $DotNetPath)) {
    throw "dotnet executable not found: $DotNetPath"
}

$protocolSource = Get-Content -LiteralPath $protocolPath -Raw -Encoding UTF8
$versionMatch = [regex]::Match($protocolSource, 'ProductVersion\s*=\s*"([^"]+)"')
if (-not $versionMatch.Success) {
    throw "Unable to read ProductVersion from $protocolPath"
}
$productVersion = $versionMatch.Groups[1].Value

if (Test-Path -LiteralPath $deliveryRoot) {
    $resolvedDeliveryRoot = (Resolve-Path -LiteralPath $deliveryRoot).Path
    $resolvedArtifactsRoot = (Resolve-Path -LiteralPath (Join-Path $root "artifacts")).Path
    if (-not $resolvedDeliveryRoot.StartsWith($resolvedArtifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean delivery path outside artifacts: $resolvedDeliveryRoot"
    }
    Remove-Item -LiteralPath $deliveryRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot, $installerRoot -Force | Out-Null

& $DotNetPath publish (Join-Path $root "src\NetRelay\NetRelay.csproj") `
    -c $Configuration -r $Runtime --self-contained false --no-restore -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "NetRelay publish failed with exit code $LASTEXITCODE"
}

& $DotNetPath publish (Join-Path $root "src\NetRelay.Updater\NetRelay.Updater.csproj") `
    -c $Configuration -r $Runtime --self-contained false --no-restore -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "NetRelay.Updater publish failed with exit code $LASTEXITCODE"
}

$gitCommit = (& git -c "safe.directory=$root" -C $root rev-parse HEAD).Trim()
$gitDirty = -not [string]::IsNullOrWhiteSpace((& git -c "safe.directory=$root" -C $root status --porcelain))
$buildTime = [DateTimeOffset]::UtcNow.ToString("O")
$trackedFiles = @("NetRelay.exe", "NetRelay.dll", "NetRelay.Updater.exe", "NetRelay.Updater.dll")
$hashLines = foreach ($file in $trackedFiles) {
    $path = Join-Path $publishRoot $file
    if (Test-Path -LiteralPath $path) {
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$file SHA256=$hash"
    }
}

@(
    "Product=NetRelay"
    "ProductVersion=$productVersion"
    "Configuration=$Configuration"
    "Runtime=$Runtime"
    "SelfContained=false"
    "Commit=$gitCommit"
    "Dirty=$($gitDirty.ToString().ToLowerInvariant())"
    "BuildTimeUtc=$buildTime"
    "CodeSigned=false"
    $hashLines
) | Set-Content -LiteralPath (Join-Path $publishRoot "BUILD-INFO.txt") -Encoding UTF8

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $updateZip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $isccCandidates = @(
        $InnoSetupPath,
        "D:\Inno Setup 6\ISCC.exe",
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup 6 was not found. Install it or run with -SkipInstaller to build publish files and the update ZIP only."
    }

    & $iscc `
        "/DAppVersion=$productVersion" `
        "/DSourceDir=$publishRoot" `
        "/DOutputDir=$installerRoot" `
        (Join-Path $PSScriptRoot "NetRelay.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
    }
}

Get-Item -LiteralPath $publishRoot, $updateZip | Select-Object FullName, Length, LastWriteTime
if (-not $SkipInstaller) {
    Get-Item -LiteralPath (Join-Path $installerRoot "NetRelaySetup.exe") | Select-Object FullName, Length, LastWriteTime
}
