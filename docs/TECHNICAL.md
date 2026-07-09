# NwsAlertBot — Technical Reference

This document is the "blueprint room": architecture, internals, and the complete configuration
field reference. If you just want to get the bot running and configured, see
[README.md](../README.md). If you want to set up a local dev environment, run tests, or cut a
release, see [CONTRIBUTING.md](../CONTRIBUTING.md). For a running history of changes, see
[CHANGELOG.md](../CHANGELOG.md).

---

## Table of Contents

1. [Architecture](#architecture)
2. [Running as a Service — Full Reference](#running-as-a-service--full-reference)
3. [Auto-Update — Full Reference](#auto-update--full-reference)
4. [Configuration Reference](#configuration-reference)
5. [SPC Convective Outlook Monitoring — How It Works](#spc-convective-outlook-monitoring--how-it-works)
6. [SPC Mesoscale Discussion Monitoring — How It Works](#spc-mesoscale-discussion-monitoring--how-it-works)
7. [Hazardous Weather Outlook (HWO) — How It Works](#hazardous-weather-outlook-hwo--how-it-works)
8. [WPC Excessive Rainfall Outlook (ERO) — How It Works](#wpc-excessive-rainfall-outlook-ero--how-it-works)
9. [Map Images — Internals](#map-images--internals)
10. [References](#references)

---

## Architecture

`AlertPollingService` (defined at the bottom of `Program.cs`, registered as a `BackgroundService`)
drives the main loop:

1. On first run, calls `StartupConfirmationService.RunAsync()` — sends a one-time test message to
   every enabled platform not yet in `confirmed_platforms.txt`. Each platform is confirmed
   independently.
2. Enters the poll loop: calls `SocialMediaOrchestrator.RunAsync()`, then sleeps for
   `PollIntervalSeconds`.
3. Each `SocialMediaOrchestrator` cycle: fetches active alerts from `NwsAlertService` → filters via
   `AlertTrackerService.HasBeenPosted()` → posts new alerts to all platforms concurrently via
   `Task.WhenAll()` → marks each posted alert via `AlertTrackerService.MarkPosted()`.
4. `NwsAlertService.BuildUrl()` pushes all filters (zone/county/state, severity, urgency, certainty,
   event type) to the NWS API as query parameters — no client-side filtering.
5. `NwsAlert.FormatPost(maxLength)` formats and truncates the alert text for each platform's
   character limit.
6. After the main NWS loop, the same `RunAsync` cycle also checks `SpcOutlookService`,
   `SpcMcdService`, `HwoService`, and `WpcEroService` (if each is individually `Enabled`) —
   separate synthetic-alert feeds with their own severity assignment (not the NWS `Filter*` gate),
   each self-throttled by its own `CheckIntervalSeconds` in `appsettings.json` rather than the main
   `PollIntervalSeconds`. Posted through the same per-platform pipeline as NWS alerts, gated by each
   platform's own `IncludeSpcOutlooks`/`IncludeSpcMcd`/`IncludeHwo`/`IncludeEro` flags.

**DI pattern**: Settings classes are bound once in `Program.cs` and registered as singletons. Every
platform service receives its typed `{Platform}Settings` singleton via constructor injection. All
platform `HttpClient`s are registered with `AddHttpClient<T>()`.

**Logging**: Serilog writes daily rolling files to `logs/`, retained for 30 days by default. The
retention window (`retainedFileCountLimit`) is set in the `UseSerilog` call in `Program.cs` — a
compile-time value, not an `appsettings.json` field, so changing it requires building from source.

**Adding a new platform** follows an established pattern — settings class, service class, DI
registration, injection into both `SocialMediaOrchestrator` and `StartupConfirmationService`,
config block, README setup section. See `CLAUDE.md`'s "Non-Negotiable Rules" section (repo root)
for the full checklist; it's written for an AI coding assistant but applies equally to a human
contributor.

---

## Running as a Service — Full Reference

See the README's [Running as a Service](../README.md#running-as-a-service) for the basic commands.
This section covers the flags and internals not needed for a first-time setup.

### Running more than one instance on the same machine

Each instance needs its own install directory (its own copy of the executable,
`appsettings.json`, and `appsettings.Local.json`) and its own service name. Set the name **once**,
in `Update.ServiceName` in that instance's `appsettings.json` — both `setup-service.ps1` and
`update.ps1` read it from there automatically if you don't pass `-ServiceName` explicitly, so
there's a single place to get it right instead of typing the same string into two separate
commands and risking a mismatch:

```json
// /opt/nwsalertbot-serverone/appsettings.json
"Update": { "ServiceName": "nwsalertbot-serverone", ... }
```
```json
// /opt/nwsalertbot-servertwo/appsettings.json
"Update": { "ServiceName": "nwsalertbot-servertwo", ... }
```

```bash
sudo ./setup-service.ps1 -InstallDir /opt/nwsalertbot-serverone
sudo ./setup-service.ps1 -InstallDir /opt/nwsalertbot-servertwo
```

If you'd rather not touch `appsettings.json`, `-ServiceName` still works as an explicit override
on either script — just remember it needs to match on both.

### What it does (and doesn't) do

- Creates the service pointed at the executable in `-InstallDir` (default: this script's own
  directory), with the working directory pinned there too — so `appsettings.json` and runtime
  state files are found/written in the right place regardless of how the service starts.
- Sets it to start automatically on boot and restart automatically on failure/crash
  (`Restart=always` on systemd; a 3-attempt restart policy on Windows).
- Does **not** touch `appsettings.json`, `appsettings.Local.json`, or install any credentials —
  set those up yourself first (see the README's [Setup](../README.md#setup) section).
- `-Uninstall` stops and removes the service registration only — it doesn't delete the
  executable, config, or any runtime state file. Also removes the passwordless-sudo rule below,
  if one was created.
- `-DryRun` reports exactly what would be created/removed without touching anything — safe to
  run without elevation.
- Errors out upfront (before creating anything) if `appsettings.json` is missing from
  `-InstallDir` — without this check you'd get a service that's created, starts, and immediately
  crash-loops instead of a clear message telling you what to fix.

### Passwordless sudo for `Update.AutoApply` (Linux)

If you plan to use [Auto-Update](#auto-update--full-reference)'s `AutoApply`, its restart step runs
`sudo systemctl restart <ServiceName>` non-interactively — without a `NOPASSWD` sudoers rule for
that exact command, it silently fails or hangs. Pass `-ConfigurePasswordlessSudo` to set this up
automatically:

```bash
sudo ./setup-service.ps1 -ServiceName nwsalertbot -ConfigurePasswordlessSudo
```

This writes a narrowly-scoped rule to `/etc/sudoers.d/<ServiceName>-update` granting only
`systemctl restart <ServiceName>` — not a blanket `systemctl` grant — and validates it with
`visudo -c` before installing it, so a malformed rule never actually reaches `/etc/sudoers.d/`.
Off by default: modifying sudoers is a real security-relevant change and shouldn't happen as a
silent side effect of installing a service.

### macOS

Not supported by this script yet — run the executable directly, or set up a `launchd` `.plist`
manually.

---

## Auto-Update — Full Reference

See the README's [Auto-Update](../README.md#auto-update) section for basic configuration and how
to run `update.ps1` manually. This section covers what happens under the hood, and the honest list
of gaps in unattended operation.

### What gets touched (and what doesn't)

The update only ever replaces the **executable** and **`update.ps1`/`setup-service.ps1`
themselves** (so future updater fixes apply on the next run too). It never touches
`appsettings.json`, `appsettings.Local.json`, or any runtime state file (`posted_alerts.txt`,
`confirmed_platforms.txt`, `logs/`, `x_post_count.txt`, `twilio_sms_count.txt`) — your
configuration and history survive every update. The old executable is backed up to
`NwsAlertBot.bak` (or `NwsAlertBot.exe.bak` on Windows) before being replaced, in case something
goes wrong.

**Restart behavior:** if a systemd service (Linux) or Windows Service matching `Update.ServiceName`
exists (see [Running as a Service](#running-as-a-service--full-reference)), it's restarted via
`systemctl`/`Restart-Service`. Otherwise the new executable is just launched directly — this
covers the common case of simply running the `.exe`/binary yourself with no service installed.

**Automatic rollback:** after starting the new version, `update.ps1` waits 15 seconds (configurable
via `-RollbackCheckDelaySeconds`) and checks whether it's still running/active. If not — it
crashed, or failed to start at all — the previous executable is automatically restored from its
`.bak` backup and restarted, so a bad release doesn't leave the bot down indefinitely with no
recovery. This is a lightweight check (just "did it not immediately crash," not real application
health — there's no health endpoint to call), but it catches the most common failure mode: a
release that doesn't start at all. If the rollback itself also fails to start, that's logged as an
error requiring manual intervention (this would mean something else is wrong — e.g. a corrupted
`.bak`, or the previous version had already stopped working for an unrelated reason).

**Checksum verification:** `release.yml` publishes a `checksums.txt` (SHA256, one line per asset)
alongside every release's archives. Before extracting anything, `update.ps1` downloads
`checksums.txt` and verifies the downloaded archive's hash matches — aborting with no changes made
if the entry is missing or doesn't match. This protects against a corrupted upload or transport-level
tampering. It does **not** protect against a genuinely compromised `release.yml`/repo/`GITHUB_TOKEN`
— an attacker who can push a malicious release can just as easily update `checksums.txt` to match.
Real supply-chain protection against that threat would need cryptographic signing, which is a much
bigger lift (key generation, secure storage, rotation) than this project's threat model currently
justifies.

### Known Limitations

This feature is aimed at unattended, "set and forget" use — which is also exactly where its
weakest points matter most, since nobody's watching. Worth understanding before turning on
`AutoApply` unattended:

- **Linux auto-restart requires passwordless `sudo`.** After swapping the executable,
  `update.ps1` runs `sudo systemctl restart $ServiceName` non-interactively. If the account
  running the bot doesn't have a `NOPASSWD` sudoers rule for that command, this silently fails
  (or hangs waiting for a password that will never come) — the binary gets updated, but the old
  process keeps running until you restart it yourself. `setup-service.ps1 -ConfigurePasswordlessSudo`
  sets this up automatically (a narrowly-scoped `/etc/sudoers.d/` rule for exactly
  `systemctl restart <ServiceName>`, validated with `visudo -c` before being installed) — off by
  default, since modifying sudoers is a real security-relevant change that shouldn't happen as a
  silent side effect of installing a service.
- **The Windows-Service self-stop-during-update path hasn't been verified against a real
  service.** `AutoApply` calls `StopApplication()` so `update.ps1` can swap the binary, while
  `setup-service.ps1` separately configures Windows failure-recovery to auto-restart the service
  if it crashes. In theory a clean, self-initiated stop reports `SERVICE_STOPPED` to the SCM and
  doesn't trigger recovery — but this hasn't been exercised against a live Windows Service, so
  there's a theoretical race if that assumption is wrong (recovery restarting the old exe while
  `update.ps1` is mid-copy).

See `docs/plans/auto-update-remaining-limitations.md` for a concrete write-up of the
Windows-Service race above, including how to actually verify it live.

If you want the peace of mind this feature is meant to provide, start with `AutoApply: false` and
run `update.ps1` by hand for a while, or watch the logs closely the first few times you enable it.

---

## Configuration Reference

All configuration lives in `appsettings.json`. Settings are grouped by what they govern:
`Location` and `Polling` are shared across every alert feed; `Nws`, `Spc`, `SpcMcd`, `Hwo`, and
`Ero` are each one specific feed's own settings; everything else is a delivery platform.

### Location — shared by every feed

`Zones`/`Counties`/`TimeZone` are resolved once and used identically by the NWS alerts feed,
SPC Outlook, SPC MCD, HWO, WPC ERO, and the Mapbox bounding-box fallback. None of those feeds
carry their own copy of this — if you add a new feed in the future, it should read from here too.

```json
"Location": {
  "Zones":    ["MOZ066", "MOZ067"],
  "Counties": ["MOC217", "MOC039"],
  "TimeZone": "America/Chicago"
}
```

| Field | Description | Default |
|---|---|---|
| `Zones` | NWS forecast zone codes (see the README's [Geographic Filtering](../README.md#geographic-filtering-zones-and-counties)) | `[]` |
| `Counties` | NWS county codes (see the README's [Geographic Filtering](../README.md#geographic-filtering-zones-and-counties)) | `[]` |
| `TimeZone` | IANA timezone ID for formatting Issued/Valid/Expires on all alert posts (NWS, SPC, HWO, and ERO). Works on Windows and Linux. | `"America/Chicago"` |

**Geographic filter:** `Zones` and `Counties` are combined into a single query — both sets of
UGC codes are always sent together, for every feed. `Nws.State` (below) is a fallback used
**only** by the main NWS alerts feed when both are empty — SPC Outlook, SPC MCD, HWO, and WPC
ERO always require explicit `Zones`/`Counties` and do not fall back to a whole state.

**US IANA timezone IDs:**

| Region | ID |
|---|---|
| Eastern | `America/New_York` |
| Central | `America/Chicago` |
| Mountain | `America/Denver` |
| Mountain (no DST — Arizona) | `America/Phoenix` |
| Pacific | `America/Los_Angeles` |
| Alaska | `America/Anchorage` |
| Hawaii | `Pacific/Honolulu` |

### Polling — master loop cadence

Drives how often the orchestrator checks all feeds. Each feed still self-gates on its own
`CheckIntervalSeconds` below; this only controls the overall tick. Only NWS alerts and SPC MCDs
ever trigger the accelerated (`ActiveAlert*`) window — SPC Outlook, HWO, and WPC ERO never do.

**Watches get their own expiry-based extension, independent of `ActiveAlertWindowHours`.** A
storm-triggering NWS alert whose `event` contains "Watch" (e.g. "Severe Thunderstorm Watch",
"Tornado Watch") sets `AlertPollingService._watchActiveUntilUtc` to that alert's own `ends`/`expires`
time (`SocialMediaOrchestrator.RunAsync` returns this alongside the storm count); accelerated
polling continues until whichever is later — the fixed `ActiveAlertWindowHours` from the last new
alert, or this Watch's own expiry. Warnings and every other event type only ever get the fixed
window. This exists because a Watch commonly runs longer than a 4h default (Severe Thunderstorm
Watches often run 6–8h) — without it, the bot drops back to idle polling while the Watch is still
in effect, and NWS only keeps a cancellation message in the active-alerts feed for a few minutes
after issuance (confirmed as short as ~16 minutes on a real cancel), so a missed poll window means
the cancellation is unrecoverable. Because cancellations are always downgraded to
`severity: Minor` (see below), a cancel message itself never sets or extends this window — only
the original Watch issuance (or an `Update` that revises its duration) does.

```json
"Polling": {
  "PollIntervalSeconds": 300,
  "ActiveAlertPollIntervalSeconds": 60,
  "ActiveAlertWindowHours": 4,
  "ActiveAlertMinSeverity": "Severe,Extreme"
}
```

| Field | Description | Default |
|---|---|---|
| `PollIntervalSeconds` | Idle poll interval in seconds — used when no active storm window is open | `300` |
| `ActiveAlertPollIntervalSeconds` | Accelerated poll interval in seconds while an active storm window is open | `60` |
| `ActiveAlertWindowHours` | Hours to stay in accelerated polling after the last new NWS alert; resets on each new NWS alert. SPC outlooks/HWO do not affect the storm window. Watches also get an independent expiry-based extension — see below. | `4` |
| `ActiveAlertMinSeverity` | Minimum severity for a new alert to trigger or extend accelerated polling mode. Alerts below this threshold are still posted but do not engage the faster poll interval. Leave empty to have any new alert trigger active mode. | `"Severe,Extreme"` |

### Nws — the main alerts feed's own query filters

```json
"Nws": {
  "State":                "MO",
  "FilterSeverity":       "Severe,Extreme",
  "FilterUrgency":        "",
  "FilterCertainty":      "",
  "FilterEventTypes":     "",
  "AdditionalEventTypes": ""
}
```

| Field | Description | Default |
|---|---|---|
| `State` | Two-letter state code fallback — only used if `Location.Zones`/`Location.Counties` are both empty, and only affects this feed | `""` |
| `FilterSeverity` | Comma-separated severity levels to include | `"Severe,Extreme"` |
| `FilterUrgency` | Comma-separated urgency levels to include | `""` (all) |
| `FilterCertainty` | Comma-separated certainty levels to include | `""` (all) |
| `FilterEventTypes` | Comma-separated event names to include | `""` (all) |
| `AdditionalEventTypes` | Comma-separated event types to always fetch regardless of `FilterSeverity`. Use to include specific lower-severity events (e.g. advisories) alongside a severity filter. Makes a separate API call and merges results. | `""` (none) |

The `Filter*` naming is deliberate: these four all narrow the feed at the server level, sent
directly to `api.weather.gov` as query parameters, so anything excluded here is never even
returned to the bot. Nothing downstream (per-platform `MinSeverity`/`EventTypes`) can un-filter
it. `AdditionalEventTypes` is the one exception — it's additive, not restrictive, which is why
it doesn't get the `Filter` prefix. This only governs the main NWS alerts feed (regular
warnings/watches/advisories + SPS) — SPC Outlook, SPC MCD, HWO, and WPC ERO below are separate
feeds with their own severity values, gated only by each platform's own `MinSeverity`. For
practical recommended combinations of these fields, see the README's
[Alert Filtering](../README.md#alert-filtering) section.

`Cancel` messages are fetched via a second, unconditional query with no severity/urgency/certainty
filter (`FilterEventTypes` still applies to it). This exists because NWS downgrades every
cancellation to `severity: Minor`, `urgency: Past`, `certainty: Observed` regardless of the
original event's actual severity — a normal `FilterSeverity` that excludes `Minor` (true of every
recommended config in the README except "everything") would otherwise silently drop every single
cancellation before the bot ever saw it.

Each platform block also accepts:

| Field | Description | Default |
|---|---|---|
| `MinSeverity` | Comma-separated severity levels for this platform only. Blank means "accept everything that already passed the feed's own filter above." | `""` (inherit) |
| `EventTypes` | Comma-separated NWS event names for this platform only. Leave empty to receive all event types. | `""` (all) |
| `IncludeSpcOutlooks` | Whether SPC Convective Outlook alerts are posted to this platform. Requires `Spc.Enabled = true`. | `true` |
| `IncludeSpcMcd` | Whether SPC Mesoscale Discussion alerts are posted to this platform. Requires `SpcMcd.Enabled = true`. | `true` |
| `IncludeHwo` | Whether Hazardous Weather Outlook text posts are sent to this platform. Requires `Hwo.Enabled = true`. Defaults to `false` — HWO is long-form text intended for personal use, enable it selectively (e.g. a Discord DM or Telegram chat). | `false` |
| `IncludeEro` | Whether WPC Excessive Rainfall Outlook alerts are posted to this platform. Requires `Ero.Enabled = true`. | `true` |

`Spc` (see [SPC Convective Outlook Monitoring](#spc-convective-outlook-monitoring--how-it-works)):

```json
"Spc": {
  "Enabled": false,
  "CheckIntervalSeconds": 1800
}
```

| Field | Description | Default |
|---|---|---|
| `Enabled` | Whether to monitor SPC Day 1/Day 2 Convective Outlooks | `false` |
| `CheckIntervalSeconds` | Minimum seconds between SPC outlook checks | `1800` |

`SpcMcd` (see [SPC Mesoscale Discussion Monitoring](#spc-mesoscale-discussion-monitoring--how-it-works)):

```json
"SpcMcd": {
  "Enabled": false,
  "CheckIntervalSeconds": 300
}
```

| Field | Description | Default |
|---|---|---|
| `Enabled` | Whether to monitor SPC Mesoscale Discussions for the monitored area | `false` |
| `CheckIntervalSeconds` | Minimum seconds between MCD checks (MCDs expire in 1–3 h) | `300` |

Per-platform, MCDs are controlled by the separate `IncludeSpcMcd` flag (independent of
`IncludeSpcOutlooks`), so you can enable or suppress each product type per platform.
Each new MCD also triggers expedited polling (same as a severe/extreme NWS alert).

`Hwo` (see [Hazardous Weather Outlook (HWO)](#hazardous-weather-outlook-hwo--how-it-works)):

```json
"Hwo": {
  "Enabled": false,
  "CheckIntervalSeconds": 300
}
```

| Field | Description | Default |
|---|---|---|
| `Enabled` | Whether to monitor the Hazardous Weather Outlook text product | `false` |
| `CheckIntervalSeconds` | Minimum seconds between HWO checks | `300` |

Per-platform delivery is controlled by the separate `IncludeHwo` flag, which defaults to
`false` (opt-in) since HWO is long-form text intended primarily for personal use.

`Ero` (see [WPC Excessive Rainfall Outlook (ERO)](#wpc-excessive-rainfall-outlook-ero--how-it-works)):

```json
"Ero": {
  "Enabled": false,
  "CheckIntervalSeconds": 1800
}
```

| Field | Description | Default |
|---|---|---|
| `Enabled` | Whether to monitor the WPC Excessive Rainfall Outlook Day 1/2/3 categorical risk | `false` |
| `CheckIntervalSeconds` | Minimum seconds between ERO checks | `1800` |

Per-platform delivery is controlled by the separate `IncludeEro` flag (default `true`).
Note: despite sitting alongside `Spc`/`SpcMcd` in this list, ERO is a WPC (Weather Prediction
Center) product, not SPC.

---

## SPC Convective Outlook Monitoring — How It Works

In addition to NWS warnings/watches/advisories, the bot can separately monitor the
[SPC (Storm Prediction Center)](https://www.spc.noaa.gov/) Day 1 and Day 2 Convective Outlooks
and alert when a monitored location is in any non-"None" categorical risk — a general
thunderstorm risk (`TSTM`) or higher. The same notification bundles that location's tornado,
wind, and hail probability for the day.

- **Locations monitored** are derived from the same `Location.Zones` (or `Location.Counties` if
  `Zones` is empty) already configured for warning geo-filtering — there is no separate
  location list to maintain. Each zone/county's polygon is fetched once from the NWS zone API
  and reduced to its area centroid (geometric center); that point is what gets checked against
  the SPC outlook polygons. Resolution happens once at startup and is cached for the life of the
  process — restart the bot after changing `Zones`/`Counties` for SPC monitoring to pick up
  the change.
- **Categorical risk** (`TSTM` / `MRGL` / `SLGT` / `ENH` / `MDT` / `HIGH`) is checked
  independently for Day 1 and Day 2. A location with no categorical match ("None") never
  triggers an alert, on either day.
- **Tornado / Wind / Hail probabilities** for that same location are looked up the same way and
  always included in the post body, e.g.:
  ```
  Tornado: 5%
  Wind: 15%
  Hail: None
  ```
- **Checked every `Spc.CheckIntervalSeconds`** (default 1800s = 30 min) — independent of
  `Polling.PollIntervalSeconds`, since SPC re-issues the Day 1 outlook ~5x/day and Day 2 ~2x/day;
  checking more often has no benefit.
- **Re-alerts on every new SPC issuance**, not only on a category change — if a location stays
  `SLGT` across three consecutive Day 1 re-issuances, you will get three separate notifications
  that day. Deduplication keys off the SPC product's own issuance timestamp, reusing the same
  `posted_alerts.txt` tracking file as NWS alerts.
- **Delivery** goes through the exact same platform pipeline as NWS alerts — every enabled
  platform receives outlook posts, subject to that platform's existing `MinSeverity`/
  `EventTypes` filters (see [Filtering SPC outlook posts per platform](#filtering-spc-outlook-posts-per-platform)
  below).
- **Outlook map image** — each outlook post includes a categorical risk map image generated by
  [Iowa State's IEM Mesonet plotting service](https://mesonet.agron.iastate.edu/plotting/auto/?q=220)
  (free, no API key), cropped to the location's forecast office (WFO) and state. It flows through
  the same `MapImageUrl` field as Mapbox alert maps, so it reaches every platform that supports
  images — see [Map Images](#map-images--internals) for the per-platform behavior table. The WFO and
  state come from the same NWS zone lookup used for centroid resolution, so there's no extra
  config or API call. Image generation is skipped (post still goes out, text-only) if the WFO or
  state can't be resolved for a location — this can happen for some Alaska/Pacific/Caribbean
  offices, whose IEM-side codes use an ICAO prefix (e.g. `PAFC`) that doesn't match the plain CWA
  id the NWS API returns (e.g. `AFC`).

> **Caveat:** Because each location is reduced to a single centroid point, a location very near
> the edge of an outlook risk area may not perfectly reflect that polygon's true boundary —
> especially for large, oddly-shaped, or multi-part (e.g. coastal) zones/counties.

### Filtering SPC outlook posts per platform

SPC outlook alerts flow through each platform's existing `MinSeverity`/`EventTypes` dials —
no new filter fields were added:

- **Event names** — `SPC Day 1 Convective Outlook` and `SPC Day 2 Convective Outlook`. Use
  these in a platform's `EventTypes` if you want that platform to opt in or out of outlook
  posts specifically (e.g. push notifications only, not social media).
- **Severity mapping** — the categorical risk is mapped onto the same severity scale used by
  NWS alerts, so `MinSeverity` and the Pushover emergency-priority escalation work
  automatically:

  | Categorical Risk | Mapped Severity |
  |---|---|
  | `HIGH` | Extreme |
  | `MDT` | Severe |
  | `ENH` | Severe |
  | `SLGT` | Moderate |
  | `MRGL` | Minor |
  | `TSTM` | Minor |

  For example, a platform configured with `"MinSeverity": "Severe,Extreme"` will receive
  `ENH`/`MDT`/`HIGH` outlook posts but not `TSTM`/`MRGL`/`SLGT` ones.

---

## SPC Mesoscale Discussion Monitoring — How It Works

In addition to NWS alerts and SPC Convective Outlooks, the bot can monitor
[SPC Mesoscale Discussions (MCDs)](https://www.spc.noaa.gov/products/md/) — short-fuse
products issued by the Storm Prediction Center that highlight areas of developing or
ongoing severe weather potential, often ahead of or alongside active tornado/severe
thunderstorm watches.

- **Area matching** uses the same zone/county centroids already resolved for SPC Outlook
  monitoring. When an active MCD's polygon (from its LAT…LON block) contains at least one
  centroid, the MCD is posted.
- **Active detection** is based on the "Valid DDHHMM Z – DDHHMM Z" line in the product
  text. Only MCDs currently within their valid window are posted.
- **Data source** — MCDs arrive via the NWS products API (`/products?type=SWO` from KWNS).
  Each new product is fetched individually to check for "SWOMCD" in the header.
- **Image** — each post includes the SPC's own MCD graphic
  (`https://www.spc.noaa.gov/products/md/{year}/mcd{NNNN}.png`), pre-generated by SPC.
- **Deduplication** uses the same `posted_alerts.txt` as NWS alerts, keyed on
  `SPC-MCD-{year}-{num}`.
- **Per-platform opt-in** — MCDs are controlled by the separate `IncludeSpcMcd` flag on
  each platform, independent of `IncludeSpcOutlooks`. You can enable MCDs on a platform
  while suppressing Convective Outlooks, or vice versa.
- **Expedited polling** — posting a new MCD triggers the same accelerated poll interval
  as a severe/extreme NWS alert, keeping the bot in fast-poll mode while active weather is ongoing.

`CheckIntervalSeconds` (default 300 = 5 min) controls how often the bot queries the NWS products
API for new MCDs. MCDs are valid for 1–3 hours, so sub-5-minute intervals have diminishing
returns. The event name for `EventTypes` filtering is `SPC Mesoscale Discussion` (severity
`Severe`).

---

## Hazardous Weather Outlook (HWO) — How It Works

The bot can also monitor the [Hazardous Weather Outlook](https://www.weather.gov/media/directives/010_docs/pd01005017curr.pdf)
(HWO), a plain-text product each local NWS office issues 1-2x/day summarizing hazards expected
over the next 7 days. Unlike every other alert type in this bot, HWO carries no polygon and no
map image — it's pure text, and delivery is opt-in per platform since it's intended primarily
for personal use rather than broad social media distribution.

- **WFO resolution** — the bot resolves the responsible forecast office(s) (WFO) from the same
  `Location.Zones`/`Location.Counties` used everywhere else, via the NWS zones API. If your
  monitored area spans multiple WFOs, each office's latest HWO is fetched and posted independently.
- **Data source** — HWO arrives via the NWS text products API
  (`/products/types/HWO/locations/{wfo}`), the same family of endpoints used for SPC MCDs.
  The bot fetches the most recent issuance per WFO each check cycle.
- **Text cleanup** — the raw teletype product text is stripped of the WMO/AFOS header codes,
  the UGC zone/county code list block, and the trailing `$$` terminator, then mid-sentence line
  wraps within each paragraph are collapsed into single lines while section breaks (e.g.
  `.DAY ONE...`) are preserved. The full cleaned text is posted — there is no separate
  "instruction" field to summarize, so nothing is dropped for length except by each platform's
  own character limit.
- **Deduplication** uses the same `posted_alerts.txt` as everything else, keyed on the NWS
  product's UUID (`HWO-{wfo}-{uuid}`), so each new issuance posts exactly once.
- **No map image** — `DownloadMapImageAsync`/Mapbox fallback are skipped entirely for HWO
  since there's no geometry to plot.
- **Per-platform opt-in** — controlled by `IncludeHwo` on each platform's settings, defaulting
  to `false`. Enable it only where you want the full text delivered (e.g. a personal Discord DM
  or Telegram chat) — platforms with small character limits (X, Bluesky, Twilio) will receive a
  truncated version since the full text commonly runs 800-2000+ characters.
- **Severity** — HWO alerts are posted with `Severity: Unknown` (NWS has no severity concept for
  this product). If a platform's `MinSeverity` filter excludes `Unknown` (e.g. it's set to
  `"Severe,Extreme"`), that platform will not receive HWO posts even with `IncludeHwo: true`.

The event name for `EventTypes` filtering is `Hazardous Weather Outlook`.

---

## WPC Excessive Rainfall Outlook (ERO) — How It Works

The bot can also monitor the [WPC (Weather Prediction Center) Excessive Rainfall Outlook](https://www.wpc.ncep.noaa.gov/qpf/excessive_rainfall_outlook_ero.php)
(ERO) — Day 1, 2, and 3 forecasts of the probability that rainfall will exceed flash flood
guidance near a point, categorized into four risk levels: Marginal (≥5%), Slight (≥15%),
Moderate (≥40%), and High (≥70%). Note: despite living alongside the SPC-issued feeds in this
bot, ERO is a **WPC** product, not SPC.

- **Locations monitored** reuse the same `Location.Zones`/`Location.Counties` centroids as
  SPC Outlook/MCD — resolved once at startup and cached for the life of the process.
- **Categorical risk** (`Marginal` / `Slight` / `Moderate` / `High`) is checked independently
  for Day 1, 2, and 3 against WPC's GeoJSON feeds
  (`https://www.wpc.ncep.noaa.gov/exper/eromap/geojson/Day{1,2,3}_Latest.geojson`). A location
  with no categorical match ("None"/sub-5%) never triggers an alert, on any day.
- **Checked every `Ero.CheckIntervalSeconds`** (default 1800s = 30 min) — independent of
  `Polling.PollIntervalSeconds`.
- **Re-alerts on every new WPC issuance**, not only on a category change, the same as SPC
  Outlook. Deduplication keys off WPC's own issuance timestamp, reusing the same
  `posted_alerts.txt` tracking file as everything else.
- **Outlook map image** — each post includes a categorical risk map image generated by
  [Iowa State's IEM Mesonet plotting service](https://mesonet.agron.iastate.edu/plotting/auto/?q=220)
  (free, no API key), the same service used for SPC Outlook maps, cropped to the location's
  forecast office (WFO) and state.
- **Details link** — each post links to WPC's own interactive ERO page
  (`https://www.wpc.ncep.noaa.gov/qpf/ero.php?opt=curr&day={day}`).

> **Caveat:** Same as SPC Outlook — each location is reduced to a single centroid point, so a
> location very near the edge of a risk area may not perfectly reflect that polygon's true
> boundary.

### Filtering ERO posts per platform

ERO alerts flow through each platform's existing `MinSeverity`/`EventTypes` dials — no new
filter fields were added beyond `IncludeEro` (see [Configuration Reference](#configuration-reference)):

- **Event names** — `WPC Day 1 Excessive Rainfall Outlook`, `WPC Day 2 Excessive Rainfall Outlook`,
  and `WPC Day 3 Excessive Rainfall Outlook`.
- **Severity mapping** — WPC's own category names don't line up 1:1 in name with this bot's
  Severity scale, only in rank order:

  | WPC Category | Mapped Severity |
  |---|---|
  | High | Extreme |
  | Moderate | Severe |
  | Slight | Moderate |
  | Marginal | Minor |

  For example, a platform configured with `"MinSeverity": "Severe,Extreme"` will receive
  `Moderate`/`High` ERO posts but not `Marginal`/`Slight` ones.

---

## Map Images — Internals

When enabled, the bot generates a **Mapbox Static Images** URL for each NWS alert (warnings,
watches, advisories) and attaches it via the alert's `MapImageUrl` field to every platform that
supports images. SPC Convective Outlook and WPC ERO posts get their own image independently of
this setting through the same `MapImageUrl` field, so the platform behavior table in the README's
[Map Images](../README.md#map-images-mapbox) section applies to all of them.

Map images are generated from two sources depending on the alert type:

**IEM (primary — most NWS alerts):** The bot parses the VTEC code from each NWS alert
(`parameters.VTEC`) and requests a pre-rendered PNG from Iowa State University's IEM Autoplot
service (plot #208). The image shows the exact NWS warning polygon, county boundary lines, and
a NEXRAD radar overlay — no geometry math, no URL size limit. Requires no API key. Used for
any alert with a VTEC code where the action is not CAN or EXP.

Before requesting the PNG, the bot calls IEM's VTEC JSON API (`/json/vtec_event.py`) to verify
the event exists in IEM's database. IEM returns HTTP 200 with a fixed default demo image for
any unknown event — it does not return a 404 — so the pre-flight check is required to avoid
posting the wrong map. If the event is not found, the bot falls back to Mapbox.

Some NWS phenomena codes differ from IEM's internal codes. Known aliases are tried automatically:
`HT.W` (Heat Warning) and `EH.W` (Excessive Heat Warning) both map to `XH.W` in IEM (Extreme
Heat Warning — IEM's code for this product since March 2025).

**IEM autoplot #217 (Special Weather Statements):** SPS alerts carry no VTEC code, so the bot
uses a different IEM endpoint. It parses `parameters.AWIPSidentifier` (AFOS PIL, e.g. `SPSMPX`)
and `parameters.WMOidentifier` (e.g. `WWUS83 KMPX 011045`) from the NWS alert to construct the
IEM product ID: `YYYYMMDDHHmm-K{WFO}-{WMO6}-SPS{WFO}`. The timestamp uses the DDHHMM from the
WMO header (not the alert `sent` field, which may lag by 1-2 minutes due to NWS processing).

The same demo-image trap applies: IEM returns HTTP 200 with a placeholder image for unknown
products. Before returning the URL, the bot verifies the SPS is indexed by querying IEM's
active SPS GeoJSON feed (`/geojson/sps.geojson?wfo={WFO}`) and matching the issue time within
a 5-minute window. If not yet indexed, the bot falls back to Mapbox.

**Mapbox (fallback — non-VTEC events and cancelled/expired alerts):** Used when no VTEC code
is present. The map area and overlay are determined by:
1. The **alert's own GeoJSON geometry polygon** (most NWS alerts include one).
2. **Dissolved county perimeter** — when no polygon is in the alert, the bot fetches geometry
   for each UGC zone/county code from the NWS zone API and dissolves shared borders so only the
   outer perimeter of the combined area is drawn.
3. **Bounding box only** — if no geometry is available at all.

For setup and the per-platform behavior table, see the README's
[Map Images (Mapbox)](../README.md#map-images-mapbox) section.

---

## References

- NWS REST API documentation: https://www.weather.gov/documentation/services-web-api
- NWS VTEC explanation (complete phenomena list): https://www.weather.gov/media/vtec/VTEC_explanation_ver9.pdf
- Zone and county code lookup: https://www.weather.gov/gis/ZoneCounty
- Active alerts by zone (test URL): https://api.weather.gov/alerts/active?zone=MOZ066
- Meta Graph API (Facebook/Instagram): https://developers.facebook.com/docs/graph-api
- X (Twitter) API v2: https://developer.twitter.com/en/docs/twitter-api
- Bluesky AT Protocol: https://docs.bsky.app/docs/get-started
- Mastodon API: https://docs.joinmastodon.org/methods/statuses/
