# NWS Alert Social Media Bot

A .NET 10 C# console application that polls the National Weather Service API for active weather
alerts and posts them to Facebook, Instagram, X (Twitter), Bluesky, Mastodon, Discord (webhook
and DM), and Telegram — and sends real-time push notifications and SMS via Pushover, Twilio, and VoIP.ms.

This document covers setup, running, and configuration. For architecture, internals, and the
complete configuration field reference, see [docs/TECHNICAL.md](docs/TECHNICAL.md). To build the
project, run tests, or cut a release, see [CONTRIBUTING.md](CONTRIBUTING.md). For a running
history of changes, see [CHANGELOG.md](CHANGELOG.md).

---

## Table of Contents

1. [Setup](#setup)
2. [Running the Bot](#running-the-bot)
3. [Running as a Service](#running-as-a-service)
4. [Auto-Update](#auto-update)
5. [Basic Configuration](#basic-configuration)
6. [Geographic Filtering: Zones and Counties](#geographic-filtering-zones-and-counties)
7. [Alert Filtering](#alert-filtering)
8. [Complete NWS Event Type Reference](#complete-nws-event-type-reference)
9. [Recommended Filter Configurations](#recommended-filter-configurations)
10. [Additional Alert Feeds: SPC Outlooks, SPC MCDs, HWO, WPC ERO](#additional-alert-feeds-spc-outlooks-spc-mcds-hwo-wpc-ero)
11. [API Credentials — Social Media](#api-credentials)
12. [Push / SMS Notifications](#push--sms-notifications)
13. [Startup Confirmation](#startup-confirmation)
14. [Image Smoke Test](#image-smoke-test)
15. [Map Images (Mapbox)](#map-images-mapbox)

---

## Setup

There are two ways to get the bot: download a prebuilt release, or build it from source. Most
users want the first option — no .NET SDK or Visual Studio required.

### Option 1: Download a release (recommended)

1. Download and extract the archive for your OS. These links always point to the latest release:

   | OS | Download | Extract |
   |---|---|---|
   | Windows | [NwsAlertBot-win-x64.zip](https://github.com/MikeWills/NwsAlertBot/releases/latest/download/NwsAlertBot-win-x64.zip) | Right-click → Extract All, or `Expand-Archive NwsAlertBot-win-x64.zip` |
   | Linux (x64) | [NwsAlertBot-linux-x64.tar.gz](https://github.com/MikeWills/NwsAlertBot/releases/latest/download/NwsAlertBot-linux-x64.tar.gz) | `tar -xzf NwsAlertBot-linux-x64.tar.gz` |
   | macOS (Intel) | [NwsAlertBot-osx-x64.tar.gz](https://github.com/MikeWills/NwsAlertBot/releases/latest/download/NwsAlertBot-osx-x64.tar.gz) | `tar -xzf NwsAlertBot-osx-x64.tar.gz` |
   | macOS (Apple Silicon) | [NwsAlertBot-osx-arm64.tar.gz](https://github.com/MikeWills/NwsAlertBot/releases/latest/download/NwsAlertBot-osx-arm64.tar.gz) | `tar -xzf NwsAlertBot-osx-arm64.tar.gz` |

   From the command line, download and extract in one go:

   ```bash
   RELEASE_URL=https://github.com/MikeWills/NwsAlertBot/releases/latest/download

   # Linux
   curl -LO "$RELEASE_URL/NwsAlertBot-linux-x64.tar.gz"
   tar -xzf NwsAlertBot-linux-x64.tar.gz

   # macOS (Intel)
   curl -LO "$RELEASE_URL/NwsAlertBot-osx-x64.tar.gz"
   tar -xzf NwsAlertBot-osx-x64.tar.gz

   # macOS (Apple Silicon)
   curl -LO "$RELEASE_URL/NwsAlertBot-osx-arm64.tar.gz"
   tar -xzf NwsAlertBot-osx-arm64.tar.gz
   ```

   ```powershell
   # Windows (PowerShell)
   $releaseUrl = "https://github.com/MikeWills/NwsAlertBot/releases/latest/download"
   Invoke-WebRequest "$releaseUrl/NwsAlertBot-win-x64.zip" -OutFile NwsAlertBot-win-x64.zip
   Expand-Archive NwsAlertBot-win-x64.zip
   ```

   All prior versions are also listed on the [Releases page](https://github.com/MikeWills/NwsAlertBot/releases)
   if you need to download a specific one instead of the latest.
2. The extracted folder contains the executable, `appsettings.json`, `update.ps1`,
   `setup-service.ps1`, and `uninstall-service.ps1`
3. Make a copy of `appsettings.json` and name the new file `appsettings.Local.json` alongside it (see [Local AppSettings file](#local-appsettings-file)) with your real credentials
4. Set `"Enabled": true` for each platform you want active — see [API Credentials](#api-credentials) for how to set each one up.
5. Run it directly (`./NwsAlertBot` on Linux/macOS — `chmod +x` first if needed — or
   `NwsAlertBot.exe` on Windows), or install it as a background service — see
   [Running the Bot](#running-the-bot) below

The executable is self-contained — no separate .NET runtime install is required.

### Option 2: Build from source

Requires the **.NET 10 SDK** and Visual Studio 2022 (recommended) or VS Code.

1. Clone the repo and open `NwsAlertBot.csproj` in Visual Studio
2. NuGet packages restore automatically on first build
3. Create `appsettings.Local.json` (see below) with your real credentials
4. Set `"Enabled": true` for each platform you want active
5. Run with F5, or publish as a self-contained executable (see
   [CONTRIBUTING.md](CONTRIBUTING.md) for cutting your own release build)

### Local AppSettings file

`appsettings.json` is a template — all sensitive values are
placeholders like `"YOUR_API_KEY"`. **Never put real credentials in `appsettings.json`.**

Instead, create `appsettings.Local.json` in the project root. Override only the fields you need:

```json
{
  "Nws": {
    "State": "MO"
  },
  "Pushover": {
    "Enabled": true,
    "ApiToken": "your-real-token",
    "UserKey":  "your-real-key"
  }
}
```

`appsettings.Local.json` is loaded after `appsettings.json` and its values win.
You only need to include the sections/fields you are actually changing.

---

## Running the Bot

### Development (Visual Studio)
Press F5. The bot starts polling immediately and logs to the console.

### As a Background Service (Recommended for Production)
For a downloaded release build (or your own self-contained publish), run
`scripts/setup-service.ps1` to install it as a systemd unit (Linux) or Windows Service
(Windows) — starts on boot, restarts automatically if it crashes. See
[Running as a Service](#running-as-a-service) below for details, including how to run more than
one instance on the same machine.

### As a Windows Scheduled Task
A lighter-weight alternative to a full Windows Service: publish as a self-contained executable
and schedule it to run on startup via `Task Scheduler`, with a trigger of "At system startup" and
a repeat interval.

### Log Files

The bot writes daily rolling log files to a `logs/` subdirectory of the working directory:

```
logs/nwsalertbot-20260625.log
logs/nwsalertbot-20260626.log
...
```

Logs are retained for **30 days** and then automatically deleted. The file format includes full timestamps and the name of the service that logged each line, making it easy to search for a specific platform or feed:

```
[2026-06-25 14:32:01 INF] NwsAlertBot.Services.AlertPollingService: Active storm mode engaged — polling every 60s for 4h.
[2026-06-25 14:32:03 ERR] NwsAlertBot.Services.FacebookService: Facebook: Post failed. Status=400
```

### Alert Deduplication
The bot tracks posted alert IDs in `posted_alerts.txt` in the working directory. This file
persists across restarts so the bot won't re-post alerts after a restart. The file is pruned
automatically when it exceeds 10,000 entries.

---

## Running as a Service

`scripts/setup-service.ps1` (bundled in every release archive alongside `update.ps1`) installs
the bot as a background service — a systemd unit on Linux, a Windows Service on Windows — so it
starts on boot and restarts automatically if it crashes, without needing a terminal window left
open. Requires PowerShell 7+ (`pwsh`), same as `update.ps1`.

Must be run elevated: as **Administrator** on Windows, with **`sudo`** on Linux.

```bash
# Linux
sudo ./setup-service.ps1 -ServiceName nwsalertbot

# Windows (run PowerShell as Administrator)
./setup-service.ps1 -ServiceName nwsalertbot
```

To remove the service later (e.g. you've decided you don't want the bot anymore), run
`uninstall-service.ps1` from the same directory — it stops and removes the service registration
only, leaving `appsettings.json`, your credentials, and any runtime state files untouched:

```bash
# Linux
sudo ./uninstall-service.ps1 -ServiceName nwsalertbot

# Windows (run PowerShell as Administrator)
./uninstall-service.ps1 -ServiceName nwsalertbot
```

Running more than one instance on the same machine (e.g. one bot per Discord server), `-DryRun`,
granting passwordless sudo for `Update.AutoApply`, and macOS status are all covered in
[docs/TECHNICAL.md — Running as a Service](docs/TECHNICAL.md#running-as-a-service--full-reference).

---

## Auto-Update

If you're running a downloaded release build (rather than this repo owner's own
continuously-deployed server — see [CONTRIBUTING.md](CONTRIBUTING.md)), the bot can check GitHub
Releases for a newer version and optionally install it automatically.

**Requires PowerShell 7+ (`pwsh`)** on the machine running the bot — it's what `update.ps1` runs
under, cross-platform. Install it from https://github.com/PowerShell/PowerShell if it isn't
already present (Windows PowerShell 5.1, the version that ships built into Windows, is **not**
enough — `pwsh` is a separate, newer install).

### Configuration

```json
"Update": {
  "AutoApply": false,
  "CheckIntervalHours": 24,
  "GitHubRepo": "MikeWills/NwsAlertBot",
  "ServiceName": "nwsalertbot"
}
```

- **`AutoApply`** is the single on/off switch for the whole feature — there's no separate
  "check but don't install" mode. `false` (default): the bot makes no GitHub API calls at all;
  upgrade manually whenever you want by running `update.ps1` yourself (see below). `true`: the
  bot checks GitHub Releases every `CheckIntervalHours` and, the moment it finds a newer tagged
  version, downloads it, replaces its own executable, and restarts itself.
- **`CheckIntervalHours`** (default `24`) — how often to check. Releases are infrequent; there's
  no benefit checking more than once a day.
- **`GitHubRepo`** (default `"MikeWills/NwsAlertBot"`) — change this if you're running your own
  fork with its own tags/releases.
- **`ServiceName`** (default `"nwsalertbot"`) — passed to `update.ps1` as its `-ServiceName` so it
  restarts the right service after swapping the executable. You only need to set it here:
  `setup-service.ps1` reads this same value automatically unless you pass `-ServiceName` to it
  explicitly. Only matters if you're [running more than one instance](#running-as-a-service) on
  the same machine — set each instance's own `appsettings.json` to a distinct name before running
  `setup-service.ps1`.

### Running it manually

With `AutoApply: false` (the default), nothing happens automatically. Run the script with no
arguments to install whatever is currently the latest release:

```bash
./update.ps1
```

To install a specific version instead, pass `-Tag`:

```bash
./update.ps1 -Tag v1.2.3
```

To safely verify the script works on your machine (downloads, checksum-verifies, and extracts, but
doesn't touch your install or restart anything) before trusting it with `AutoApply: true`:

```bash
./update.ps1 -DryRun
```

See `Get-Help ./update.ps1 -Full` for all parameters (`-Repo`, `-ServiceName`, etc.).

What gets replaced (and what never is), automatic rollback behavior, checksum verification, and
the honest list of known gaps in unattended operation are covered in
[docs/TECHNICAL.md — Auto-Update](docs/TECHNICAL.md#auto-update--full-reference) — worth reading
before you turn on `AutoApply` unattended. Short version: start with `AutoApply: false` and run
`update.ps1` by hand for a while, or watch the logs closely the first few times you enable it.

---

## Basic Configuration

All configuration lives in `appsettings.json`, overridden by `appsettings.Local.json` for your
real values (see [Local AppSettings file](#local-appsettings-file)). The two settings
blocks every feed shares:

```json
"Location": {
  "Zones":    ["MOZ066", "MOZ067"],
  "Counties": ["MOC217", "MOC039"],
  "TimeZone": "America/Chicago"
}
```

`Zones`/`Counties` control which geographic area every feed (NWS alerts, SPC Outlook/MCD, HWO,
WPC ERO) monitors — see [Geographic Filtering](#geographic-filtering-zones-and-counties) below for
how to find your codes. `TimeZone` is an IANA ID (e.g. `America/Chicago`, `America/New_York`,
`America/Denver`, `America/Los_Angeles`) used to format Issued/Valid/Expires times on every post.

```json
"Polling": {
  "PollIntervalSeconds": 300,
  "ActiveAlertPollIntervalSeconds": 60,
  "ActiveAlertWindowHours": 4,
  "ActiveAlertMinSeverity": "Severe,Extreme"
}
```

`PollIntervalSeconds` (default 300 = 5 min) is the normal check interval. When a new Severe/Extreme
NWS alert (or SPC MCD) comes in, the bot switches to the faster `ActiveAlertPollIntervalSeconds`
(default 60s) for `ActiveAlertWindowHours` (default 4), so you get near-real-time updates during
active weather without hammering the API the rest of the time. A Watch (Tornado Watch, Severe
Thunderstorm Watch, etc.) automatically keeps the faster interval going for its own full duration,
even if that's longer than `ActiveAlertWindowHours` — that way the bot is still checking closely
when the Watch is eventually cancelled or expires.

The main NWS alert feed's own severity/urgency/certainty/event-type filters are covered next, in
[Alert Filtering](#alert-filtering). For the complete field-by-field reference (every setting,
every default, including the per-platform `MinSeverity`/`EventTypes`/`Include*` fields and the
`Spc`/`SpcMcd`/`Hwo`/`Ero` blocks), see
[docs/TECHNICAL.md — Configuration Reference](docs/TECHNICAL.md#configuration-reference).

---

## Geographic Filtering: Zones and Counties

The NWS API supports two types of geographic codes. Both use the format `{ST}{type}{###}` where
`{ST}` is the two-letter state abbreviation and `{###}` is a three-digit number.

### Zone Codes vs. County Codes

| Type | Format | Example | When to use |
|---|---|---|---|
| Forecast Zone | `{ST}Z{###}` | `MOZ066` | Most weather alerts (severe thunderstorm, tornado, winter, fire weather) |
| County Code | `{ST}C{###}` | `MOC217` | County-based alerts (some flood, heat, frost advisories) |

**Recommendation:** Use **both** Zones and Counties together when possible. Some alert types are
issued by zone, others by county. If you use only one type, you may miss certain alerts.

```json
"Location": {
  "Zones":    ["MOZ066", "MOZ067"],
  "Counties": ["MOC217", "MOC039"]
}
```

When both are specified, the bot sends all zone and county codes together in a single NWS API
query — the API accepts mixed UGC codes (`MNZxxx` and `MNCxxx`) in the same list.

> **Note:** In flat terrain (most of the Midwest, Great Plains, Southeast), zone and county
> boundaries are nearly identical and one set is usually sufficient. In mountainous areas
> (Rockies, Appalachians, West Coast), they can differ significantly.

### How to Find Your Zone and County Codes

**Method 1 — NWS Alerts page (easiest):**
1. Go to https://alerts.weather.gov/
2. Click your state on the map
3. The URL will contain zone codes like `?warnzone=MOZ066&warncounty=MOC217`

**Method 2 — NWS Zone/County lookup:**
- Zone codes: https://www.weather.gov/gis/ZoneCounty
- Download the `z_<month><day><year>.zip` shapefile — it contains a CSV with all zone codes and
  county names

**Method 3 — IEM Warning Search:**
- https://mesonet.agron.iastate.edu/vtec/search.php
- Search by county name to find its zone code

**Method 4 — Direct URL test:**
You can verify a zone code works by opening this URL in a browser (replace `MOZ066`):
```
https://api.weather.gov/alerts/active?zone=MOZ066
```

### Zone Code Format Examples by State

| State | Zone Example | County Example | Notes |
|---|---|---|---|
| Missouri | `MOZ066` | `MOC217` | Vernon County |
| Kansas | `KSZ097` | `KSC097` | Same 3-digit FIPS number |
| Illinois | `ILZ005` | `ILC005` | |
| Texas | `TXZ163` | `TXC113` | Very large state — zone = county for most |
| Colorado | `COZ040` | `COC041` | Mountains: zones ≠ counties |
| California | `CAZ018` | `CAC019` | Coastal vs. inland zones differ significantly |

### Multiple Counties/Zones

You can monitor as many zones and counties as you want:

```json
"Zones": ["MOZ064", "MOZ065", "MOZ066", "MOZ067", "MOZ068"],
"Counties": ["MOC039", "MOC167", "MOC043", "MOC217", "MOC185"]
```

There is no API limit on how many codes you pass. The bot makes a single API call with all codes
comma-separated.

---

## Alert Filtering

All filter fields below are applied by the National Weather Service itself when the bot requests
alerts — you're not filtering a big list after the fact, you're only ever asking for the alerts
that already match.

### Message Types

The bot requests all three NWS message types:

| Type | Prefix in post | Meaning |
|---|---|---|
| `Alert` | ⚠️ | New issuance |
| `Update` | 🔄 UPDATE: | Amendment or extension of an existing alert |
| `Cancel` | ✅ CANCELLED: | Explicit cancellation before the original expiry |

Each message type has its own unique ID, so deduplication works correctly — an update or cancellation will always post even if the original alert was already posted. Note that not every alert receives a cancellation; some simply expire naturally without a `Cancel` message from NWS.

**Cancellations always bypass `FilterSeverity`/`FilterUrgency`/`FilterCertainty`.** NWS downgrades
every cancel message to `severity: Minor`, `urgency: Past`, `certainty: Observed` regardless of
the original event's actual severity — confirmed even for cancelled Tornado Warnings and Flash
Flood Warnings. That means a cancellation always reaches you, even with a `FilterSeverity` that
excludes `Minor` (which is every example in this README except the "everything" one).
`FilterEventTypes` still applies, though — a cancellation for an event type you've excluded won't post.

### FilterSeverity

Controls the minimum threat level of alerts to post.

| Value | Meaning | Typical Products |
|---|---|---|
| `Extreme` | Extraordinary threat to life/property | Tornado Warning (Tornado Emergency), Flash Flood Emergency |
| `Severe` | Significant threat | Tornado Warning, Severe Thunderstorm Warning, Flash Flood Warning |
| `Moderate` | Possible threat | Winter Storm Warning, Flood Warning, High Wind Warning |
| `Minor` | Minimal threat | Advisories (Wind Advisory, Frost Advisory, Dense Fog Advisory) |
| `Unknown` | Severity not determined | Some special statements |

**Recommended settings:**

```json
"FilterSeverity": "Severe,Extreme"        // Warnings only — high-impact events
"FilterSeverity": "Moderate,Severe,Extreme" // Add winter storms, flood warnings, etc.
"FilterSeverity": ""                       // All alerts including advisories (noisy)
```

### FilterUrgency

How quickly action is needed.

| Value | Meaning |
|---|---|
| `Immediate` | Responsive action should be taken immediately (e.g. Tornado Warning) |
| `Expected` | Responsive action should be taken soon (within the next hour) |
| `Future` | Responsive action should be taken in the near future (watches) |
| `Past` | Responsive action is no longer needed |
| `Unknown` | Urgency not known |

**Example:** Limit to only actively dangerous situations:
```json
"FilterUrgency": "Immediate,Expected"
```

### FilterCertainty

How likely the event is to occur.

| Value | Meaning |
|---|---|
| `Observed` | Determined to have occurred or ongoing (e.g. confirmed tornado on ground) |
| `Likely` | Likely (> ~50% probability) |
| `Possible` | Possible but not likely (watches, outlooks) |
| `Unlikely` | Not expected to occur |
| `Unknown` | Certainty not known |

**Example:** Only post confirmed or highly likely events:
```json
"FilterCertainty": "Observed,Likely"
```

### Per-Platform Severity Filter (`MinSeverity`)

Each platform has an optional `MinSeverity` field that lets you restrict which alerts that platform
receives, independently of the feed-level `FilterSeverity`. Leave it empty to pass everything that
the feed-level filter allows through.

**Example — Pushover gets all alerts, social media gets only Severe, Extreme:**

```json
"Nws": {
  "FilterSeverity": ""
},
"Facebook":  { "MinSeverity": "Severe,Extreme" },
"Instagram": { "MinSeverity": "Severe,Extreme" },
"X":         { "MinSeverity": "Severe,Extreme" },
"Bluesky":   { "MinSeverity": "Severe,Extreme" },
"Mastodon":  { "MinSeverity": "Severe,Extreme" },
"Pushover":  { "MinSeverity": "" },
"Twilio":    { "MinSeverity": "" }
```

`Nws.FilterSeverity` is applied server-side at the NWS API — it sets the floor for what gets
fetched at all. Per-platform `MinSeverity` and `EventTypes` are applied client-side after the
fetch. A platform cannot receive alerts below the feed-level floor, only above it.

**Example — social media gets only tornado alerts; Pushover gets everything:**

```json
"Nws": { "FilterSeverity": "" },
"Facebook": { "EventTypes": "Tornado Warning,Tornado Watch" },
"X":        { "EventTypes": "Tornado Warning,Tornado Watch" },
"Bluesky":  { "EventTypes": "Tornado Warning,Tornado Watch" },
"Pushover": { "EventTypes": "" }
```

`MinSeverity` and `EventTypes` can be combined on the same platform — both must pass for the
alert to be posted.

When a platform is skipped due to either filter, the bot logs:
```
info: Facebook, X, Bluesky: Skipped [Severe] Flood Watch — platform filter.
```

### FilterEventTypes

Override all severity/urgency/certainty filters and post only specific named event types.
Leave blank to rely on severity/urgency/certainty filtering instead. (Each platform also has
its own separate `EventTypes` field — see [Per-Platform Severity Filter](#per-platform-severity-filter-minseverity)
above — which further restricts what that specific platform receives, independent of this one.)

```json
"FilterEventTypes": "Tornado Warning,Tornado Watch,Flash Flood Warning"
```

See the complete list of event types in the next section.

---

## Complete NWS Event Type Reference

See **[NWS_EVENT_TYPES.md](NWS_EVENT_TYPES.md)** for the full list of event types sorted by
severity (Minor → Moderate → Severe → Extreme), including type (Warning/Watch/Advisory),
severity classification, and notes for each product.

The short version: the **Severity** column in that file shows the value(s) the NWS API returns
for each event type — the same values used in `FilterSeverity` and `MinSeverity` config fields.
Where two values are listed (e.g. `Severe, Extreme`), the NWS assigns severity at issuance time —
to reliably catch that event type, include **both** values in your filter.

---

## Recommended Filter Configurations

### Life-threatening emergencies only
```json
"FilterSeverity": "Extreme",
"FilterUrgency":  "Immediate"
```
Posts: Tornado Warnings, Flash Flood Emergencies, Storm Surge Warnings, Tsunami Warnings

### All warnings (no watches or advisories)
```json
"FilterSeverity":   "Severe,Extreme",
"FilterUrgency":    "",
"FilterCertainty":  "",
"FilterEventTypes": ""
```

### Warnings and watches (no advisories)
```json
"FilterSeverity":   "Moderate,Severe,Extreme",
"FilterUrgency":    "",
"FilterCertainty":  "Observed,Likely,Possible",
"FilterEventTypes": ""
```

### Specific event types only
```json
"FilterSeverity":   "",
"FilterEventTypes": "Tornado Warning,Tornado Watch,Severe Thunderstorm Warning,Flash Flood Warning,Flash Flood Watch"
```

### Everything (high volume — use with specific zone filtering)
```json
"FilterSeverity":   "",
"FilterUrgency":    "",
"FilterCertainty":  "",
"FilterEventTypes": ""
```

---

## Additional Alert Feeds: SPC Outlooks, SPC MCDs, HWO, WPC ERO

Beyond regular NWS warnings/watches/advisories, the bot can monitor four more NOAA products for
your configured `Location.Zones`/`Counties`. Each is independently `Enabled`, self-throttled by
its own `CheckIntervalSeconds`, and delivered through the same per-platform pipeline as everything
else — gated by a per-platform `Include*` flag (see
[Configuration Reference](docs/TECHNICAL.md#configuration-reference)). Full "how it works" detail
and per-platform severity-mapping tables for each are in docs/TECHNICAL.md, linked below.

| Feed | What it is | Enable it | Details |
|---|---|---|---|
| **SPC Convective Outlook** | Day 1/2 severe thunderstorm risk (tornado/wind/hail %) for your area | `"Spc": { "Enabled": true, "CheckIntervalSeconds": 1800 }` | [docs/TECHNICAL.md](docs/TECHNICAL.md#spc-convective-outlook-monitoring--how-it-works) |
| **SPC Mesoscale Discussion (MCD)** | Short-fuse (1–3h) severe weather potential, ahead of/alongside active watches | `"SpcMcd": { "Enabled": true, "CheckIntervalSeconds": 300 }` | [docs/TECHNICAL.md](docs/TECHNICAL.md#spc-mesoscale-discussion-monitoring--how-it-works) |
| **Hazardous Weather Outlook (HWO)** | Plain-text 7-day hazard summary from your local NWS office | `"Hwo": { "Enabled": true, "CheckIntervalSeconds": 300 }` | [docs/TECHNICAL.md](docs/TECHNICAL.md#hazardous-weather-outlook-hwo--how-it-works) |
| **WPC Excessive Rainfall Outlook (ERO)** | Day 1/2/3 flash-flood-guidance-exceedance risk | `"Ero": { "Enabled": true, "CheckIntervalSeconds": 1800 }` | [docs/TECHNICAL.md](docs/TECHNICAL.md#wpc-excessive-rainfall-outlook-ero--how-it-works) |

HWO is long-form text (no map image) intended primarily for personal use — its per-platform flag,
`IncludeHwo`, defaults to `false` (opt-in) rather than `true` like the other three. A common setup
is enabling it only on a personal Discord DM or Telegram chat:

```json
"DiscordDm": {
  "Enabled": true,
  "IncludeHwo": true
}
```

---

## API Credentials

### Facebook Page
Requires a Meta Developer account. For posting only to Pages you personally administer, you do
**not** need to submit for App Review — that's only required for Advanced Access (posting to
Pages you don't own) or distributing the app to other users.

**Getting a never-expiring Page Access Token** (this is the part that trips people up — follow
this exact order):

1. Create an app at [developers.facebook.com](https://developers.facebook.com/) → My Apps →
   Create App → type **Business**. Note the **App ID** and **App Secret** (Settings → Basic).
2. In [Graph API Explorer](https://developers.facebook.com/tools/explorer/), select your app,
   generate a **User Access Token** with permissions `pages_show_list`, `pages_read_engagement`,
   `pages_manage_posts`. This works immediately in Development Mode for Pages you admin — no
   review needed.
3. Exchange it for a **long-lived User Access Token** (~60 days):
   ```
   GET https://graph.facebook.com/v25.0/oauth/access_token
     ?grant_type=fb_exchange_token
     &client_id={app-id}
     &client_secret={app-secret}
     &fb_exchange_token={short-lived-token}
   ```
4. Use the **long-lived user token** (not the short-lived one) to derive the Page token:
   ```
   GET https://graph.facebook.com/v25.0/{page-id}?fields=access_token
     &access_token={long-lived-user-token}
   ```
   The returned `access_token` is your never-expiring Page token. Find your Page ID the same
   way you'd normally look up account info — Business Settings → Accounts → Pages → click your
   Page, or from the `me/accounts` response if you used that instead of a direct page ID.
5. Verify in the [Access Token Debugger](https://developers.facebook.com/tools/debug/accesstoken/)
   — **Expires** should say **Never**. Use this for `Facebook.PageAccessToken`.

> **Gotcha — Business Manager System User tokens didn't work for us:** generating a Page token
> via Business Settings → System Users → Generate New Token (even with `pages_read_engagement` +
> `pages_manage_posts` scopes confirmed in the Access Token Debugger, and the System User granted
> Full control on the Page) still failed with:
> `(#200) ... requires both pages_read_engagement and pages_manage_posts as an admin with
> sufficient administrative permission`. Deriving the token from a personal long-lived **User**
> token (steps 1–5 above) worked on the first try. If you hit this error, skip the System User
> route entirely and use the user-token derivation method instead.
>
> Also check **App Dashboard → Use cases → Testing your use cases** — newer Meta apps show a
> "0 of 1 API call(s) required" checklist per permission (`pages_manage_posts`,
> `pages_read_engagement`, etc.) under a use case like "Manage everything on your Page." We're not
> certain this gates Standard Access, but if you're still stuck after the steps above, satisfying
> that checklist via Graph API Explorer is worth trying — results can take up to 24 hours to
> register.

- **Note:** Automated posting to personal profiles is not supported by the API.
- When a map image is available, posts go to `/photos` instead of `/feed` automatically — no
  extra permission needed beyond `pages_manage_posts`.

### Instagram
Uses the same Meta Developer app as Facebook.
- Requires an **Instagram Professional (Business or Creator) account** linked to a Facebook Page
- Requires `instagram_content_publish` permission
- **Instagram requires an image with every post.** Set `ImageUrl` to a publicly accessible
  image hosted on your server (e.g. a branded weather alert graphic).
- Guide: https://developers.facebook.com/docs/instagram-platform/content-publishing

### X (Twitter)
- Developer portal: https://developer.twitter.com/
- Requires OAuth 1.0a credentials: API Key, API Secret, Access Token, Access Token Secret
- Set app permissions to **Read and Write**
- Free tier: 500 posts/month. Basic tier ($100/month): 3,000 posts/month
- A busy severe weather season can exceed the free tier
- `MaxPostsPerMonth` (default `500`, matching the free tier) is a client-side quota guard —
  once reached, further posts are skipped and logged (`X: Monthly post quota reached...`)
  instead of burning requests X would reject anyway. Raise it to `3000` if you're on the Basic
  tier, or set it to `0` to disable the guard entirely. The counter persists across restarts in
  `x_post_count.txt` (rolling 30-day window).
- When a map image is available, it's downloaded and uploaded via the v1.1 media endpoint before
  the tweet posts — no extra credentials needed, same OAuth 1.0a tokens are reused.

### Bluesky
- No developer account or review process required
- Generate an **App Password** (not your main password): bsky.app → Settings → Privacy and
  Security → App Passwords
- Free, no rate limit concerns for typical alert volumes
- When a map image is available, it's downloaded and uploaded as a blob, then embedded in the
  post — no extra setup needed.

### Mastodon
- No developer account required
- Generate an access token from your instance: Settings → Development → New Application
- Set scope to `write:statuses`
- Free, open source, no rate limit concerns for typical alert volumes
- When a map image is available, it's downloaded and uploaded via the media endpoint, then
  attached to the status — no extra setup needed.

### Discord (Webhook — channel posts)
- No developer account required — uses Incoming Webhooks
- In your server: **Server Settings → Integrations → Webhooks → New Webhook**
- Pick the channel the webhook should post to, then **Copy Webhook URL**
- Add each URL to the `WebhookUrls` list in `appsettings.json` — one entry per channel/server
- Optionally set `Username` to override the display name shown for these messages
- All webhooks receive every alert concurrently
- Free, no rate limit concerns for typical alert volumes
- Each alert is posted as a rich embed, color-coded by severity (red = Extreme, orange = Severe,
  yellow = Moderate, green = Minor)
- **Note:** if you had `WebhookUrl` (singular) in your local config from a previous version,
  rename it to `WebhookUrls` and wrap the value in a JSON array: `["https://..."]`
- API docs: https://discord.com/developers/docs/resources/webhook#execute-webhook

### Discord DM (Bot — direct messages to users)
- Delivers alerts as direct messages to one or more Discord users via a bot
- **Create a bot:**
  1. Go to https://discord.com/developers/applications → **New Application**
  2. Go to **Bot** → **Reset Token** → copy the token → set `BotToken` in config
  3. No special OAuth scopes are needed — the bot only sends DMs, it never joins a server
- **Add the bot to a shared server** (required — Discord only allows bots to DM users they share a server with):
  1. Under **OAuth2 → URL Generator**, select scope `bot` with no permissions
  2. Open the generated URL and add the bot to any server you and the recipients are in
- **Find user IDs:**
  - Enable **Developer Mode** in Discord: Settings → Advanced → Developer Mode
  - Right-click any user → **Copy User ID**
  - Add each ID to `UserIds` in config: `["123456789012345678"]`
- Each alert is sent as a rich embed, color-coded by severity, same as the webhook format
- The DM channel for each user is opened once and cached for the session
- API docs: https://discord.com/developers/docs/resources/channel#create-message

### Telegram
- No developer account required — uses the Telegram Bot API
- Message [@BotFather](https://t.me/BotFather) on Telegram, send `/newbot`, follow the prompts,
  and copy the bot token it gives you
- **Find your `ChatId`:**
  - Private chat: open a chat with your new bot in Telegram and send it any message (e.g. "hi")
    — `getUpdates` only returns messages received after you start polling, so do this first or
    the response below will be an empty `result: []`. Then visit
    `https://api.telegram.org/bot<YourBotToken>/getUpdates` in a browser and read `chat.id`
    from the JSON response — or message [@userinfobot](https://t.me/userinfobot) to get your
    own numeric ID
  - Group: add the bot to the group and send a message in the group (group privacy mode may
    require the message to mention the bot or be a command — disable it via BotFather's
    `/setprivacy` if needed), then read `chat.id` from `getUpdates` as above; group IDs are
    negative numbers
  - Channel: add the bot as an **admin** of the channel, then use the channel's `@username`
    (if public) or its numeric `-100...` ID as `ChatId`
- Set `BotToken` and `ChatId` in `appsettings.json`
- Free, no rate limit concerns for typical alert volumes
- API docs: https://core.telegram.org/bots/api

---

## Push / SMS Notifications

The bot supports several notification providers simultaneously. Enable any combination in
`appsettings.json` by setting `"Enabled": true`. All of them send at the same time as social
media posts, running concurrently.

---

### Pushover (Push Notification)

**Cost:** $5 one-time per platform (iOS or Android)
**Latency:** Near-instant (<5 seconds typical)

Pushover delivers rich push notifications with support for priority levels that bypass Do Not
Disturb — critical for 3am tornado warnings.

**Setup:**
1. Purchase the Pushover app: https://pushover.net/ ($5 one-time, 30-day free trial)
2. Create an application token at https://pushover.net/apps/build
3. Copy your User Key from https://pushover.net/
4. Set `ApiToken` and `UserKey` in `appsettings.json`

**Priority levels:**

| Value | Behavior |
|---|---|
| `-2` | No notification, no sound (lowest) |
| `-1` | Quiet — notification delivered silently |
| `0` | Normal |
| `1` | High — always sounds, bypasses user quiet hours |
| `2` | Emergency — repeats every `EmergencyRetrySeconds` until acknowledged, bypasses DND |

**Recommended configuration for weather alerts:**
```json
"Pushover": {
  "Enabled": true,
  "DefaultPriority": 1,
  "ExtremePriority": 2,
  "EmergencyRetrySeconds": 60,
  "EmergencyExpireSeconds": 3600,
  "Sound": "siren"
}
```

This sends Severe alerts as Priority 1 (always sounds) and Extreme alerts (Tornado Warning,
Flash Flood Emergency) as Priority 2 (repeats every 60 seconds for up to 1 hour until you
acknowledge on your phone).

**Available sounds:** pushover, bike, bugle, cashregister, classical, cosmic, falling, gamelan,
incoming, intermission, magic, mechanical, pianobar, siren, spacealarm, tugboat, alien, climb,
persistent, echo, updown, vibrate, none

API docs: https://pushover.net/api

---

### Twilio (SMS)

**Cost:** ~$1.15/month for a phone number + ~$0.0079 per SMS (US)
**Latency:** Typically 2–10 seconds; depends on carrier

SMS works on any phone without a data connection or app install. Useful for sending alerts to
multiple recipients (family members, staff) or as a backup for when push notifications may
not be reliable.

**Setup:**
1. Create a Twilio account: https://console.twilio.com/
2. Purchase a phone number (~$1.15/month)
3. Copy your Account SID and Auth Token from the console dashboard
4. For US toll-free numbers, complete toll-free verification (required to message US numbers)
5. Fill in `AccountSid`, `AuthToken`, `FromNumber`, and `ToNumbers` in `appsettings.json`

**Multiple recipients:**
```json
"Twilio": {
  "Enabled": true,
  "ToNumbers": ["+15559876543", "+15557654321", "+15552223333"]
}
```
Each recipient receives a separate SMS. Each message is billed individually.

**SMS length:** The bot keeps messages to 320 characters (2 SMS segments) to control cost.
At $0.0079/segment × 3 recipients = ~$0.047 per alert event for 3 people.

**Cost guard:** `MaxSmsPerDay` (default `100`) caps the number of individual SMS sends per
rolling 24-hour window, counted per recipient (one alert to 3 `ToNumbers` counts as 3) — protects
against runaway Twilio charges during a busy severe weather outbreak. Once reached, further sends
are skipped and logged (`Twilio: Daily SMS cost guard reached...`). Set it to `0` to disable. The
counter persists across restarts in `twilio_sms_count.txt`.

**MMS (map images):** When a `MapImageUrl` is available (Mapbox alert maps, SPC outlook maps —
see [Map Images](#map-images-mapbox)), it's sent as `MediaUrl`, turning the message into MMS.
Twilio fetches the image itself; no extra setup needed. MMS pricing is higher than SMS — check
your [Twilio pricing page](https://www.twilio.com/en-us/sms/pricing/us) before enabling on a
high-volume feed.

API docs: https://www.twilio.com/docs/messaging/api/message-resource

---

### VoIP.ms (SMS)

**Cost:** SMS sending is free with a VoIP.ms DID that supports SMS; standard per-minute/DID
fees apply for the underlying number.
**Latency:** Typically a few seconds

An alternative to Twilio for users who already have a VoIP.ms account and a DID with SMS
enabled. Uses the VoIP.ms REST API directly — no SDK required.

**Setup:**
1. Make sure your DID has SMS enabled: VoIP.ms portal → DID Management → edit your DID →
   enable SMS
2. Enable API access and set an API password: Main Menu → SOAP and REST/JSON API
   (https://voip.ms/m/api.php) — note this is a separate password from your account login
3. Add your server's public IP address to the API allow list on the same page. Find your
   server's public IP with `curl -4 ifconfig.me` — **not** its Tailscale IP if you're using
   Tailscale for anything else. Requests from a non-allow-listed IP don't get a clean error —
   the connection just hangs for about 100 seconds, which looks identical to a network/firewall
   problem in the logs.
4. Fill in `ApiUsername`, `ApiPassword`, `Did` (digits only, no `+` or punctuation), and
   `ToNumbers` in `appsettings.json`

**Multiple recipients:**
```json
"VoipMs": {
  "Enabled": true,
  "Did": "5551234567",
  "ToNumbers": ["5559876543", "5557654321"]
}
```

Each recipient receives a separate SMS.

**SMS length:** Messages are kept to 160 characters (1 SMS segment). Non-ASCII characters
(emoji, em-dashes, smart quotes) are stripped/normalized to plain ASCII before sending — VoIP.ms's
API has been observed rejecting Unicode message content outright with a generic "Bad Request"
SOAP fault rather than a normal error response.

API docs: https://voip.ms/m/apidocs.php (method: `sendSMS`)

---

### Using Multiple Providers Together

All providers can be enabled simultaneously and run concurrently per alert. A common
combination:

```json
"Pushover": { "Enabled": true, ... },   // Primary push for your phone
"Telegram": { "Enabled": true, ... },   // Free secondary push / family alerts
"Twilio":   { "Enabled": false, ... }   // SMS disabled unless needed
```

If one provider fails (network error, auth failure), the others are unaffected — each wraps
its delivery independently.

---

## Startup Confirmation

On first run, the bot sends a one-time confirmation message to every enabled platform so you
can verify credentials are working before real alerts start flowing.

### How It Works

1. At startup, the bot checks `confirmed_platforms.txt` in the working directory
2. For each enabled platform **not yet in that file**, it sends a single test message
3. On success, the platform name is written to the file — it will never receive another
   confirmation message, even after a restart
4. If a platform fails, it is **not** recorded and will be retried on the next startup

### What the Confirmation Looks Like

> ✅ NWS Alert Bot — connection confirmed on Monday, June 8, 2026 at 9:15 AM.
> This is a one-time test message. Please delete it once you have verified it arrived.

**Delete the test post from each platform once you have confirmed it arrived.** The bot has
no way to delete its own posts — this is intentional.

### Re-triggering Confirmation

To re-send a confirmation to a platform (e.g. after changing credentials):

1. Open `confirmed_platforms.txt` in the working directory
2. Delete the line containing that platform's name
3. Restart the bot — it will re-send the confirmation to only that platform

To re-confirm all platforms, delete `confirmed_platforms.txt` entirely and restart.

### Confirmation Behavior Per Platform

| Platform | Confirmation priority | Notes |
|---|---|---|
| Facebook | Normal post | Same as an alert post |
| Instagram | Normal post with image | Requires `ImageUrl` to be set |
| X | Normal tweet | Counts against your monthly post limit |
| Bluesky | Normal post | |
| Mastodon | Normal post | |
| Pushover | Priority 0 (normal, no DND bypass) | Intentionally lower priority for test |
| Twilio | SMS to all `ToNumbers` | Each recipient billed separately |
| VoIP.ms | SMS to all `ToNumbers` | Sent from your DID |
| Discord | Plain text message | No embed for the confirmation message |
| DiscordDm | Plain text DM to each configured user | No embed for the confirmation message |
| Telegram | Plain text message | No photo for the confirmation message |

---

## Image Smoke Test

Startup confirmation (above) is **text-only** — it never exercises the image-attachment code
(Facebook `/photos`, X media upload, Bluesky `uploadBlob`, Mastodon media upload, Twilio MMS,
Discord embed image, Telegram photo). To verify that code actually works against your live
accounts, run:

```bash
dotnet run -- --smoke-test-image
```

This posts one synthetic test alert — with a real, publicly-hosted test image attached (an SPC
outlook plot from IEM Mesonet, the same source used for SPC outlook map images; no Mapbox token
required) — to every **enabled** platform that supports images: Facebook, Instagram, X, Bluesky,
Mastodon, Discord, Telegram, and Twilio (MMS). It logs a per-platform OK/FAILED result and
**exits immediately** without starting the live polling loop — it does not touch
`confirmed_platforms.txt` or `posted_alerts.txt`, so it's safe to re-run as many times as you want
while debugging a platform.

**This is a live action** — it posts to your real accounts/channels and (for Twilio) sends a
real billed MMS. Delete the test posts once you've confirmed the image rendered correctly.

Pushover and VoIP.ms are skipped (no image support); platforms that are disabled are skipped too.
If nothing is enabled, it logs a warning and exits without posting anything.

---

## Map Images (Mapbox)

When enabled, the bot generates a map image for each alert (NWS warnings/watches/advisories, plus
SPC Outlook and WPC ERO posts) and attaches it to every platform that supports images. Most maps
come from a free IEM service requiring no setup on your part; Mapbox is used as a fallback for
alerts with no VTEC code. See
[docs/TECHNICAL.md — Map Images](docs/TECHNICAL.md#map-images--internals) for how the bot decides
which source to use.

### Setup

1. Create a free account at [account.mapbox.com](https://account.mapbox.com/)
2. Copy your **default public token** (starts with `pk.`) from the Tokens page
3. In `appsettings.Local.json`:

```json
{
  "Map": {
    "Enabled": true,
    "AccessToken": "pk.YOUR_MAPBOX_TOKEN"
  }
}
```

### Configuration

| Field | Description | Default |
|---|---|---|
| `Enabled` | Whether to generate map images | `false` |
| `AccessToken` | Mapbox public token (starts with `pk.`) | `""` |
| `Style` | Mapbox style ID, format `{username}/{style_id}` | `"mapbox/outdoors-v12"` |
| `Width` | Image width in pixels (max 1280) | `600` |
| `Height` | Image height in pixels (max 1280) | `400` |

**Available built-in styles:** `mapbox/outdoors-v12`, `mapbox/streets-v12`, `mapbox/light-v11`,
`mapbox/dark-v11`, `mapbox/satellite-streets-v12`

**Free tier:** 50,000 static map images per month — more than sufficient for a weather bot.

### Platform behavior

| Platform | Map behavior |
|---|---|
| Facebook | Image is downloaded and uploaded as multipart `source` to `/photos`. If download fails, the post still goes to `/feed` as text-only. |
| Instagram | Uses the map URL instead of the static `ImageUrl`. Falls back to `ImageUrl` if no map is available. Instagram Graph API requires a public URL — direct upload is not supported. |
| X (Twitter) | Image is downloaded and uploaded via the v1.1 media endpoint, then attached to the tweet by `media_id`. If upload fails, the tweet still posts as text-only. |
| Bluesky | Image is downloaded and uploaded via `uploadBlob`, then attached as an `app.bsky.embed.images` embed. If upload fails, the post still goes out as text-only. |
| Mastodon | Image is downloaded and uploaded via the media endpoint, then attached by `media_ids[]`. If upload fails, the status still posts as text-only. |
| Discord | Image is downloaded and uploaded as a file attachment (`files[0]`), then referenced in the embed via `attachment://map.png`. If download fails, the embed posts without the image. |
| Discord DM | Same as Discord webhook — image is downloaded and sent as a file attachment. If download fails, the embed posts without the image. |
| Telegram | Image is downloaded and uploaded via multipart `sendPhoto`. If download fails, falls back to `sendMessage` using the full 4,096-character limit instead of the 1,024-character caption limit. |
| Twilio | Sent as MMS via the `MediaUrl` field — Twilio fetches the URL itself. Direct upload is not supported by the Twilio REST API. |
| Pushover, VoIP.ms | No change — text-only. |
