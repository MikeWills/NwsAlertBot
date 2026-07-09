#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads and installs a NwsAlertBot release, then restarts it.

.DESCRIPTION
    Detects the current OS/architecture, downloads the matching release archive from GitHub,
    verifies its SHA256 checksum against the release's checksums.txt (aborting if it's missing or
    doesn't match), and replaces the running executable with it -- WITHOUT touching appsettings.json,
    appsettings.Local.json, or any runtime state files (posted_alerts.txt,
    confirmed_platforms.txt, logs/, x_post_count.txt, twilio_sms_count.txt) already in the
    install directory. Only the executable itself (and this script, so future updater fixes
    apply too) are replaced.

    Normally launched automatically by UpdateCheckService when Update.AutoApply is true in
    appsettings.json, passing -WaitForPid (so it waits for the running bot to exit before
    swapping the binary), -Tag (the latest release tag, already resolved from GitHub's API on the
    C# side), and -ServiceName (read from Update.ServiceName, so this always matches without
    relying on anyone keeping two separately-typed values in sync). Can also be run manually (omit
    -WaitForPid) to upgrade by hand when Update.AutoApply is false -- omit -Tag to install
    whatever is currently the latest release (resolved the same way UpdateCheckService does, via
    GitHub's releases/latest API), or pass -Tag with a specific version, e.g. "v1.2.3", to pin to
    that release instead. -ServiceName can still be omitted, since it's resolved from
    Update.ServiceName the same way if not passed explicitly.

    Restart behavior: if a systemd service (Linux) or Windows Service matching -ServiceName is
    found, it's restarted via systemctl/Restart-Service. Otherwise the new executable is simply
    launched directly -- covers the common "just run the .exe" case with no service installed.

    After restarting, waits -RollbackCheckDelaySeconds and checks whether the new version is
    still running. If not (crashed, won't start), automatically restores the previous executable
    from its .bak backup and restarts with that instead, so a bad release doesn't leave the bot
    down indefinitely with no recovery.

.PARAMETER Repo
    GitHub "owner/repo" to download the release from.

.PARAMETER Tag
    Release tag to install, e.g. "v1.2.3". If omitted, resolves and installs the latest release
    automatically via GitHub's releases/latest API -- pass this only to pin to a specific version.

.PARAMETER InstallDir
    Directory containing the current executable (and appsettings.json). Defaults to this
    script's own directory, since it's expected to live alongside the executable.

.PARAMETER WaitForPid
    Process ID to wait for before touching any files -- set automatically by UpdateCheckService
    to the bot's own process ID so the executable isn't locked/in-use during the swap.

.PARAMETER ServiceName
    Name of a systemd (Linux) or Windows Service to restart after installing, if one exists. If
    omitted, resolved from Update.ServiceName in appsettings.Local.json (checked first) or
    appsettings.json in -InstallDir, falling back to "nwsalertbot" if neither sets it.

.PARAMETER DryRun
    Detects the platform, resolves the download URL, downloads and checksum-verifies the release,
    and extracts it to confirm the archive is valid -- without installing to -InstallDir or
    restarting anything. Use this to safely verify the script works on your machine before
    letting UpdateCheckService run it for real.

.PARAMETER RollbackCheckDelaySeconds
    After starting the new version, how long to wait before checking that it's still
    running/active. If it isn't (crashed, failed to start), the previous executable is
    automatically restored from its .bak backup and restarted. Default 15s -- long enough that a
    startup crash has already happened, short enough not to noticeably delay a healthy update.

.EXAMPLE
    ./update.ps1 -DryRun

.EXAMPLE
    ./update.ps1 -Repo MikeWills/NwsAlertBot -Tag v1.2.3 -DryRun
#>
param(
    [string]$Repo = "MikeWills/NwsAlertBot",
    [string]$Tag,
    [string]$InstallDir = $PSScriptRoot,
    [int]$WaitForPid = 0,
    [string]$ServiceName,
    [switch]$DryRun,
    [int]$RollbackCheckDelaySeconds = 15
)

$ErrorActionPreference = "Stop"

function Write-Step($message) {
    Write-Host "[update] $message"
}

# Resolves the service name from Update.ServiceName in appsettings.Local.json (checked first,
# matching the app's own override precedence) or appsettings.json in $dir, falling back to
# $fallback if neither file exists, is unreadable, or doesn't set it. Kept in sync with the
# identical function in setup-service.ps1 (duplicated rather than shared, since each script is
# meant to run standalone with nothing else to dot-source).
function Resolve-ServiceName([string]$dir, [string]$fallback) {
    foreach ($file in @("appsettings.Local.json", "appsettings.json")) {
        $path = Join-Path $dir $file
        if (-not (Test-Path $path)) { continue }
        try {
            $raw = Get-Content -Path $path -Raw
            $lines = $raw -split "`r?`n" | Where-Object { $_.TrimStart() -notmatch '^//' }
            $stripped = ($lines -join "`n") -replace ',(\s*[\]\}])', '$1'
            $config = $stripped | ConvertFrom-Json
            if ($config.Update -and $config.Update.ServiceName) {
                return $config.Update.ServiceName
            }
        }
        catch {
            # Malformed or unreadable -- fall through to the next file, then the fallback.
        }
    }
    return $fallback
}

if (-not $ServiceName) {
    $ServiceName = Resolve-ServiceName -dir $InstallDir -fallback "nwsalertbot"
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

# --- 2. Resolve -Tag to the latest release if not specified -----------------------------------
if (-not $Tag) {
    Write-Step "No -Tag specified -- resolving the latest release..."
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" `
            -Headers @{ "User-Agent" = "NwsAlertBot-update.ps1" }
        $Tag = $release.tag_name
    }
    catch {
        Write-Error "Could not resolve the latest release from https://api.github.com/repos/$Repo/releases/latest -- $($_.Exception.Message). Pass -Tag explicitly to install a specific version."
        exit 1
    }
    if (-not $Tag) {
        Write-Error "GitHub's releases/latest API did not return a tag_name. Pass -Tag explicitly to install a specific version."
        exit 1
    }
    Write-Step "Latest release: $Tag"
}

# --- 3. Detect platform and pick the matching release asset -----------------------------------
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

# --- 4. Download and extract to a temp directory ------------------------------------------------
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "nwsalertbot-update-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $tempDir | Out-Null
$archivePath = Join-Path $tempDir $assetName

try {
    Write-Step "Downloading..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing

    # --- 4b. Verify the download against release.yml's checksums.txt before trusting it --------
    # Protects against corrupted uploads/transfers and simple after-the-fact tampering with the
    # archive. Does NOT protect against a compromised release.yml/repo/GITHUB_TOKEN -- an attacker
    # who can push a malicious release can just as easily update checksums.txt to match. Real
    # supply-chain protection against that would need cryptographic signing, which is a much
    # bigger lift than this project's threat model (a personal weather bot) currently justifies.
    Write-Step "Verifying checksum..."
    $checksumUrl = "https://github.com/$Repo/releases/download/$Tag/checksums.txt"
    $checksumFile = Join-Path $tempDir "checksums.txt"
    Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumFile -UseBasicParsing

    $expectedLine = Get-Content $checksumFile | Where-Object { $_ -match [regex]::Escape($assetName) }
    if (-not $expectedLine) {
        Write-Error "No checksum entry found for $assetName in checksums.txt -- aborting for safety."
        exit 1
    }
    $expectedHash = ($expectedLine -split '\s+')[0]
    $actualHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        Write-Error "Checksum mismatch for $assetName! Expected $expectedHash, got $actualHash. Aborting -- do not trust this download."
        exit 1
    }
    Write-Step "Checksum verified for $assetName."

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

    # --- 5. Replace ONLY the executable (and this script) -- never appsettings.json/appsettings.Local.json
    #        or any runtime state file (posted_alerts.txt, confirmed_platforms.txt, logs/,
    #        x_post_count.txt, twilio_sms_count.txt). Those belong to this install, not the release.
    $currentExePath = Join-Path $InstallDir $exeName
    $backupPath = $null # stays $null if there was nothing to back up (first-ever install) --
                         # checked later before attempting a rollback.
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

    $newSetupScriptPath = Join-Path $extractDir "setup-service.ps1"
    if (Test-Path $newSetupScriptPath) {
        Write-Step "Updating setup-service.ps1 for next time"
        Copy-Item -Path $newSetupScriptPath -Destination (Join-Path $InstallDir "setup-service.ps1") -Force
    }
}
finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 6. Restart, then verify the new version actually stays running ----------------------------

# Starts/restarts the bot via its systemd unit / Windows Service if one exists (matching
# $ServiceName), otherwise launches the executable directly. Returns a hashtable describing how
# it was started, so Test-BotIsRunning can check the right thing afterward. Called twice on a
# failed update: once for the new version, once again for the rolled-back previous version.
function Start-BotService {
    if ($IsLinux) {
        $unitExists = (systemctl list-unit-files "$ServiceName.service" 2>$null | Select-String $ServiceName)
        if ($unitExists) {
            Write-Step "Restarting systemd service '$ServiceName'..."
            sudo systemctl restart $ServiceName
            return @{ Mode = "systemd" }
        }
    }
    elseif ($IsWindows) {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) {
            Write-Step "Restarting Windows Service '$ServiceName'..."
            Restart-Service -Name $ServiceName -Force
            return @{ Mode = "windows-service" }
        }
    }

    Write-Step "No matching service found -- launching the executable directly."
    $proc = Start-Process -FilePath $currentExePath -WorkingDirectory $InstallDir -PassThru
    return @{ Mode = "direct"; ProcessId = $proc.Id }
}

# Lightweight health check -- just "did it not immediately crash", not real application health
# (there's no health endpoint to call). Good enough to catch a release that fails to start at all.
function Test-BotIsRunning([hashtable]$startInfo) {
    switch ($startInfo.Mode) {
        "systemd" {
            $status = (systemctl is-active $ServiceName 2>$null)
            return $status.Trim() -eq "active"
        }
        "windows-service" {
            $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            return $svc -and $svc.Status -eq "Running"
        }
        "direct" {
            return $null -ne (Get-Process -Id $startInfo.ProcessId -ErrorAction SilentlyContinue)
        }
    }
    return $false
}

$startInfo = Start-BotService

Write-Step "Waiting ${RollbackCheckDelaySeconds}s to confirm the new version started successfully..."
Start-Sleep -Seconds $RollbackCheckDelaySeconds

if (Test-BotIsRunning $startInfo) {
    Write-Step "Update to $Tag complete and running."
    exit 0
}

# --- 7. Rollback -- the new version didn't stay running -----------------------------------------
Write-Step "New version did not stay running after ${RollbackCheckDelaySeconds}s."

if (-not $backupPath -or -not (Test-Path $backupPath)) {
    Write-Error "Update to $Tag appears to have failed, and no backup exists to roll back to (this was the first install onto $currentExePath). Manual intervention needed -- check logs for why the new version won't start."
    exit 1
}

Write-Step "Rolling back to the previous version from $backupPath..."
Copy-Item -Path $backupPath -Destination $currentExePath -Force
if (-not $IsWindows) {
    chmod +x $currentExePath
}

$rollbackStartInfo = Start-BotService
Start-Sleep -Seconds $RollbackCheckDelaySeconds

if (Test-BotIsRunning $rollbackStartInfo) {
    Write-Error "Update to $Tag failed to start and was automatically rolled back to the previous version, which is running again. Check logs for why $Tag didn't start before retrying."
    exit 1
}
else {
    Write-Error "Update to $Tag failed AND the rollback to the previous version also failed to start. Manual intervention required -- the bot may be down. Check $backupPath and $currentExePath directly."
    exit 1
}
