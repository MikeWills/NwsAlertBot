#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Installs (or removes) NwsAlertBot as a background service -- a systemd unit on Linux, a
    Windows Service on Windows -- so it starts on boot and restarts automatically if it crashes.

.DESCRIPTION
    Creates a service that runs the executable in this script's own directory, with the working
    directory pinned to that same directory so appsettings.json and runtime state files
    (posted_alerts.txt, logs/, etc.) are found/written in the right place.

    Running more than one instance on the same machine (e.g. one bot per Discord server) means
    each instance needs a distinct service name. Rather than typing that name twice in two
    different places (a CLI argument here, a JSON value in appsettings.json) and risking a typo
    that silently breaks Update.AutoApply's auto-restart-after-update logic, -ServiceName is
    optional: if omitted, it's read straight out of Update.ServiceName in that instance's
    appsettings.Local.json (checked first) or appsettings.json (in -InstallDir) -- the same value
    UpdateCheckService already uses. Set it once there and every script agrees on the name
    automatically. Passing -ServiceName explicitly still overrides this.

    Must be run with elevated privileges: as Administrator on Windows, with sudo on Linux.

.PARAMETER ServiceName
    Name to register the service under. Must be unique per machine if running multiple instances.
    If omitted, resolved from Update.ServiceName in appsettings.Local.json/appsettings.json (see
    DESCRIPTION), falling back to "nwsalertbot" if neither sets it.

.PARAMETER InstallDir
    Directory containing the executable and appsettings.json. Defaults to this script's own
    directory.

.PARAMETER Description
    Human-readable service description. Defaults to a name derived from -ServiceName.

.PARAMETER User
    Linux only. The systemd unit's User=. Defaults to whoever runs this script (via `whoami`) --
    pass this to run the service as a different, e.g. dedicated, account instead. The user must
    already exist. Also chowns -InstallDir to it, since the account needs write access there for
    logs/, posted_alerts.txt, and the other runtime state files -- without this the service would
    install and immediately crash-loop with a permission-denied error trying to create logs/.

.PARAMETER Uninstall
    Stops and removes the named service instead of creating it. Does not touch the executable,
    appsettings.json, or any runtime state file -- only the service registration itself.

.PARAMETER DryRun
    Reports what would be done without actually creating, modifying, or removing anything.

.PARAMETER ConfigurePasswordlessSudo
    Linux only. If you plan to use Update.AutoApply, update.ps1's restart step runs
    "sudo systemctl restart <ServiceName>" non-interactively -- without passwordless sudo for
    that exact command, it silently fails or hangs waiting for a password that will never come.
    This switch writes a narrowly-scoped drop-in to /etc/sudoers.d/ granting the current user
    NOPASSWD for *only* "systemctl restart <ServiceName>" (validated with `visudo -c` before
    being installed, so a malformed rule is never actually applied). Off by default -- modifying
    sudoers is a real security-relevant change and shouldn't happen as a silent side effect of
    installing a service.

.EXAMPLE
    sudo ./setup-service.ps1 -ServiceName nwsalertbot-mainserver

.EXAMPLE
    # Run as Administrator. Reads the service name from appsettings.json/appsettings.Local.json
    # in C:\bots\friendserver -- set Update.ServiceName there first.
    ./setup-service.ps1 -InstallDir C:\bots\friendserver

.EXAMPLE
    sudo ./setup-service.ps1 -ServiceName nwsalertbot-mainserver -Uninstall

.EXAMPLE
    # Also grants passwordless "systemctl restart nwsalertbot-mainserver", needed for AutoApply
    sudo ./setup-service.ps1 -ServiceName nwsalertbot-mainserver -ConfigurePasswordlessSudo

.EXAMPLE
    # Linux only -- runs the service as the dedicated "nwsbot" account instead of whoever ran
    # this script, and chowns -InstallDir to it so it can write logs/ and other state files.
    sudo ./setup-service.ps1 -ServiceName nwsalertbot -User nwsbot
#>
param(
    [string]$ServiceName,
    [string]$InstallDir = $PSScriptRoot,
    [string]$Description,
    [string]$User,
    [switch]$Uninstall,
    [switch]$DryRun,
    [switch]$ConfigurePasswordlessSudo
)

$ErrorActionPreference = "Stop"

function Write-Step($message) {
    Write-Host "[setup-service] $message"
}

# Resolves the service name from Update.ServiceName in appsettings.Local.json (checked first,
# matching the app's own override precedence) or appsettings.json in $dir, falling back to
# $fallback if neither file exists, is unreadable, or doesn't set it. Kept in sync with the
# identical function in update.ps1 (duplicated rather than shared, since each script is meant to
# run standalone with nothing else to dot-source).
function Resolve-ServiceName([string]$dir, [string]$fallback) {
    foreach ($file in @("appsettings.Local.json", "appsettings.json")) {
        $path = Join-Path $dir $file
        if (-not (Test-Path $path)) { continue }
        try {
            $raw = Get-Content -Path $path -Raw
            # appsettings.json uses only full-line "//" comments (never inline-after-value) and
            # sometimes trailing commas -- ConvertFrom-Json accepts neither, so strip/fix both,
            # matching the leniency System.Text.Json is configured with on the C# side.
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

if (-not $Description) {
    $Description = "NWS Alert Bot ($ServiceName)"
}

if ($User -and -not $IsLinux) {
    Write-Step "WARNING: -User has no effect outside Linux (Windows Services run under LocalSystem here) -- ignoring."
    $User = $null
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

if (-not $Uninstall -and -not (Test-Path (Join-Path $InstallDir "appsettings.json"))) {
    Write-Error "appsettings.json not found in $InstallDir. The bot requires it to start (it's not optional) -- without this check, the service would be created, start, and immediately crash-loop instead of failing with a clear error here. Extract the full release archive, or copy appsettings.json alongside the executable, before running this script."
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
    $sudoersPath = "/etc/sudoers.d/$ServiceName-update"

    if ($Uninstall) {
        if (-not (Test-Path $unitPath)) {
            Write-Step "Unit '$ServiceName.service' does not exist -- nothing to remove."
            exit 0
        }
        if ($DryRun) {
            Write-Step "-DryRun: would stop, disable, and remove $unitPath (and $sudoersPath if present)."
            exit 0
        }
        Write-Step "Stopping and removing systemd unit '$ServiceName'..."
        sudo systemctl stop $ServiceName 2>$null
        sudo systemctl disable $ServiceName 2>$null
        sudo rm -f $unitPath
        sudo systemctl daemon-reload
        if (Test-Path $sudoersPath) {
            sudo rm -f $sudoersPath
            Write-Step "Removed passwordless-sudo rule $sudoersPath."
        }
        Write-Step "Removed."
        exit 0
    }

    if (Test-Path $unitPath) {
        Write-Error "Unit '$unitPath' already exists. Run with -Uninstall first if you want to recreate it, or choose a different -ServiceName (useful if you're running more than one instance)."
        exit 1
    }

    $serviceUser = if ($User) { $User } else { (whoami).Trim() }

    # Catches a typo here before it becomes a cryptic systemd "user does not exist" failure
    # well after this script has already exited.
    if ($User) {
        & id $User *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Error "User '$User' does not exist on this system. Create it first (e.g. sudo useradd -r -s /usr/sbin/nologin $User), then re-run this script."
            exit 1
        }
    }

    $unitContent = @"
[Unit]
Description=$Description
After=network.target

[Service]
Type=simple
ExecStart=$exePath
WorkingDirectory=$InstallDir
User=$serviceUser
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
"@

    if ($DryRun) {
        Write-Step "-DryRun: would write the following unit to $unitPath, then enable and start it:"
        Write-Host $unitContent
        if ($User) {
            Write-Step "-DryRun: would also chown $InstallDir to '$User'."
        }
        if ($ConfigurePasswordlessSudo) {
            Write-Step "-DryRun: would also write a passwordless-sudo rule for 'systemctl restart $ServiceName' to $sudoersPath."
        }
        exit 0
    }

    Write-Step "Writing systemd unit to $unitPath (requires sudo)..."
    $unitContent | sudo tee $unitPath | Out-Null
    chmod +x $exePath

    if ($User) {
        # The service user needs write access to -InstallDir for logs/, posted_alerts.txt, and
        # the other runtime state files -- without this it installs and immediately crash-loops
        # with a permission-denied error the first time it tries to create logs/.
        Write-Step "Granting '$User' ownership of $InstallDir..."
        sudo chown -R "${User}" $InstallDir
    }

    Write-Step "Enabling and starting '$ServiceName'..."
    sudo systemctl daemon-reload
    sudo systemctl enable $ServiceName
    sudo systemctl start $ServiceName

    Write-Step "Service '$ServiceName' installed and started."
    Write-Step "Manage it with: systemctl {status|stop|start|restart} $ServiceName"

    if ($ConfigurePasswordlessSudo) {
        # Scoped to exactly the one command update.ps1 actually invokes non-interactively --
        # least privilege, rather than a blanket NOPASSWD for all of systemctl.
        $currentUserForSudo = (whoami).Trim()
        $sudoersLine = "$currentUserForSudo ALL=(ALL) NOPASSWD: /bin/systemctl restart $ServiceName`n"
        $tempSudoers = [System.IO.Path]::GetTempFileName()
        try {
            Set-Content -Path $tempSudoers -Value $sudoersLine -NoNewline
            sudo visudo -c -f $tempSudoers | Out-Null
            if ($LASTEXITCODE -eq 0) {
                sudo install -m 0440 -o root -g root $tempSudoers $sudoersPath
                Write-Step "Granted passwordless 'systemctl restart $ServiceName' via $sudoersPath (required for Update.AutoApply's restart-after-update step)."
            }
            else {
                Write-Step "WARNING: sudoers syntax validation failed -- skipped passwordless-sudo setup. Update.AutoApply's restart step will hang/fail without it; see docs/TECHNICAL.md 'Known Limitations'."
            }
        }
        finally {
            Remove-Item -Path $tempSudoers -Force -ErrorAction SilentlyContinue
        }
    }
}
# --- macOS -------------------------------------------------------------------------------------
elseif ($IsMacOS) {
    Write-Error "macOS launchd service setup isn't supported by this script yet -- run the executable directly, or set up a launchd .plist manually."
    exit 1
}
