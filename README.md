# NWS Alert Social Media Bot

A .NET 10 C# console application that polls the National Weather Service API for active weather
alerts and posts them to Facebook, Instagram, X (Twitter), Bluesky, Mastodon, Discord (webhook
and DM), and Telegram — and sends real-time push notifications and SMS via Pushover, Twilio, and VoIP.ms.

---

## Table of Contents

1. [Setup](#setup)
2. [Configuration Reference](#configuration-reference)
3. [Geographic Filtering: Zones and Counties](#geographic-filtering-zones-and-counties)
4. [SPC Convective Outlook Monitoring](#spc-convective-outlook-monitoring)
5. [SPC Mesoscale Discussion Monitoring](#spc-mesoscale-discussion-monitoring)
6. [Hazardous Weather Outlook (HWO)](#hazardous-weather-outlook-hwo)
7. [WPC Excessive Rainfall Outlook (ERO)](#wpc-excessive-rainfall-outlook-ero)
8. [Alert Filtering: Severity, Urgency, Certainty, Event Types](#alert-filtering)
9. [Complete NWS Event Type Reference](#complete-nws-event-type-reference)
10. [API Credentials — Social Media](#api-credentials)
11. [Push / SMS Notifications](#push--sms-notifications)
12. [Map Images (Mapbox)](#map-images-mapbox)
13. [Running the Bot](#running-the-bot)
14. [Deploying to Ubuntu (GitHub Actions)](#deploying-to-ubuntu-github-actions)
15. [Cross-Platform Release Builds](#cross-platform-release-builds)
16. [Auto-Update](#auto-update)

---

## Setup 

### Requirements
- .NET 10 SDK  
- Visual Studio 2022 (recommended) or VS Code

### Steps
1. Unzip and open `NwsAlertBot.csproj` in Visual Studio
2. NuGet packages restore automatically on first build
3. Create `appsettings.Local.json` (see below) with your real credentials
4. Set `"Enabled": true` for each platform you want active
5. Run with F5, or publish as a Windows Service / Task Scheduler job

### Keeping Secrets Out of Git

`appsettings.json` is committed to source control as a template — all sensitive values are
placeholders like `"YOUR_API_KEY"`. **Never put real credentials in `appsettings.json`.**

Instead, create `appsettings.Local.json` in the project root (it is listed in `.gitignore`
and will never be committed). Override only the fields you need:

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

### NuGet Packages (auto-restored)
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Configuration.Json`
- `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` — structured logging
- `NetTopologySuite`, `NetTopologySuite.IO.GeoJSON4STJ` — GeoJSON geometry (union/dissolve, point-in-polygon, convex hull, simplification)
- `Microsoft.Extensions.Http.Resilience` — retry/circuit-breaker for read-only weather/mapping HTTP clients

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
| `Zones` | NWS forecast zone codes (see below) | `[]` |
| `Counties` | NWS county codes (see below) | `[]` |
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
| `ActiveAlertWindowHours` | Hours to stay in accelerated polling after the last new NWS alert; resets on each new NWS alert. SPC outlooks/HWO do not affect the storm window. | `4` |
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
feeds with their own severity values, gated only by each platform's own `MinSeverity`.

Each platform block also accepts:

| Field | Description | Default |
|---|---|---|
| `MinSeverity` | Comma-separated severity levels for this platform only. Blank means "accept everything that already passed the feed's own filter above." | `""` (inherit) |
| `EventTypes` | Comma-separated NWS event names for this platform only. Leave empty to receive all event types. | `""` (all) |
| `IncludeSpcOutlooks` | Whether SPC Convective Outlook alerts are posted to this platform. Requires `Spc.Enabled = true`. | `true` |
| `IncludeSpcMcd` | Whether SPC Mesoscale Discussion alerts are posted to this platform. Requires `SpcMcd.Enabled = true`. | `true` |
| `IncludeHwo` | Whether Hazardous Weather Outlook text posts are sent to this platform. Requires `Hwo.Enabled = true`. Defaults to `false` — HWO is long-form text intended for personal use, enable it selectively (e.g. a Discord DM or Telegram chat). | `false` |
| `IncludeEro` | Whether WPC Excessive Rainfall Outlook alerts are posted to this platform. Requires `Ero.Enabled = true`. | `true` |

`Spc` (see [SPC Convective Outlook Monitoring](#spc-convective-outlook-monitoring)):

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

`SpcMcd` (see [SPC Mesoscale Discussion Monitoring](#spc-mesoscale-discussion-monitoring)):

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

`Hwo` (see [Hazardous Weather Outlook (HWO)](#hazardous-weather-outlook-hwo)):

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

`Ero` (see [WPC Excessive Rainfall Outlook (ERO)](#wpc-excessive-rainfall-outlook-ero)):

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

## SPC Convective Outlook Monitoring

In addition to NWS warnings/watches/advisories, the bot can separately monitor the
[SPC (Storm Prediction Center)](https://www.spc.noaa.gov/) Day 1 and Day 2 Convective Outlooks
and alert when a monitored location is in any non-"None" categorical risk — a general
thunderstorm risk (`TSTM`) or higher. The same notification bundles that location's tornado,
wind, and hail probability for the day.

### How it works

- **Locations monitored** are derived from the same `Location.Zones` (or `Location.Counties` if
  `Zones` is empty) already configured for warning geo-filtering above — there is no separate
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
  images — see [Map Images](#map-images-mapbox) for the per-platform behavior table. The WFO and
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

## SPC Mesoscale Discussion Monitoring

In addition to NWS alerts and SPC Convective Outlooks, the bot can monitor
[SPC Mesoscale Discussions (MCDs)](https://www.spc.noaa.gov/products/md/) — short-fuse
products issued by the Storm Prediction Center that highlight areas of developing or
ongoing severe weather potential, often ahead of or alongside active tornado/severe
thunderstorm watches.

### How it works

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

### Setup

```json
"SpcMcd": {
  "Enabled": true,
  "CheckIntervalSeconds": 300
}
```

No additional credentials or API keys required. `CheckIntervalSeconds` (default 300 = 5 min)
controls how often the bot queries the NWS products API for new MCDs. MCDs are valid for
1–3 hours, so sub-5-minute intervals have diminishing returns.

The event name for `EventTypes` filtering is `SPC Mesoscale Discussion` (severity `Severe`).
Per-platform delivery is controlled by `IncludeSpcMcd` (default `true`) — independent of
`IncludeSpcOutlooks`, so you can enable or suppress each product type per platform.

---

## Hazardous Weather Outlook (HWO)

The bot can also monitor the [Hazardous Weather Outlook](https://www.weather.gov/media/directives/010_docs/pd01005017curr.pdf)
(HWO), a plain-text product each local NWS office issues 1-2x/day summarizing hazards expected
over the next 7 days. Unlike every other alert type in this bot, HWO carries no polygon and no
map image — it's pure text, and delivery is opt-in per platform since it's intended primarily
for personal use rather than broad social media distribution.

### How it works

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

### Setup

```json
"Hwo": {
  "Enabled": true,
  "CheckIntervalSeconds": 300
}
```

No additional credentials or API keys required. Then enable `IncludeHwo: true` on whichever
platform(s) you want it delivered to — for example, a personal Discord DM:

```json
"DiscordDm": {
  "Enabled": true,
  "IncludeHwo": true
}
```

The event name for `EventTypes` filtering is `Hazardous Weather Outlook`.

---

## WPC Excessive Rainfall Outlook (ERO)

The bot can also monitor the [WPC (Weather Prediction Center) Excessive Rainfall Outlook](https://www.wpc.ncep.noaa.gov/qpf/excessive_rainfall_outlook_ero.php)
(ERO) — Day 1, 2, and 3 forecasts of the probability that rainfall will exceed flash flood
guidance near a point, categorized into four risk levels: Marginal (≥5%), Slight (≥15%),
Moderate (≥40%), and High (≥70%). Note: despite living alongside the SPC-issued feeds in this
bot, ERO is a **WPC** product, not SPC.

### How it works

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

### Setup

```json
"Ero": {
  "Enabled": true,
  "CheckIntervalSeconds": 1800
}
```

No additional credentials or API keys required.

---

## Alert Filtering

All filter fields are pushed directly to the NWS API — the bot does not pull all alerts and
filter locally. This keeps API responses small and fast.

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
Flood Warnings. Because of that, `Cancel` messages are fetched via a separate, unconditional
query with no severity/urgency/certainty filter (`FilterEventTypes` still applies). Without this,
almost any `FilterSeverity` that excludes `Minor` — which includes every example in this README
except the "everything" one — would silently drop every single cancellation, for every alert
type, before the bot ever saw it.

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
  `x_post_count.txt` (rolling 30-day window) — this bot redeploys on every push to master, so an
  in-memory-only counter would reset far too often to track a real monthly quota.
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

## Deploying to Ubuntu (GitHub Actions)

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
  [Auto-Update](#auto-update) compares against GitHub Releases.

### Cutting a release

```bash
git tag v1.0.0
git push origin v1.0.0
```

Produces `NwsAlertBot-win-x64.zip`, `NwsAlertBot-linux-x64.tar.gz`, `NwsAlertBot-osx-x64.tar.gz`,
and `NwsAlertBot-osx-arm64.tar.gz` attached to the `v1.0.0` release, each also containing
`scripts/update.ps1` (see [Auto-Update](#auto-update)).

### Running a downloaded build

Each archive contains the executable, `appsettings.json`, and `update.ps1`. Extract it, create
`appsettings.Local.json` alongside it with your real credentials (see
[Keeping Secrets Out of Git](#keeping-secrets-out-of-git)), and run the executable directly —
`./NwsAlertBot` on Linux/macOS (`chmod +x` first if the executable bit didn't survive transfer)
or `NwsAlertBot.exe` on Windows. No .NET runtime install is required — the self-contained build
bundles it.

---

## Auto-Update

If you're running a downloaded release build (rather than this repo owner's own
continuously-deployed server — see [Cross-Platform Release Builds](#cross-platform-release-builds)),
the bot can check GitHub Releases for a newer version and optionally install it automatically.

**Requires PowerShell 7+ (`pwsh`)** on the machine running the bot — it's what `update.ps1` runs
under, cross-platform. Install it from https://github.com/PowerShell/PowerShell if it isn't
already present (Windows PowerShell 5.1, the version that ships built into Windows, is **not**
enough — `pwsh` is a separate, newer install).

### Configuration

```json
"Update": {
  "AutoApply": false,
  "CheckIntervalHours": 24,
  "GitHubRepo": "MikeWills/NwsAlertBot"
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

### What gets touched (and what doesn't)

The update only ever replaces the **executable** and **`update.ps1` itself** (so future updater
fixes apply on the next run too). It never touches `appsettings.json`, `appsettings.Local.json`,
or any runtime state file (`posted_alerts.txt`, `confirmed_platforms.txt`, `logs/`,
`x_post_count.txt`, `twilio_sms_count.txt`) — your configuration and history survive every
update. The old executable is backed up to `NwsAlertBot.bak` (or `NwsAlertBot.exe.bak` on
Windows) before being replaced, in case something goes wrong.

**Restart behavior:** if a systemd service (Linux) or Windows Service named `nwsalertbot` exists,
it's restarted via `systemctl`/`Restart-Service`. Otherwise the new executable is just launched
directly — this covers the common case of simply running the `.exe`/binary yourself with no
service installed.

### Running it manually

With `AutoApply: false` (the default), nothing happens automatically — check
[the releases page](https://github.com/MikeWills/NwsAlertBot/releases) yourself and run:

```bash
./update.ps1 -Tag v1.2.3
```

To safely verify the script works on your machine (downloads and extracts, but doesn't touch
your install or restart anything) before trusting it with `AutoApply: true`:

```bash
./update.ps1 -Tag v1.2.3 -DryRun
```

See `Get-Help ./update.ps1 -Full` for all parameters (`-Repo`, `-ServiceName`, etc.).

---

## Map Images (Mapbox)

When enabled, the bot generates a **Mapbox Static Images** URL for each NWS alert (warnings,
watches, advisories) and attaches it via the alert's `MapImageUrl` field to every platform that
supports images. SPC Convective Outlook posts get their own image independently of this setting
— see [Outlook map image](#how-it-works) above — through the same `MapImageUrl` field, so the
platform behavior table below applies to both.

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

---

## Running the Bot

### Development (Visual Studio)
Press F5. The bot starts polling immediately and logs to the console.

### As a Windows Scheduled Task
Publish as a self-contained executable and schedule it to run on startup, or use `Task Scheduler`
with a trigger of "At system startup" and a repeat interval.

### As a Windows Service
Add `UseWindowsService()` to `Program.cs` and publish. Then:
```
sc create NwsAlertBot binpath="C:\path\to\NwsAlertBot.exe"
sc start NwsAlertBot
```

### Log Files

The bot writes daily rolling log files to a `logs/` subdirectory of the working directory:

```
logs/nwsalertbot-20260625.log
logs/nwsalertbot-20260626.log
...
```

Logs are retained for **30 days** and then automatically deleted. The file format includes full timestamps and the source class name, making it easy to grep for specific services:

```
[2026-06-25 14:32:01 INF] NwsAlertBot.Services.AlertPollingService: Active storm mode engaged — polling every 60s for 4h.
[2026-06-25 14:32:03 ERR] NwsAlertBot.Services.FacebookService: Facebook: Post failed. Status=400
```

To change the log retention period, update `retainedFileCountLimit` in the `UseSerilog` call in `Program.cs`.

### Alert Deduplication
The bot tracks posted alert IDs in `posted_alerts.txt` in the working directory. This file
persists across restarts so the bot won't re-post alerts after a restart. The file is pruned
automatically when it exceeds 10,000 entries.

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
counter persists across restarts in `twilio_sms_count.txt`, since this bot redeploys on every push
to master and an in-memory-only counter would reset too often to be a meaningful daily cap.

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
   the connection just hangs until `HttpClient`'s 100-second timeout, which looks identical to
   a network/firewall problem in the logs.
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
outlook plot from IEM Mesonet, the same source used for [SPC outlook map images](#how-it-works);
no Mapbox token required) — to every **enabled** platform that supports images: Facebook,
Instagram, X, Bluesky, Mastodon, Discord, Telegram, and Twilio (MMS). It logs a per-platform
OK/FAILED result and **exits immediately** without starting the live polling loop — it does not
touch `confirmed_platforms.txt` or `posted_alerts.txt`, so it's safe to re-run as many times as
you want while debugging a platform.

**This is a live action** — it posts to your real accounts/channels and (for Twilio) sends a
real billed MMS. Delete the test posts once you've confirmed the image rendered correctly.

Pushover and VoIP.ms are skipped (no image support); platforms that are disabled are skipped too.
If nothing is enabled, it logs a warning and exits without posting anything.

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
(`InternalsVisibleTo.cs` at the repo root), not by widening the public API surface.

---

## Recent Changes

- **Add: self-update for standalone release binaries.** New `UpdateCheckService` checks GitHub
  Releases against the running executable's own version (injected at publish time via
  `-p:Version=` from the git tag — see `release.yml`) once per `Update.CheckIntervalHours`
  (default 24). Single on/off switch: `Update.AutoApply` (default `false`) — no separate
  "check but don't apply" mode, since that added a second setting for no real benefit. When
  `true`, a newer version triggers `scripts/update.ps1` (bundled in every release archive),
  which downloads the release, replaces only the executable and itself (never
  `appsettings.json`/`appsettings.Local.json`/any runtime state file), backs up the old
  executable first, and restarts via systemd/Windows Service if one exists or just relaunches
  the binary directly otherwise. Cross-platform PowerShell (`pwsh`) script with a `-DryRun` mode
  for safely verifying it works on a given machine before trusting it with `AutoApply: true`.
  9 new unit tests for the pure version-parsing/comparison and check-interval logic; the
  download/extract/restart logic was verified by hand against the project's own real `v0.1.0`
  release before landing (see [Auto-Update](#auto-update)).
- **Add: persisted quota/cost guards for X and Twilio.** New `RateLimitTracker` (a small,
  file-persisted fixed-window counter, deliberately not `System.Threading.RateLimiting` — see
  CLAUDE.md Common Pitfalls for why) backs two new settings: `XSettings.MaxPostsPerMonth` (default
  500, matching the free tier — rolling 30-day window, `x_post_count.txt`) and
  `TwilioSettings.MaxSmsPerDay` (default 100 — rolling 24-hour window, counted per recipient,
  `twilio_sms_count.txt`). Once a limit is reached, further posts/sends are skipped and logged
  rather than burning requests X would reject anyway or racking up SMS costs during a busy
  outbreak. Both persist across restarts (this bot redeploys on every push to master) and default
  to enabled; set either to `0` to disable. 6 new unit tests covering the tracker's window
  rollover, persistence-across-instances, and zero-means-unlimited behavior.
- **Add: HTTP resilience for read-only weather/mapping clients + first automated test suite.**
  Added `Microsoft.Extensions.Http.Resilience` (`.AddStandardResilienceHandler()`, retry + circuit
  breaker + timeouts) to `NwsAlertService`, `NwsZoneService`, `SpcOutlookService`, `SpcMcdService`,
  `HwoService`, `WpcEroService`, and the named `"WeatherImagery"`/`"WeatherImageryPrimary"` clients
  used by `MapService`'s IEM pre-flight checks and `SocialMediaOrchestrator`'s map image download
  (replacing a hand-rolled retry loop there with the same handler used everywhere else). Deliberately
  **not** applied to `XService` (its OAuth1.0a signature includes a per-request timestamp/nonce — an
  automatic retry resending an identical signed request looks like a replay) or `BlueskyService`
  (already has its own 401-reauth retry). Also added `NwsAlertBot.Tests` (xunit), the project's first
  automated test suite — 64 tests covering pure logic only (SPC MCD parsing including the
  lon-wrap-at-100°W case, `NwsAlert.FormatPost` truncation, `PlatformHelpers`, `PolygonGeometry`,
  `MapService.BuildIemSpsUrl`). Several tested methods were changed from `private` to `internal`
  (visible to the test project via `InternalsVisibleTo`) rather than made `public`. `deploy.yml`
  now runs `dotnet test` before publishing — a failing test blocks the live deploy to production
  rather than shipping anyway (previously nothing gated `deploy.yml`, which runs on every push to
  master).
- **Refactor: extracted duplicated per-platform text/URL helpers into `Services/PlatformHelpers.cs`.**
  `BuildSmsText` was byte-for-byte identical between `TwilioService` and `VoipMsService`; `CacheBust`
  was identical between `InstagramService` and `TwilioService`; `Truncate`/`GetColor` (renamed
  `DiscordSeverityColor`) were identical between `DiscordService` and `DiscordDmService`; and the
  ad-hoc `value.Length > N ? value[..(N-3)] + "..." : value` truncation pattern was repeated
  independently in X, Bluesky, Mastodon, Pushover, Twilio, and VoIP.ms. All consolidated into one
  shared `PlatformHelpers` static class (`TruncateWithEllipsis`, `CacheBust`, `BuildSmsText`,
  `DiscordSeverityColor`). Net -57 lines across 9 files.
  **Fix included**: Pushover's alert title truncation (`PushoverService.SendAlertAsync`) previously
  hard-cut at 250 chars with no ellipsis, unlike every other truncation in the codebase — now uses
  the shared helper and gets the same "..." marker as everywhere else.
- **Fix: validate AfosId/WmoIdentifier character classes before building IEM SPS URLs.**
  `MapService.BuildIemSpsUrl`/`VerifyIemSpsAsync` previously only length-checked `alert.AfosId` and
  `alert.WmoIdentifier` before embedding substrings of them into a colon-delimited IEM autoplot #217
  URL — unlike the VTEC fields used by `BuildIemUrl`/`ResolveIemPhenomenaAsync`, which are already
  constrained to safe character classes by `NwsAlertService.VtecPattern`'s regex before they ever
  reach `MapService`. Added the same style of character-class validation (`^SPS[A-Z]{3}$` for
  AfosId, `^[A-Z0-9]{6}$` for the WMO6 substring) so a malformed value can't inject unexpected
  segments into the URL; falls back to Mapbox exactly like every other IEM-unavailable case, same
  as before. Verified against real example values and injection-shaped strings before landing.
- **Refactor: replaced hand-rolled computational geometry with NetTopologySuite.** `PolygonGeometry.cs`
  and `MapService.cs` previously hand-rolled ~500 lines of GIS logic: a Graham-scan convex hull, a
  custom edge-counting polygon dissolve (for merging adjacent county/zone shapes into an outer
  perimeter), ray-casting point-in-polygon, ring centroid/area (shoelace formula), a naive
  "simplification" that just rounded/deduped coordinates instead of real simplification, and manual
  `StringBuilder` GeoJSON construction with no escaping. Replaced all of it with
  `NetTopologySuite` + `NetTopologySuite.IO.GeoJSON4STJ` (the `System.Text.Json`-based GeoJSON I/O
  variant — no Newtonsoft dependency): `Geometry.Union()` for dissolve (more robust than the old
  edge-matching, which could dead-end on floating-point mismatches between adjacent county
  boundaries), `Geometry.Covers()` for point-in-polygon (holes handled natively), `Geometry.Centroid`
  for centroid, `Geometry.ConvexHull()` for the hull fallback, and
  `TopologyPreservingSimplifier` + `GeometryPrecisionReducer` for URL-length simplification
  (guaranteed-valid output, unlike naive coordinate rounding). Behavior is intended to be equivalent;
  verified with a standalone script exercising union/dissolve, disjoint-geometry MultiPolygon output,
  convex hull, point-in-polygon with holes, multi-polygon centroid selection, and simplification —
  see commit history for details. Both `PolygonGeometry.ComputeCentroid`/`PointInGeometry` and
  `MapService`'s NTS calls are wrapped in try/catch (matching the old code's fully defensive
  posture), since some NTS operations (e.g. `GeometryPrecisionReducer.Reduce`) can throw on
  topologically awkward input rather than returning null/empty.
- **Fix: Discord/DiscordDm reported success on partial multi-recipient failure.** Both
  `DiscordService.SendAsync` and `DiscordDmService.SendToAllUsersAsync` aggregated per-recipient
  send results with `Any()`, so if only one of several webhook URLs or DM user IDs succeeded, the
  whole post/DM was reported as an overall success and the orchestrator's failure warning never
  logged. Changed both to `All()` to match the existing multi-recipient pattern used by
  `TwilioService`/`VoipMsService`.
- **Add: WPC Excessive Rainfall Outlook (ERO) monitoring.** New `WpcEroService` polls WPC's
  Day 1/2/3 categorical GeoJSON feeds (`Marginal`/`Slight`/`Moderate`/`High`, ≥5%/15%/40%/70%
  probability of exceeding flash flood guidance) and checks them against the same monitored
  zone/county centroids used by SPC Outlook/MCD. Follows the exact same pattern as SPC Outlook:
  one alert per day per non-"None" risk, an IEM Mesonet categorical map image
  (`which:{day}E` on the same autoplot #220 used for SPC outlooks), and a details link to WPC's
  own interactive ERO page. New `Ero` settings block (`Enabled`, `CheckIntervalSeconds`, default
  1800s) and a new per-platform `IncludeEro` flag (default `true`) on all 11 delivery platforms.
  Despite living next to `Spc`/`SpcMcd` in config, ERO is issued by WPC, not SPC — confirmed
  against WPC's live GeoJSON feed before implementing (see [WPC Excessive Rainfall Outlook (ERO)](#wpc-excessive-rainfall-outlook-ero)).
  Extracted the GeoJSON point-in-polygon test (previously private to `SpcOutlookService`) into
  the shared `PolygonGeometry.PointInGeometry()` so ERO didn't need a third copy.
- **Fix: SMS alerts (Twilio, VoIP.ms) could truncate the details link entirely.** Both services
  build a single message string (headline + area + expiry + instruction) and hard-truncate it to
  fit the segment budget (320 chars for Twilio, 160 for VoIP.ms). Any link that happened to be part
  of `Instruction` (e.g. the SPC MCD/Outlook detail page URL) lived at the very end of that string,
  so it was always the first thing cut once the message ran long — VoIP.ms's 160-char budget in
  particular is regularly blown by just the headline + area + expiry, dropping the link every time.
  Added `NwsAlert.DetailsUrl` (populated for all four alert sources: NWS VTEC alerts point to the
  `api.weather.gov` alert record, SPC MCD/Outlook point to their SPC HTML page, HWO points to the
  `api.weather.gov` product record) and a `TruncateKeepingDetailsLink()` helper in both SMS services
  that truncates the *rest* of the message first and always preserves the trailing `Details: {url}`
  line intact.
- **Fix: SPC MCD polygon parsing decoded far-west longitudes incorrectly, causing false-positive
  alerts for monitored areas hundreds of miles outside the actual MCD.** `SpcMcdService.ParseLatLon`
  assumed 8-digit `LAT...LON` tokens always meant lon &lt; 100°W and that lon &gt;= 100°W used a
  9-digit token. In practice SPC always encodes 8-digit tokens and drops the leading "1" digit for
  longitudes &gt;= 100.00°W (e.g. 101.73°W is encoded as `0173`, not `10173`) instead of widening the
  field. This made western polygon vertices decode as ~0-2°W (off the coast of Europe), ballooning
  the effective polygon shape and causing it to spuriously contain zone centroids far to the east
  (e.g. an MCD over north-central Nebraska/northeast South Dakota was flagged as covering
  south-central Minnesota zones). Fixed by unwrapping any decoded lon &lt; 5000 (i.e. &lt; 50.00) by
  adding 100° — CONUS longitudes run ~67-125°W, so the wrapped range (0-25) never overlaps the
  unwrapped range (67-99), making the unwrap unambiguous. See `Services/SpcMcdService.cs`.
- **Fix: VoIP.ms `sendSMS` requests must be GET, not POST.** Every SMS attempt failed with a
  generic HTTP 500 SOAP fault ("Bad Request") regardless of credentials, message content, or
  source IP allowlist status. Root cause: VoIP.ms's own documented sample code sends `sendSMS`
  as a `GET` request with query-string parameters — the bot was POSTing a form-encoded body
  instead, which their API silently rejects with that same generic fault. Confirmed by
  reproducing the exact fault via `curl POST` with real, verified-correct credentials, then
  confirming an identical `curl GET` request succeeded. `VoipMsService` now sends `GET` with a
  URL-encoded query string; since credentials are now in the URL (required by the API),
  `System.Net.Http.HttpClient.VoipMsService` request-URL logging is suppressed in `Program.cs`,
  matching the existing pattern for Telegram/Bluesky/X/Mastodon.
  Also fixed/documented two secondary issues found while diagnosing this:
  - Non-ASCII message content (emoji, em-dashes, smart quotes) is now normalized to ASCII before
    sending — not the root cause of the "Bad Request" fault, but a real independent gotcha, since
    Unicode content forces UCS-2 SMS encoding at a much shorter per-segment limit than the
    160-char GSM-7-oriented truncation accounted for.
  - An un-allow-listed source IP causes the API to silently hang the connection for ~100s
    (`HttpClient`'s default timeout) rather than a clean auth error, which looks identical to a
    network/firewall problem in the logs.
- **Fix: cancellation alerts silently dropped by severity/urgency/certainty filters** — NWS
  downgrades every `Cancel` message to `severity: Minor`, `urgency: Past`, `certainty: Observed`
  regardless of the original event's actual severity (confirmed live for cancelled Tornado
  Warnings and Flash Flood Warnings, not just lower-severity products). Any `FilterSeverity` that
  excludes `Minor` — the default, and every recommended config in this README except "everything"
  — meant cancellations for every alert type were filtered out server-side and never reached the
  bot. `NwsAlertService` now fetches cancellations via a separate, always-on query that bypasses
  those three filters (mirroring how `AdditionalEventTypes` already bypassed `FilterSeverity`).
- **BREAKING: `Nws.Severity`/`Urgency`/`Certainty`/`EventTypes` renamed to `FilterSeverity`/
  `FilterUrgency`/`FilterCertainty`/`FilterEventTypes`.** All four are server-side, restrictive
  filters sent to `api.weather.gov` — the `Filter` prefix makes that explicit and distinguishes
  them from `AdditionalEventTypes` (deliberately unprefixed, since it's additive rather than
  restrictive) and from each platform's own client-side `MinSeverity`/`EventTypes` fields
  (unchanged, not renamed). If you have an existing `appsettings.Local.json`, rename these four
  keys under `Nws` manually — `LocalConfigSync` does not auto-migrate renamed keys.
- **BREAKING: `Zones`/`Counties`/`TimeZone` moved out of `Nws` into a new `Location` block;
  `PollIntervalSeconds`/`ActiveAlertPollIntervalSeconds`/`ActiveAlertWindowHours`/
  `ActiveAlertMinSeverity` moved into a new `Polling` block.** These settings were always shared
  by every feed (SPC Outlook, SPC MCD, and HWO all resolve locations from the same
  Zones/Counties/TimeZone, not just the NWS alerts feed), so nesting them under `Nws` was
  misleading — and in at least one deployment led to a dead `TimeZone` key mistakenly added
  under `Spc` (which has no such property; it silently used `Nws.TimeZone` instead). If you have
  an existing `appsettings.Local.json`, manually move `Zones`, `Counties`, and `TimeZone` from
  `Nws` into a new top-level `Location` section, and move `PollIntervalSeconds`,
  `ActiveAlertPollIntervalSeconds`, `ActiveAlertWindowHours`, and `ActiveAlertMinSeverity` into a
  new top-level `Polling` section — see the Configuration Reference below for the exact shape.
  `LocalConfigSync` does not auto-migrate this rename (it only adds missing keys within sections
  that already match by name), so old-style configs will silently fall back to defaults for
  these fields until moved manually.
- **Hazardous Weather Outlook (HWO) monitoring** — new `HwoService`/`Hwo` settings block.
  Text-only product (no map image), opt-in per platform via `IncludeHwo` (defaults `false`).
  Cleans up raw teletype formatting (header codes, UGC zone list, line wraps) before posting
  the full product text.
- **Map: IEM autoplot #217 for Special Weather Statements** — SPS alerts (non-VTEC) now use
  IEM's SPS-specific map endpoint. The bot parses `AWIPSidentifier` and `WMOidentifier` from the
  NWS alert parameters to build the IEM product ID. A pre-flight check against IEM's active SPS
  GeoJSON feed prevents posting the demo image when IEM hasn't yet indexed the product.
- **SPC Convective Outlook significant-severe levels** — when the monitored area falls within a
  CIG1/CIG2 hatching polygon on the SPC outlook GeoJSON, the label is appended to the
  tornado/wind/hail probability lines in the post (e.g. `Wind: 30% — CIG1`).
- **SPC Mesoscale Discussion (MCD) monitoring** — new `SpcMcd` settings block. When enabled,
  the bot detects active MCDs from the NWS products API, checks the MCD polygon against
  monitored zone/county centroids, and posts matching MCDs with the SPC-hosted image. MCDs
  use per-platform `IncludeSpcMcd` (independent of `IncludeSpcOutlooks`) and trigger expedited polling.
- **Map: IEM pre-flight verification + phenomena aliasing** — IEM silently returns a fixed
  default demo image (HTTP 200) for any unknown event instead of a 404. The bot now calls IEM's
  VTEC JSON API before requesting the PNG to confirm the event exists (`event_exists: true`);
  unverified events fall back to Mapbox. Additionally, NWS `HT.W` and `EH.W` are automatically
  tried as IEM's `XH.W` (Extreme Heat Warning) when the NWS code is not found in IEM's database.

- **Map: IEM autoplot as primary map source** — the bot now parses the VTEC code from each NWS
  alert and requests a pre-rendered PNG from IEM Autoplot #208. The image shows the exact NWS
  warning polygon, county boundary lines, and NEXRAD radar. No Mapbox token or geometry math
  required for VTEC events. Mapbox is retained as fallback for non-VTEC events.

- **Map: dissolved county perimeter overlay (Mapbox fallback)** — when falling back to Mapbox
  for events without a VTEC code, shared county borders are dissolved so only the outer perimeter
  of the combined area is drawn, fitting within Mapbox's URL limit.

- **NWS text: teletype line-wrap normalization** — NWS alert text (headline, description,
  instruction) uses hard line breaks at ~70 characters inherited from legacy teletype formatting.
  These mid-sentence wraps are now collapsed into spaces at parse time while intentional paragraph
  breaks (blank lines) are preserved. "stay out of\nthe sun" now reads "stay out of the sun"
  on every platform.

- **NWS geographic filter: zones + counties now combined** — previously only `Zones` were sent to
  the NWS API (with `Counties` silently ignored when both were configured). Both lists are now
  combined into a single `zone=` query parameter, since the NWS API accepts mixed UGC codes
  (`MNZxxx` and `MNCxxx`) in the same list. Configure both for complete coverage — some alert
  types are issued by zone, others by county. Same fix applied to the map fallback bounding box.

- **NWS API: resilient secondary query** — if the primary alert URL fails (e.g. bad config value,
  transient NWS outage), the `AdditionalEventTypes` secondary query now still runs and can return
  results. Previously, a primary failure silently aborted the secondary query too. The failed
  request URL is now logged at Error level so config typos are immediately visible in the log.

- **SPC outlook: fix tornado/wind/hail display for areas below explicit probability thresholds** — SPC only draws tornado probability polygons starting at 2% and wind/hail polygons starting at 5%. For any area in a categorical risk but below those thresholds, the bot previously displayed "None". It now displays "< 2%" for tornado and "< 5%" for wind/hail, matching SPC's actual convention. (The tornado layer also includes a background feature with `LABEL = "Less Than 2% All Areas"` that the parser skipped; the fix handles this implicitly via the corrected null default.)

- **Image handling: single download + direct upload for all platforms that support it** — previously each platform service that needed the map image fetched it independently (up to 7 simultaneous downloads of the same URL per alert). Now the orchestrator downloads the image once before dispatching and stores the bytes on `NwsAlert.MapImageBytes`; each service uses those bytes directly. Combined with the earlier switch from URL-embedding to direct upload for Discord, Discord DM, Telegram, and Facebook, all platforms that accept raw image bytes (X, Bluesky, Mastodon, Discord, Discord DM, Telegram, Facebook) now upload the bytes they already have. Instagram and Twilio remain URL-based — their APIs require a public URL and do not support direct byte upload.

- **Security/correctness fixes (code review — second pass):**
  - `AlertTrackerService`: replaced bare `HashSet<string>` with a `List` + `HashSet` pair so the
    prune operation always evicts the oldest entries. The previous `TakeLast` on an unordered set was
    non-deterministic and could discard recently-posted IDs, causing re-posts after a prune.
  - `SpcOutlookService`: fixed three bugs — (1) a transient NWS zone API failure at startup no longer
    permanently disables SPC monitoring for the process lifetime; (2) a malformed `DN` field in SPC
    GeoJSON no longer aborts the entire day's outlook check; (3) the deduplication ID no longer falls
    back to `UtcNow` when `ISSUE_ISO` is absent (it now uses `EXPIRE_ISO` instead, which is stable for
    the outlook period — avoiding a re-post every 30 minutes).
  - `BlueskyService`: added null check on `_accessJwt` after retry re-authentication so a failed
    re-auth attempt doesn't cause a wasted API call with an empty bearer token.
  - `NwsZoneService`: zone codes are now uppercased before use so lowercase entries in config
    (e.g. `"moc217"`) are handled correctly. Added an in-memory result cache so each zone is fetched
    at most once per session — eliminates redundant NWS API calls across `MapService` and
    `SpcOutlookService`.
  - `MapService`: zone fetches for alert bounding boxes are now parallelized with `Task.WhenAll`,
    removing the serial per-county delay (previously 2–5 seconds for multi-county alerts).
  - `InstagramService`: fixed missing `using` on `JsonDocument` in `CreateMediaContainerAsync`.
  - `Program.cs`: added `HttpClient` request-URL log suppression for Bluesky, X, and Mastodon —
    those services fetch the Mapbox map image via `GetByteArrayAsync`, which logged the full URL
    including the Mapbox access token. (Telegram was fixed in the first pass; these three were missed.)
- **Security/correctness fixes (code review — first pass):**
  - VoIP.ms: switched from GET+querystring to POST+form-body so API credentials are no longer
    included in request URLs (which were logged at `Information` level by the HttpClient pipeline).
  - Telegram: suppressed `HttpClient` infrastructure logging for `TelegramService` — Telegram's Bot
    API embeds the bot token in the URL path by design, so request URL logging was leaking the token.
  - `StartupConfirmationService`: disabled platforms are now excluded from the pending list instead
    of producing a misleading "delivery failed — check credentials" warning on every startup.
  - `SocialMediaOrchestrator`: only NWS alerts engage active storm mode (accelerated polling). SPC
    outlooks are posted but do not affect the polling interval — they update only a few times per day
    and are independent of active NWS warnings.
  - `BlueskyService`: re-authentication retry now only triggers on HTTP 401 Unauthorized, not on
    rate-limit, content-policy, or transient errors.
  - Removed unused `AppSettings` class (all settings were bound per-section directly) and the
    unused `NwsAlert.SeverityRank` property.
- Added an [Image Smoke Test](#image-smoke-test) dev tool (`ImageSmokeTestService`, run via
  `dotnet run -- --smoke-test-image`) — posts one synthetic alert with a real test image to every
  enabled image-capable platform and reports pass/fail, since startup confirmation never
  exercises the image-attachment code path.
- Added SPC outlook map images via [Iowa State's IEM Mesonet plotting service](https://mesonet.agron.iastate.edu/plotting/auto/?q=220)
  (free, no API key) — every SPC Day 1/Day 2 outlook post now carries a categorical risk map
  image cropped to the location's WFO/state, attached through the same `MapImageUrl` field as
  Mapbox alert maps. `NwsZoneService` now also resolves a zone's WFO (`cwa`) and `state` from the
  same NWS zone API call used for centroid resolution (added `GetZoneInfoAsync`/`ZoneInfo`).
  Image attachment was also added to Facebook (`/photos`), X (v1.1 media upload), Bluesky
  (`uploadBlob` + image embed), Mastodon (media upload), and Twilio (MMS via `MediaUrl`) — see
  the updated [Map Images](#map-images-mapbox) platform behavior table.
- Removed ntfy support (`NtfyService`, `Ntfy` settings block) — Telegram replaces it as the
  free/self-hostable push notification option.
- Added Telegram support (`TelegramService`, `Telegram` settings block) — posts alerts to a
  Telegram chat or channel via the Bot API. Sends `sendPhoto` with a caption when a Mapbox map
  image is available, otherwise a plain `sendMessage`. See [Telegram](#telegram) and the
  confirmation/map-image behavior tables above.
- Added [SPC Convective Outlook Monitoring](#spc-convective-outlook-monitoring) — alerts on
  Day 1/Day 2 categorical risk (Thunderstorm or higher) plus tornado/wind/hail probability for
  monitored zones/counties, posted through the existing platform pipeline. New `Spc` settings
  block. `MapService`'s zone/county geometry fetch was extracted into a shared `NwsZoneService`
  (used by both the map bounding-box fallback and the new SPC location resolution).
