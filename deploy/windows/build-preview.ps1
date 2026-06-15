[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$DotNetPath = "$env:USERPROFILE\.dotnet\dotnet.exe"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$artifactsRoot = Join-Path $root "artifacts"
$publishRoot = Join-Path $artifactsRoot "publish\$Runtime"
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

if (Test-Path -LiteralPath $publishRoot) {
    $resolvedPublishRoot = (Resolve-Path -LiteralPath $publishRoot).Path
    $resolvedArtifactsRoot = (Resolve-Path -LiteralPath $artifactsRoot).Path
    if (-not $resolvedPublishRoot.StartsWith($resolvedArtifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean preview path outside artifacts: $resolvedPublishRoot"
    }
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

& $DotNetPath publish (Join-Path $root "src\NetRelay\NetRelay.csproj") `
    -c $Configuration -r $Runtime --self-contained false --no-restore -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "NetRelay preview publish failed with exit code $LASTEXITCODE"
}

& $DotNetPath publish (Join-Path $root "src\NetRelay.Updater\NetRelay.Updater.csproj") `
    -c $Configuration -r $Runtime --self-contained false --no-restore -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "NetRelay.Updater preview publish failed with exit code $LASTEXITCODE"
}

$gitCommit = (& git -c "safe.directory=$root" -C $root rev-parse HEAD).Trim()
$gitDirty = -not [string]::IsNullOrWhiteSpace((& git -c "safe.directory=$root" -C $root status --porcelain))
$hashLines = foreach ($file in @("NetRelay.exe", "NetRelay.dll", "NetRelay.Updater.exe", "NetRelay.Updater.dll")) {
    $path = Join-Path $publishRoot $file
    if (Test-Path -LiteralPath $path) {
        "$file SHA256=$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
}

@(
    "Product=NetRelay Development Preview"
    "ProductVersion=$productVersion"
    "Configuration=$Configuration"
    "Runtime=$Runtime"
    "SelfContained=false"
    "Commit=$gitCommit"
    "Dirty=$($gitDirty.ToString().ToLowerInvariant())"
    "BuildTimeUtc=$([DateTimeOffset]::UtcNow.ToString("O"))"
    "CodeSigned=false"
    $hashLines
) | Set-Content -LiteralPath (Join-Path $publishRoot "BUILD-INFO.txt") -Encoding UTF8

Get-Item -LiteralPath $publishRoot | Select-Object FullName, LastWriteTime
