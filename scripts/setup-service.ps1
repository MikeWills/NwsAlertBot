#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Installs (or removes) NwsAlertBot as a background service -- a systemd unit on Linux, a
    Windows Service on Windows -- so it starts on boot and restarts automatically if it crashes.

.DESCRIPTION
    Creates a service that runs the executable in this script's own directory, with the working
    directory pinned to that same directory so appsettings.json and runtime state files
    (posted_alerts.txt, logs/, etc.) are found/written in the right place.

    -ServiceName lets you run more than one instance on the same machine under different names --
    e.g. running this bot for two different Discord servers from two separate install
    directories. Give each instance its own -ServiceName and -InstallDir. If you also use
    Update.AutoApply / update.ps1, pass the same -ServiceName there too (its default,
    "nwsalertbot", only matches the first instance) so its auto-restart-after-update logic finds
    the right service.

    Must be run with elevated privileges: as Administrator on Windows, with sudo on Linux.

.PARAMETER ServiceName
    Name to register the service under. Must be unique per machine if running multiple instances.

.PARAMETER InstallDir
    Directory containing the executable and appsettings.json. Defaults to this script's own
    directory.

.PARAMETER Description
    Human-readable service description. Defaults to a name derived from -ServiceName.

.PARAMETER Uninstall
    Stops and removes the named service instead of creating it. Does not touch the executable,
    appsettings.json, or any runtime state file -- only the service registration itself.

.PARAMETER DryRun
    Reports what would be done without actually creating, modifying, or removing anything.

.EXAMPLE
    sudo ./setup-service.ps1 -ServiceName nwsalertbot-mainserver

.EXAMPLE
    # Run as Administrator
    ./setup-service.ps1 -ServiceName nwsalertbot-friendserver -InstallDir C:\bots\friendserver

.EXAMPLE
    sudo ./setup-service.ps1 -ServiceName nwsalertbot-mainserver -Uninstall
#>
param(
    [string]$ServiceName = "nwsalertbot",
    [string]$InstallDir = $PSScriptRoot,
    [string]$Description,
    [switch]$Uninstall,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if (-not $Description) {
    $Description = "NWS Alert Bot ($ServiceName)"
}

function Write-Step($message) {
    Write-Host "[setup-service] $message"
}

$exeName = if ($IsWindows) { "NwsAlertBot.exe" } elseif ($IsLinux -or $IsMacOS) { "NwsAlertBot" } else {
    Write-Error "Unrecognized platform -- IsWindows/IsMacOS/IsLinux are all false. Are you running PowerShell 7+ (pwsh), not Windows PowerShell 5.1?"
    exit 1
}
$exePath = Join-Path $InstallDir $exeName

if (-not $Uninstall -and -not (Test-Path $exePath)) {
    Write-Error "Executable not found at $exePath. Run this script from the same directory as $exeName (or pass -InstallDir)."
    exit 1
}

# --- Windows -------------------------------------------------------------------------------
if ($IsWindows) {
    # Get-Service only queries -- checked before the admin gate so -DryRun works without
    # elevation. Only the actual create/remove actions further down require Administrator.
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($Uninstall) {
        if (-not $existing) {
            Write-Step "Service '$ServiceName' does not exist -- nothing to remove."
            exit 0
        }
        if ($DryRun) {
            Write-Step "-DryRun: would stop and remove service '$ServiceName'."
            exit 0
        }
    }
    elseif ($existing) {
        Write-Error "Service '$ServiceName' already exists. Run with -Uninstall first if you want to recreate it, or choose a different -ServiceName (useful if you're running more than one instance)."
        exit 1
    }
    elseif ($DryRun) {
        Write-Step "-DryRun: would create Windows Service '$ServiceName' running $exePath (working directory $InstallDir), set to auto-start and auto-restart on failure."
        exit 0
    }

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
        IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "Must run as Administrator to create or remove a Windows Service. Right-click PowerShell -> 'Run as Administrator', then re-run this script."
        exit 1
    }

    if ($Uninstall) {
        Write-Step "Stopping and removing service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        Write-Step "Removed."
        exit 0
    }

    Write-Step "Creating Windows Service '$ServiceName'..."
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$exePath`"" `
        -DisplayName $Description `
        -Description $Description `
        -StartupType Automatic | Out-Null

    # New-Service has no equivalent for failure-recovery actions -- restart up to 3 times with a
    # 60s delay between attempts, resetting the failure count after 24h of successful uptime.
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

    Write-Step "Starting service..."
    Start-Service -Name $ServiceName

    Write-Step "Service '$ServiceName' installed and started."
    Write-Step "Manage it with Get-Service/Start-Service/Stop-Service/Restart-Service -Name $ServiceName, or services.msc."
}
# --- Linux -----------------------------------------------------------------------------------
elseif ($IsLinux) {
    $unitPath = "/etc/systemd/system/$ServiceName.service"

    if ($Uninstall) {
        if (-not (Test-Path $unitPath)) {
            Write-Step "Unit '$ServiceName.service' does not exist -- nothing to remove."
            exit 0
        }
        if ($DryRun) {
            Write-Step "-DryRun: would stop, disable, and remove $unitPath."
            exit 0
        }
        Write-Step "Stopping and removing systemd unit '$ServiceName'..."
        sudo systemctl stop $ServiceName 2>$null
        sudo systemctl disable $ServiceName 2>$null
        sudo rm -f $unitPath
        sudo systemctl daemon-reload
        Write-Step "Removed."
        exit 0
    }

    if (Test-Path $unitPath) {
        Write-Error "Unit '$unitPath' already exists. Run with -Uninstall first if you want to recreate it, or choose a different -ServiceName (useful if you're running more than one instance)."
        exit 1
    }

    $currentUser = (whoami).Trim()
    $unitContent = @"
[Unit]
Description=$Description
After=network.target

[Service]
Type=simple
ExecStart=$exePath
WorkingDirectory=$InstallDir
User=$currentUser
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
"@

    if ($DryRun) {
        Write-Step "-DryRun: would write the following unit to $unitPath, then enable and start it:"
        Write-Host $unitContent
        exit 0
    }

    Write-Step "Writing systemd unit to $unitPath (requires sudo)..."
    $unitContent | sudo tee $unitPath | Out-Null
    chmod +x $exePath

    Write-Step "Enabling and starting '$ServiceName'..."
    sudo systemctl daemon-reload
    sudo systemctl enable $ServiceName
    sudo systemctl start $ServiceName

    Write-Step "Service '$ServiceName' installed and started."
    Write-Step "Manage it with: systemctl {status|stop|start|restart} $ServiceName"
}
# --- macOS -------------------------------------------------------------------------------------
elseif ($IsMacOS) {
    Write-Error "macOS launchd service setup isn't supported by this script yet -- run the executable directly, or set up a launchd .plist manually."
    exit 1
}
