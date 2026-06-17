param(
    [string]$Output = "artifacts/baota-portable"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$outputPath = Join-Path $root $Output
$appPath = Join-Path $outputPath "app"
$publicPath = Join-Path $outputPath "public"
$npmCachePath = Join-Path $root "artifacts\.npm-cache"
$dotnetCandidate = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (Test-Path -LiteralPath $dotnetCandidate) {
    $dotnetExecutable = $dotnetCandidate
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw "dotnet was not found in PATH or $dotnetCandidate."
    }
    $dotnetExecutable = $dotnetCommand.Source
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

New-Item -ItemType Directory -Path $appPath, $publicPath | Out-Null
New-Item -ItemType Directory -Path $npmCachePath -Force | Out-Null

& npm.cmd --cache $npmCachePath --prefix (Join-Path $root "website\admin") ci
if ($LASTEXITCODE -ne 0) {
    Start-Sleep -Seconds 2
    & npm.cmd --cache $npmCachePath --prefix (Join-Path $root "website\admin") ci
}
if ($LASTEXITCODE -ne 0) { throw "Admin dependency installation failed after retry." }
& npm.cmd --prefix (Join-Path $root "website\admin") run build
if ($LASTEXITCODE -ne 0) { throw "Admin production build failed." }

& $dotnetExecutable publish (Join-Path $root "server\NetRelay.Server\NetRelay.Server.csproj") `
    -c Release `
    -r linux-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $appPath
if ($LASTEXITCODE -ne 0) { throw "Server self-contained publish failed." }

Copy-Item (Join-Path $root "website\assets") $publicPath -Recurse
Copy-Item (Join-Path $root "website\index.html") $publicPath
Copy-Item (Join-Path $root "website\script.js") $publicPath
Copy-Item (Join-Path $root "website\styles.css") $publicPath
Copy-Item (Join-Path $root "website\admin\dist") (Join-Path $publicPath "admin") -Recurse
Copy-Item (Join-Path $PSScriptRoot "install.sh") $outputPath
Copy-Item (Join-Path $PSScriptRoot "netrelay.service") $outputPath
Copy-Item (Join-Path $PSScriptRoot "netrelay-menu.sh") $outputPath
Copy-Item (Join-Path $PSScriptRoot "nginx-location.conf") $outputPath
Copy-Item (Join-Path $PSScriptRoot "README.md") $outputPath

$commit = git -c safe.directory=$($root.Path.Replace('\','/')) rev-parse HEAD
$dirty = if (git -c safe.directory=$($root.Path.Replace('\','/')) status --porcelain) { "true" } else { "false" }
@"
commit=$commit
dirty=$dirty
builtAt=$([DateTimeOffset]::UtcNow.ToString("O"))
runtime=linux-x64-self-contained
"@ | Set-Content -LiteralPath (Join-Path $outputPath "BUILD.txt") -Encoding UTF8

$archive = "$outputPath.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $outputPath "*") -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Baota portable package: $archive"
