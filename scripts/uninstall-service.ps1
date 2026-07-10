#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Stops and removes the NwsAlertBot background service (systemd unit on Linux, Windows Service
    on Windows) installed by setup-service.ps1.

.DESCRIPTION
    A thin, discoverably-named wrapper around `setup-service.ps1 -Uninstall` -- see that script's
    own help (Get-Help ./setup-service.ps1 -Full) for the full uninstall behavior. Does not touch
    the executable, appsettings.json, appsettings.Local.json, or any runtime state file
    (posted_alerts.txt, logs/, etc.) -- only the service registration itself, and the
    passwordless-sudo rule created by -ConfigurePasswordlessSudo, if any.

    Must be run with elevated privileges: as Administrator on Windows, with sudo on Linux.

.PARAMETER ServiceName
    Name of the service to remove. If omitted, resolved from Update.ServiceName in
    appsettings.Local.json/appsettings.json in -InstallDir, falling back to "nwsalertbot" -- the
    same resolution setup-service.ps1 and update.ps1 use.

.PARAMETER InstallDir
    Directory containing appsettings.json (used only to resolve -ServiceName if it isn't passed
    explicitly). Defaults to this script's own directory.

.PARAMETER DryRun
    Reports what would be stopped/removed without actually touching anything.

.EXAMPLE
    sudo ./uninstall-service.ps1 -ServiceName nwsalertbot

.EXAMPLE
    # Windows, run as Administrator
    ./uninstall-service.ps1 -DryRun
#>
param(
    [string]$ServiceName,
    [string]$InstallDir = $PSScriptRoot,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$setupScript = Join-Path $PSScriptRoot "setup-service.ps1"
if (-not (Test-Path $setupScript)) {
    Write-Error "setup-service.ps1 not found alongside this script in $PSScriptRoot -- uninstall-service.ps1 forwards to it and can't do the actual removal without it."
    exit 1
}

$forwardArgs = @{ InstallDir = $InstallDir; Uninstall = $true }
if ($ServiceName) { $forwardArgs.ServiceName = $ServiceName }
if ($DryRun)      { $forwardArgs.DryRun = $true }

& $setupScript @forwardArgs
exit $LASTEXITCODE
