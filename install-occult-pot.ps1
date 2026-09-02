param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist\OccultPot"
$installedJson = Join-Path $root "OccultPot.installed.json"
$installedRoot = Join-Path $env:APPDATA "XIVLauncherCN\installedPlugins\OccultPot"

if (-not $SkipBuild) {
    $pkg = Join-Path $root "package-release.ps1"
    if (Test-Path $pkg) {
        & $pkg
    } else {
        dotnet build (Join-Path $root "OccultPotPlugin.csproj") -c Release
    }
}

$dllPath = Join-Path $dist "OccultPot.dll"
if (-not (Test-Path $dllPath)) {
    throw "Build output not found: $dllPath"
}

$manifest = Get-Content $installedJson -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$manifest.AssemblyVersion
$installedDir = Join-Path $installedRoot $version

$targets = New-Object System.Collections.Generic.List[string]
[void]$targets.Add($installedDir)
if (Test-Path $installedRoot) {
    Get-ChildItem $installedRoot -Directory | ForEach-Object { [void]$targets.Add($_.FullName) }
}

foreach ($dir in ($targets | Select-Object -Unique)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item (Join-Path $dist "*") $dir -Force
    Copy-Item $installedJson (Join-Path $dir "OccultPot.json") -Force
    Write-Host "Installed: $dir"
}

$devDir = Join-Path $env:APPDATA "XIVLauncherCN\devPlugins\OccultPot"
if (Test-Path $devDir) {
    Remove-Item $devDir -Recurse -Force
}

Write-Host ""
Write-Host "If /xlplugins already shows this version folder, disable then enable OccultPot."
Write-Host "If it still shows an older folder name, fully exit FFXIV and start again."
