#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads and installs a NwsAlertBot release, then restarts it.

.DESCRIPTION
    Detects the current OS/architecture, downloads the matching release archive from GitHub,
    and replaces the running executable with it -- WITHOUT touching appsettings.json,
    appsettings.Local.json, or any runtime state files (posted_alerts.txt,
    confirmed_platforms.txt, logs/, x_post_count.txt, twilio_sms_count.txt) already in the
    install directory. Only the executable itself (and this script, so future updater fixes
    apply too) are replaced.

    Normally launched automatically by UpdateCheckService when Update.AutoApply is true in
    appsettings.json, passing -WaitForPid so it waits for the running bot to exit before
    swapping the binary. Can also be run manually (omit -WaitForPid) to upgrade by hand when
    Update.AutoApply is false -- pass -Tag with the version you want, e.g. "v1.2.3".

    Restart behavior: if a systemd service (Linux) or Windows Service matching -ServiceName is
    found, it's restarted via systemctl/Restart-Service. Otherwise the new executable is simply
    launched directly -- covers the common "just run the .exe" case with no service installed.

.PARAMETER Repo
    GitHub "owner/repo" to download the release from.

.PARAMETER Tag
    Release tag to install, e.g. "v1.2.3".

.PARAMETER InstallDir
    Directory containing the current executable (and appsettings.json). Defaults to this
    script's own directory, since it's expected to live alongside the executable.

.PARAMETER WaitForPid
    Process ID to wait for before touching any files -- set automatically by UpdateCheckService
    to the bot's own process ID so the executable isn't locked/in-use during the swap.

.PARAMETER ServiceName
    Name of a systemd (Linux) or Windows Service to restart after installing, if one exists.
    Defaults to "nwsalertbot" to match this project's own deploy.yml.

.PARAMETER DryRun
    Detects the platform, resolves the download URL, and reports what would happen without
    downloading, installing, or restarting anything. Use this to safely verify the script works
    on your machine before letting UpdateCheckService run it for real.

.EXAMPLE
    ./update.ps1 -Repo MikeWills/NwsAlertBot -Tag v1.2.3 -DryRun
#>
param(
    [string]$Repo = "MikeWills/NwsAlertBot",
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$InstallDir = $PSScriptRoot,
    [int]$WaitForPid = 0,
    [string]$ServiceName = "nwsalertbot",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Write-Step($message) {
    Write-Host "[update] $message"
}

# --- 1. Wait for the running process to exit, if one was specified ---------------------------
if ($WaitForPid -gt 0) {
    Write-Step "Waiting for process $WaitForPid to exit..."
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Process -Id $WaitForPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
    }
    if (Get-Process -Id $WaitForPid -ErrorAction SilentlyContinue) {
        Write-Error "Process $WaitForPid did not exit within 60s; aborting update."
        exit 1
    }
    Write-Step "Process $WaitForPid has exited."
}

# --- 2. Detect platform and pick the matching release asset -----------------------------------
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($IsWindows) {
    $rid = "win-x64"
    $archiveExt = "zip"
    $exeName = "NwsAlertBot.exe"
}
elseif ($IsMacOS) {
    $rid = if ($arch -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "osx-arm64" } else { "osx-x64" }
    $archiveExt = "tar.gz"
    $exeName = "NwsAlertBot"
}
elseif ($IsLinux) {
    $rid = "linux-x64"
    $archiveExt = "tar.gz"
    $exeName = "NwsAlertBot"
}
else {
    Write-Error "Unrecognized platform -- IsWindows/IsMacOS/IsLinux are all false. Are you running PowerShell 6+ (pwsh), not Windows PowerShell 5.1?"
    exit 1
}

$assetName = "NwsAlertBot-$rid.$archiveExt"
$downloadUrl = "https://github.com/$Repo/releases/download/$Tag/$assetName"

Write-Step "Platform: $rid | Asset: $assetName"
Write-Step "Download URL: $downloadUrl"

if ($DryRun) {
    Write-Step "-DryRun set -- will download and extract to verify the release/asset are valid, but will not touch $InstallDir or restart anything."
}

# --- 3. Download and extract to a temp directory ------------------------------------------------
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "nwsalertbot-update-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $tempDir | Out-Null
$archivePath = Join-Path $tempDir $assetName

try {
    Write-Step "Downloading..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing

    $extractDir = Join-Path $tempDir "extracted"
    New-Item -ItemType Directory -Path $extractDir | Out-Null

    Write-Step "Extracting..."
    if ($archiveExt -eq "zip") {
        Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force
    }
    else {
        # tar is present on Linux/macOS natively and on Windows 10 1803+ (bsdtar) -- avoids
        # needing a separate module just for .tar.gz.
        tar -xzf $archivePath -C $extractDir
    }

    $newExePath = Join-Path $extractDir $exeName
    if (-not (Test-Path $newExePath)) {
        Write-Error "Expected executable not found in downloaded archive: $newExePath"
        exit 1
    }

    if ($DryRun) {
        Write-Step "Download and extraction succeeded; found $exeName in the archive as expected."
        Write-Step "-DryRun set -- stopping here without installing to $InstallDir or restarting anything."
        exit 0
    }

    # --- 4. Replace ONLY the executable (and this script) -- never appsettings.json/appsettings.Local.json
    #        or any runtime state file (posted_alerts.txt, confirmed_platforms.txt, logs/,
    #        x_post_count.txt, twilio_sms_count.txt). Those belong to this install, not the release.
    $currentExePath = Join-Path $InstallDir $exeName
    if (Test-Path $currentExePath) {
        $backupPath = "$currentExePath.bak"
        Write-Step "Backing up current executable to $backupPath"
        Copy-Item -Path $currentExePath -Destination $backupPath -Force
    }

    Write-Step "Installing new executable to $currentExePath"
    Copy-Item -Path $newExePath -Destination $currentExePath -Force
    if (-not $IsWindows) {
        chmod +x $currentExePath
    }

    $newScriptPath = Join-Path $extractDir "update.ps1"
    if (Test-Path $newScriptPath) {
        Write-Step "Updating update.ps1 itself for next time"
        Copy-Item -Path $newScriptPath -Destination (Join-Path $InstallDir "update.ps1") -Force
    }
}
finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 5. Restart --------------------------------------------------------------------------------
$restarted = $false

if ($IsLinux) {
    $unitExists = (systemctl list-unit-files "$ServiceName.service" 2>$null | Select-String $ServiceName)
    if ($unitExists) {
        Write-Step "Restarting systemd service '$ServiceName'..."
        sudo systemctl restart $ServiceName
        $restarted = $true
    }
}
elseif ($IsWindows) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Step "Restarting Windows Service '$ServiceName'..."
        Restart-Service -Name $ServiceName -Force
        $restarted = $true
    }
}

if (-not $restarted) {
    Write-Step "No matching service found -- launching the executable directly."
    Start-Process -FilePath $currentExePath -WorkingDirectory $InstallDir
}

Write-Step "Update to $Tag complete."
