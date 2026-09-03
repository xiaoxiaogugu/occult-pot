param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsRoot = Split-Path -Parent $root
$distPlugin = Join-Path $root "dist\OccultPot"
$localTest = Join-Path $toolsRoot "OccultPot-refactor-out"
$zipPath = Join-Path $toolsRoot "OccultPot-v2.0.4.zip"
$zipLatest = Join-Path $root "dist\latest.zip"

dotnet build (Join-Path $root "OccultPotPlugin.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$required = @("OccultPot.dll", "OccultPot.json", "OmenTools.dll", "GuerrillaNtp.dll", "TinyPinyin.dll")
foreach ($file in $required) {
    $path = Join-Path $distPlugin $file
    if (-not (Test-Path $path)) {
        throw "Missing release file: $path"
    }
}

New-Item -ItemType Directory -Force -Path $localTest | Out-Null
Get-ChildItem $localTest -File -ErrorAction SilentlyContinue | Remove-Item -Force
Copy-Item (Join-Path $distPlugin "*") -Destination $localTest -Force

$pluginZipDir = Join-Path $root "plugins\OccultPot"
New-Item -ItemType Directory -Force -Path $pluginZipDir | Out-Null
$pluginZip = Join-Path $pluginZipDir "latest.zip"

$localZip = Join-Path $toolsRoot "OccultPot-refactor-out.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (Test-Path $zipLatest) { Remove-Item $zipLatest -Force }
if (Test-Path $localZip) { Remove-Item $localZip -Force }
if (Test-Path $pluginZip) { Remove-Item $pluginZip -Force }

Compress-Archive -Path (Join-Path $distPlugin "*") -DestinationPath $zipPath -Force
Compress-Archive -Path (Join-Path $distPlugin "*") -DestinationPath $zipLatest -Force
Compress-Archive -Path (Join-Path $distPlugin "*") -DestinationPath $pluginZip -Force
Compress-Archive -Path (Join-Path $localTest "*") -DestinationPath $localZip -Force
Write-Host "Package: $localTest"
Write-Host "Zip: $zipPath"
Write-Host "Repo zip: $pluginZip"
