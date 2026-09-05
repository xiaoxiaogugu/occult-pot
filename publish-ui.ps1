$ErrorActionPreference = "Stop"

if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne "STA") {
    $self = $MyInvocation.MyCommand.Path
    Start-Process -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile", "-STA", "-WindowStyle", "Hidden",
        "-ExecutionPolicy", "Bypass", "-File", $self
    )
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

try {
    $dpi = Add-Type -PassThru -Name "DpiAware" -Namespace "PublishUi" -MemberDefinition @"
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
"@
    [void]$dpi::SetProcessDPIAware()
} catch {
}

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Set-Location -LiteralPath $root

function Get-CurrentVersion {
    $csproj = Join-Path $root "OccultPotPlugin.csproj"
    $text   = [IO.File]::ReadAllText($csproj)
    if ($text -notmatch "<Version>([^<]+)</Version>") {
        throw "csproj 里没有 Version"
    }
    $Matches[1].Trim()
}

function Get-NextVersion([string]$current) {
    if ($current -notmatch "^(\d+)\.(\d+)\.(\d+)$") {
        throw "当前版本不是 x.y.z：$current"
    }
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3] + 1
    "$major.$minor.$patch"
}

try {
    $currentVersion = Get-CurrentVersion
    $nextVersion    = Get-NextVersion $currentVersion
} catch {
    [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "发布")
    exit 1
}

$font     = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)
$fontLog  = New-Object System.Drawing.Font("Consolas", 9)
$fontBtn  = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, [System.Drawing.FontStyle]::Bold)

$form = New-Object System.Windows.Forms.Form
$form.Text            = "发布新月岛撒娇罐"
$form.StartPosition   = "CenterScreen"
$form.Size            = New-Object System.Drawing.Size(560, 620)
$form.MinimumSize     = New-Object System.Drawing.Size(480, 480)
$form.Font            = $font
$form.AutoScaleMode   = [System.Windows.Forms.AutoScaleMode]::Dpi
$form.AutoScaleDimensions = New-Object System.Drawing.SizeF(96, 96)

$lblCurrent = New-Object System.Windows.Forms.Label
$lblCurrent.Text     = "当前版本  $currentVersion"
$lblCurrent.Location = New-Object System.Drawing.Point(20, 18)
$lblCurrent.AutoSize = $true

$lblVer = New-Object System.Windows.Forms.Label
$lblVer.Text     = "版本号"
$lblVer.Location = New-Object System.Drawing.Point(20, 54)
$lblVer.AutoSize = $true

$txtVer = New-Object System.Windows.Forms.TextBox
$txtVer.Text     = $nextVersion
$txtVer.Location = New-Object System.Drawing.Point(90, 50)
$txtVer.Width    = 160
$txtVer.Anchor   = "Top,Left"

$lblNotes = New-Object System.Windows.Forms.Label
$lblNotes.Text     = "更新说明"
$lblNotes.Location = New-Object System.Drawing.Point(20, 90)
$lblNotes.AutoSize = $true

$txtNotes = New-Object System.Windows.Forms.TextBox
$txtNotes.Multiline  = $true
$txtNotes.ScrollBars = "Vertical"
$txtNotes.Location   = New-Object System.Drawing.Point(20, 114)
$txtNotes.Size       = New-Object System.Drawing.Size(504, 120)
$txtNotes.Anchor     = "Top,Left,Right"
$txtNotes.AcceptsReturn = $true

$btnPush = New-Object System.Windows.Forms.Button
$btnPush.Text     = "推送"
$btnPush.Font     = $fontBtn
$btnPush.Location = New-Object System.Drawing.Point(20, 248)
$btnPush.Size     = New-Object System.Drawing.Size(504, 40)
$btnPush.Anchor   = "Top,Left,Right"

$lblLog = New-Object System.Windows.Forms.Label
$lblLog.Text     = "日志"
$lblLog.Location = New-Object System.Drawing.Point(20, 300)
$lblLog.AutoSize = $true
$lblLog.Anchor   = "Top,Left"

$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Multiline  = $true
$txtLog.ScrollBars = "Both"
$txtLog.ReadOnly   = $true
$txtLog.WordWrap   = $false
$txtLog.Font       = $fontLog
$txtLog.Location   = New-Object System.Drawing.Point(20, 324)
$txtLog.Size       = New-Object System.Drawing.Size(504, 236)
$txtLog.Anchor     = "Top,Bottom,Left,Right"

$form.Controls.AddRange(@(
    $lblCurrent, $lblVer, $txtVer, $lblNotes, $txtNotes,
    $btnPush, $lblLog, $txtLog
))

$script:proc         = $null
$script:payloadPath  = $null
$script:launcherPath = $null
$script:logQueue     = New-Object "System.Collections.Concurrent.ConcurrentQueue[string]"
$script:doneFlag     = $false
$script:doneCode     = 0

function Append-Log([string]$line) {
    if ([string]::IsNullOrEmpty($line)) { return }
    $txtLog.AppendText($line + [Environment]::NewLine)
}

function Set-Busy([bool]$busy) {
    $txtVer.Enabled   = -not $busy
    $txtNotes.Enabled = -not $busy
    $btnPush.Enabled  = -not $busy
    $btnPush.Text     = $(if ($busy) { "推送中…" } else { "推送" })
}

function Clear-TempFiles {
    foreach ($p in @($script:payloadPath, $script:launcherPath)) {
        if ($p -and (Test-Path -LiteralPath $p)) {
            Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        }
    }
    $script:payloadPath  = $null
    $script:launcherPath = $null
}

function Finish-Publish([int]$code) {
    Set-Busy $false
    $script:proc = $null
    Clear-TempFiles
    if ($code -eq 0) {
        try {
            $now  = Get-CurrentVersion
            $next = Get-NextVersion $now
            $lblCurrent.Text = "当前版本  $now"
            $txtVer.Text     = $next
        } catch {
        }
        Append-Log "完成"
        [void][System.Windows.Forms.MessageBox]::Show(
            $form, "已发布", "发布",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        )
        return
    }
    Append-Log "失败，退出码 $code"
    [void][System.Windows.Forms.MessageBox]::Show(
        $form, "推送失败，看下面日志", "发布",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    )
}

function Start-Publish {
    $ver   = $txtVer.Text.Trim()
    $notes = $txtNotes.Text.Trim()

    if ([string]::IsNullOrWhiteSpace($notes)) {
        [void][System.Windows.Forms.MessageBox]::Show($form, "先写更新说明", "发布")
        return
    }
    if ($notes -match "(?i)cursor") {
        [void][System.Windows.Forms.MessageBox]::Show($form, "更新说明里不能出现 Cursor 字样", "发布")
        return
    }
    if ($ver -notmatch "^\d+\.\d+\.\d+$") {
        [void][System.Windows.Forms.MessageBox]::Show($form, "版本号格式应为 x.y.z，例如 2.1.2", "发布")
        return
    }

    $txtLog.Clear()
    $now = Get-CurrentVersion
    Append-Log "当前 $now → 发布 $ver"
    Set-Busy $true

    $stamp   = Get-Date -Format "yyyyMMddHHmmss"
    $payload = Join-Path $env:TEMP "occult-pot-publish-$stamp.json"
    $launch  = Join-Path $env:TEMP "occult-pot-publish-$stamp.ps1"
    $script:payloadPath  = $payload
    $script:launcherPath = $launch

    $json = @{
        Root    = $root
        Version = $ver
        Notes   = $notes
    } | ConvertTo-Json -Compress
    $utf8 = New-Object System.Text.UTF8Encoding $true
    [IO.File]::WriteAllText($payload, $json, $utf8)

    $launchText = @"
`$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
`$p = Get-Content -LiteralPath $($payload | ConvertTo-Json -Compress) -Raw -Encoding UTF8 | ConvertFrom-Json
Set-Location -LiteralPath `$p.Root
& (Join-Path `$p.Root "publish-github.ps1") -Version `$p.Version -Notes `$p.Notes
if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }
"@
    [IO.File]::WriteAllText($launch, $launchText, $utf8)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = "powershell.exe"
    $psi.Arguments              = "-NoProfile -ExecutionPolicy Bypass -File `"$launch`""
    $psi.WorkingDirectory       = $root
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.CreateNoWindow         = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding  = [Text.Encoding]::UTF8

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo          = $psi
    $proc.EnableRaisingEvents = $true
    $script:proc = $proc

    $onOut = [System.Diagnostics.DataReceivedEventHandler] {
        param($sender, $e)
        if ($null -eq $e.Data) { return }
        [void]$script:logQueue.Enqueue($e.Data)
    }
    $onExit = {
        $code = 1
        try { $code = $script:proc.ExitCode } catch { }
        $script:doneCode = $code
        $script:doneFlag = $true
    }

    $proc.add_OutputDataReceived($onOut)
    $proc.add_ErrorDataReceived($onOut)
    $proc.add_Exited($onExit)

    try {
        [void]$proc.Start()
        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()
    } catch {
        Set-Busy $false
        $script:proc = $null
        Clear-TempFiles
        Append-Log $_.Exception.Message
        [void][System.Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, "发布")
    }
}

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 200
$timer.Add_Tick({
    $line = $null
    while ($script:logQueue.TryDequeue([ref]$line)) {
        Append-Log $line
    }
    if (-not $script:doneFlag) { return }
    while ($script:logQueue.TryDequeue([ref]$line)) {
        Append-Log $line
    }
    $script:doneFlag = $false
    Finish-Publish $script:doneCode
})
$timer.Start()

$btnPush.Add_Click({ Start-Publish })

$form.Add_FormClosing({
    if ($script:proc -and -not $script:proc.HasExited) {
        $_.Cancel = $true
        [void][System.Windows.Forms.MessageBox]::Show($form, "还在推送，请等结束", "发布")
    }
})

$form.Add_FormClosed({
    $timer.Stop()
    Clear-TempFiles
})

[void]$form.ShowDialog()
