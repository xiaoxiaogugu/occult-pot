param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Notes,

    [switch]$SkipBuild,
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if ($Version -notmatch "^\d+\.\d+\.\d+$") {
    throw "版本号格式应为 x.y.z，例如 2.1.0"
}

$assembly = "$Version.0"
$unixNow = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

function Set-FileText([string]$path, [string]$old, [string]$new) {
    $text = [IO.File]::ReadAllText($path)
    if (-not $text.Contains($old)) {
        return
    }
    [IO.File]::WriteAllText($path, $text.Replace($old, $new))
}

$csproj = Join-Path $root "OccultPotPlugin.csproj"
$csprojText = [IO.File]::ReadAllText($csproj)
$csprojText = [regex]::Replace($csprojText, "<Version>[^<]+</Version>", "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $csprojText)

foreach ($jsonPath in @("OccultPot.json", "pluginmaster.json")) {
    $full = Join-Path $root $jsonPath
    $jsonText = [IO.File]::ReadAllText($full)
    $jsonText = [regex]::Replace($jsonText, '"AssemblyVersion":\s*"[^"]+"', "`"AssemblyVersion`": `"$assembly`"")
    if ($jsonPath -eq "pluginmaster.json") {
        $jsonText = [regex]::Replace($jsonText, '"LastUpdate":\s*"[^"]+"', "`"LastUpdate`": `"$unixNow`"")
    }
    [IO.File]::WriteAllText($full, $jsonText)
}

$installed = Join-Path $root "OccultPot.installed.json"
if (Test-Path $installed) {
    $instText = [IO.File]::ReadAllText($installed)
    $instText = [regex]::Replace($instText, '"AssemblyVersion":\s*"[^"]+"', "`"AssemblyVersion`": `"$assembly`"")
    $instText = [regex]::Replace($instText, '"EffectiveVersion":\s*"[^"]+"', "`"EffectiveVersion`": `"$assembly`"")
    [IO.File]::WriteAllText($installed, $instText)
}

if (-not $SkipBuild) {
    & (Join-Path $root "package-release.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "打包失败"
    }
}

$zip = Join-Path $root "plugins\OccultPot\latest.zip"
if (-not (Test-Path $zip)) {
    throw "缺少 $zip"
}

git add -u -- ":(exclude).cursor" ":(exclude)*.user"
git add -- OccultPotPlugin.csproj OccultPot.json pluginmaster.json package-release.ps1 publish-github.ps1 publish-update.ps1 publish-ui.ps1 publish.bat plugins/OccultPot/latest.zip

$staged = git diff --cached --name-only
if (-not $staged) {
    throw "没有可提交的更改"
}

$bad = $staged | Where-Object { $_ -match "(?i)cursor|agent-transcript|\.cursor/" }
if ($bad) {
    git reset HEAD -- $bad
    throw "已拦下 Cursor 相关文件：$bad"
}

$message = "发布 ${Version}：$Notes"
git commit -m $message
if ($LASTEXITCODE -ne 0) {
    throw "提交失败"
}

$log = git log -1 --format=%B
if ($log -match "(?i)cursor") {
    git reset --soft HEAD~1
    throw "提交说明里出现了 Cursor 字样，已撤回。请只用本脚本提交。"
}

if (-not $NoPush) {
    git push origin HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "推送失败"
    }
}

Write-Host "已发布 $Version"
Write-Host $message
Write-Host "仓库：https://github.com/xiaoxiaogugu/occult-pot"
Write-Host "插件库：https://raw.githubusercontent.com/xiaoxiaogugu/occult-pot/main/pluginmaster.json"
