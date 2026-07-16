# Contributing to NwsAlertBot

This is the "workshop manual": how to set up a local dev environment, run the test suite, cut a
release, and deploy the project's own continuously-deployed instance. For what the bot does and
how to configure/run it, see [README.md](README.md). For architecture and the full configuration
field reference, see [docs/TECHNICAL.md](docs/TECHNICAL.md).

---

## Table of Contents

1. [NuGet Packages](#nuget-packages)
2. [Running Tests](#running-tests)
3. [Cross-Platform Release Builds](#cross-platform-release-builds)
4. [Deploying to Ubuntu (GitHub Actions)](#deploying-to-ubuntu-github-actions)

---

## NuGet Packages

Auto-restored on first build:

- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Configuration.Json`
- `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` — structured logging
- `NetTopologySuite`, `NetTopologySuite.IO.GeoJSON4STJ` — GeoJSON geometry (union/dissolve, point-in-polygon, convex hull, simplification)
- `Microsoft.Extensions.Http.Resilience` — retry/circuit-breaker for read-only weather/mapping HTTP clients
- `Microsoft.Extensions.Hosting.WindowsServices` — `.UseWindowsService()`, required for Windows Service mode (see `scripts/setup-service.ps1`)
- `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` — test-only, in `NwsAlertBot.Tests`

A new third-party package is fine if it earns its place, but flag the tradeoff before adding one —
see `CLAUDE.md`'s Non-Negotiable Rules for the full policy.

---

## Running Tests

```bash
dotnet test NwsAlertBot.Tests/NwsAlertBot.Tests.csproj
```

The `NwsAlertBot.Tests` project (xunit) covers pure logic only — no live HTTP calls, no
credentials needed:

- **Parsing** — SPC MCD's `LAT...LON` polygon parsing (including the lon-wrap-at-100°W encoding),
  MCD number extraction, and valid-window parsing (including the midnight-crossover fix)
- **Formatting** — `NwsAlert.FormatPost`'s per-platform truncation, and the shared
  `PlatformHelpers` (SMS body building, cache-busting, Discord embed colors)
- **Geometry** — `PolygonGeometry`'s centroid and point-in-polygon logic (the NetTopologySuite-backed
  replacement for the old hand-rolled GIS code)
- **URL validation** — `MapService.BuildIemSpsUrl`'s AFOS/WMO identifier handling

Several tested methods are `internal` rather than `public` (e.g. `SpcMcdService.ParseLatLon`,
`NwsAlertService.NormalizeNwsText`) — the test project sees them via `InternalsVisibleTo`
(`InternalsVisibleTo.cs` at the repo root), not by widening the public API surface. When adding
tests for a new pure-logic method that's currently `private`, change it to `internal` (not
`public`) and it becomes visible to `NwsAlertBot.Tests` automatically.

`.github/workflows/ci.yml` runs this suite on every pull request (and on push to master), gating
merges via a required GitHub branch protection check. `deploy.yml` also runs it before publishing —
a failing test blocks the live deploy to production.

---

## Cross-Platform Release Builds

Separate from `deploy.yml` (which continuously deploys `linux-x64` to your own server on every
push to `master`), `.github/workflows/release.yml` builds downloadable, self-contained binaries
for Windows, Linux, and macOS (both Intel and Apple Silicon) whenever you push a version tag.

### How it works

- **Trigger:** push a tag matching `v*` (e.g. `v1.0.0`). No manual dispatch — cutting a release
  is always tied to a version tag.
- **Build:** all four platforms are cross-compiled from a single `ubuntu-latest` runner —
  `dotnet publish` fetches the target runtime pack via NuGet regardless of host OS, so no
  matrix of OS runners is needed. Explicitly targets `NwsAlertBot.csproj` (not the `.sln`),
  since `-o` isn't fully supported at solution scope.
- **Output:** each platform is published self-contained + single-file
  (`-p:PublishSingleFile=true`), so the result is one executable with no separate .NET runtime
  install required on the target machine.
- **Packaging:** Windows ships as `.zip`; Linux and macOS ship as `.tar.gz` (zip doesn't
  reliably preserve the Unix executable bit, which would otherwise require the user to manually
  `chmod +x` after extracting).
- **Publishing:** all four archives are attached to a new GitHub Release named after the tag,
  created via the GitHub CLI (`gh release create`) using the built-in `GITHUB_TOKEN` — no
  third-party release-management action required.
- **Versioning:** the tag (minus its leading `v`) is passed to `dotnet publish` as
  `-p:Version=X.Y.Z`, so the running executable knows its own version — this is what
  [Auto-Update](docs/TECHNICAL.md#auto-update--full-reference) compares against GitHub Releases.

### Cutting a release

```bash
git tag v1.0.0
git push origin v1.0.0
```

Produces `NwsAlertBot-win-x64.zip`, `NwsAlertBot-linux-x64.tar.gz`, `NwsAlertBot-osx-x64.tar.gz`,
and `NwsAlertBot-osx-arm64.tar.gz` attached to the `v1.0.0` release, each also containing
`scripts/update.ps1` (see [Auto-Update](docs/TECHNICAL.md#auto-update--full-reference)),
`scripts/setup-service.ps1`, and `scripts/uninstall-service.ps1`.

### Running a downloaded build

Each archive contains the executable, `appsettings.json`, `update.ps1`, `setup-service.ps1`, and
`uninstall-service.ps1`. See the README's [Running the Bot](README.md#running-the-bot) section for
how to set it up and run it — the archive layout is identical to what you'd get from building
locally.

---

## Deploying to Ubuntu (GitHub Actions)

This section covers continuous deployment straight from this repo's own CI (`deploy.yml`) — for
your own fork with its own GitHub Actions secrets. If you just want to run a downloaded release
build as a background service without setting up CI, see the README's
[Running as a Service](README.md#running-as-a-service) instead — `scripts/setup-service.ps1`
automates the systemd unit shown below.

Every push to `master` builds a self-contained `linux-x64` binary and deploys it to your server
via SSH over Tailscale, then restarts the systemd service automatically.

### One-time server setup

Run these commands on your Ubuntu server (replace `YOUR_SSH_USER` with the username you will
use for SSH deployments):

```bash
# Create a dedicated service user (no login shell, no home directory)
sudo useradd --system --no-create-home --shell /usr/sbin/nologin nwsalertbot

# Create the deploy directory
sudo mkdir -p /opt/nwsalertbot

# Give the service user ownership, and add your SSH user to the group
# so GitHub Actions can write files to the directory
sudo chown nwsalertbot:nwsalertbot /opt/nwsalertbot
sudo chmod 775 /opt/nwsalertbot
sudo usermod -aG nwsalertbot YOUR_SSH_USER

# Place your credentials file — this is never deployed by GitHub Actions
sudo nano /opt/nwsalertbot/appsettings.Local.json
sudo chown nwsalertbot:nwsalertbot /opt/nwsalertbot/appsettings.Local.json
sudo chmod 600 /opt/nwsalertbot/appsettings.Local.json

# Install the systemd service (copy-paste this file content)
sudo tee /etc/systemd/system/nwsalertbot.service > /dev/null <<'EOF'
[Unit]
Description=NWS Alert Bot
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=nwsalertbot
WorkingDirectory=/opt/nwsalertbot
ExecStart=/opt/nwsalertbot/NwsAlertBot
Restart=always
RestartSec=10
KillSignal=SIGINT
KillMode=process

StandardOutput=journal
StandardError=journal
SyslogIdentifier=nwsalertbot

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload
sudo systemctl enable nwsalertbot
```

Allow your SSH user to start/stop the service without a password prompt:

```bash
sudo visudo
# Add this line (replace YOUR_SSH_USER):
YOUR_SSH_USER ALL=(ALL) NOPASSWD: /bin/systemctl start nwsalertbot, /bin/systemctl stop nwsalertbot, /bin/systemctl status nwsalertbot
```

### Tailscale setup

The deploy workflow connects to your server via Tailscale, so no public SSH exposure is needed.

1. **Create a tag** — in the [Tailscale ACL editor](https://login.tailscale.com/admin/acls), add:
   ```jsonc
   "tagOwners": {
     "tag:ci": ["autogroup:admin"]
   }
   ```
   Then add an ACL rule allowing `tag:ci` to reach your server on port 22:
   ```jsonc
   {
     "action": "accept",
     "src":    ["tag:ci"],
     "dst":    ["your-server:22"]
   }
   ```

2. **Create an OAuth credential** — go to [Trust credentials](https://login.tailscale.com/admin/settings/trust-credentials), click **Credential → OAuth**. On the Settings step, assign the `tag:ci` tag. On the Scopes step, check **Write** on both **Devices → Core** and **Keys → Auth Keys**. Click **Generate credential** and copy the Client ID and Client Secret.

### GitHub secrets

Add these in your repo under **Settings → Secrets and variables → Actions**:

| Secret | Description |
|---|---|
| `SSH_HOST` | Server's Tailscale IP (`tailscale ip -4`) or MagicDNS name |
| `SSH_USER` | SSH username |
| `SSH_KEY` | Private SSH key (contents of `~/.ssh/id_rsa`) |
| `SSH_PORT` | SSH port — omit to default to `22` |
| `DEPLOY_PATH` | Deploy directory on server, e.g. `/opt/nwsalertbot` |
| `TS_OAUTH_CLIENT_ID` | Tailscale OAuth Client ID |
| `TS_OAUTH_SECRET` | Tailscale OAuth Client Secret |

### Viewing logs on the server

```bash
# Follow live log output
journalctl -u nwsalertbot -f

# Last 100 lines
journalctl -u nwsalertbot -n 100

# Check service status
sudo systemctl status nwsalertbot
```
