param(
    [Parameter(Mandatory = $true)]
    [string]$Notes,

    [string]$Version,
    [switch]$SkipBuild,
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if ($Notes -match "(?i)cursor") {
    throw "更新说明里不能出现 Cursor 字样"
}

$csproj = Join-Path $root "OccultPotPlugin.csproj"
$csprojText = [IO.File]::ReadAllText($csproj)
if ($csprojText -notmatch "<Version>([^<]+)</Version>") {
    throw "csproj 里没有 Version"
}

$current = $Matches[1].Trim()
if ($current -notmatch "^(\d+)\.(\d+)\.(\d+)$") {
    throw "当前版本不是 x.y.z：$current"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3] + 1
    $Version = "$major.$minor.$patch"
}

$args = @{
    Version = $Version
    Notes   = $Notes
}
if ($SkipBuild) { $args.SkipBuild = $true }
if ($NoPush) { $args.NoPush = $true }

Write-Host "当前 $current → 发布 $Version"
& (Join-Path $root "publish-github.ps1") @args
if ($LASTEXITCODE -ne 0) {
    throw "发布失败"
}
