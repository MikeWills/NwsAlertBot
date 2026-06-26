# NWS Alert Social Media Bot

A .NET 8 C# console application that polls the National Weather Service API for active weather
alerts and posts them to Facebook, Instagram, X (Twitter), Bluesky, Mastodon, Discord (webhook
and DM), and Telegram — and sends real-time push notifications and SMS via Pushover, Twilio, and VoIP.ms.

---

## Table of Contents

1. [Setup](#setup)
2. [Configuration Reference](#configuration-reference)
3. [Geographic Filtering: Zones and Counties](#geographic-filtering-zones-and-counties)
4. [SPC Convective Outlook Monitoring](#spc-convective-outlook-monitoring)
5. [Alert Filtering: Severity, Urgency, Certainty, Event Types](#alert-filtering)
6. [Complete NWS Event Type Reference](#complete-nws-event-type-reference)
7. [API Credentials — Social Media](#api-credentials)
8. [Push / SMS Notifications](#push--sms-notifications)
9. [Map Images (Mapbox)](#map-images-mapbox)
10. [Running the Bot](#running-the-bot)
11. [Deploying to Ubuntu (GitHub Actions)](#deploying-to-ubuntu-github-actions)

---

## Setup 

### Requirements
- .NET 8 SDK  
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

---

## Configuration Reference

All configuration lives in `appsettings.json`.

```json
{
  "Nws": {
    "Zones":              ["MOZ066", "MOZ067"],
    "Counties":           ["MOC217", "MOC039"],
    "State":              "MO",
    "PollIntervalSeconds": 300,
    "ActiveAlertPollIntervalSeconds": 60,
    "ActiveAlertWindowHours": 4,
    "Severity":           "Severe,Extreme",
    "Urgency":            "",
    "Certainty":          "",
    "EventTypes":         ""
  }
}
```

| Field | Description | Default |
|---|---|---|
| `Zones` | NWS forecast zone codes (see below) | `[]` |
| `Counties` | NWS county codes (see below) | `[]` |
| `State` | Two-letter state code fallback | `""` |
| `PollIntervalSeconds` | Idle poll interval in seconds — used when no active storm window is open | `300` |
| `ActiveAlertPollIntervalSeconds` | Accelerated poll interval in seconds while an active storm window is open | `60` |
| `ActiveAlertWindowHours` | Hours to stay in accelerated polling after the last new NWS alert; resets on each new NWS alert. SPC outlooks do not affect the storm window. | `4` |
| `Severity` | Comma-separated severity levels to include | `"Severe,Extreme"` |
| `Urgency` | Comma-separated urgency levels to include | `""` (all) |
| `Certainty` | Comma-separated certainty levels to include | `""` (all) |
| `EventTypes` | Comma-separated event names to include | `""` (all) |

Each platform block also accepts:

| Field | Description | Default |
|---|---|---|
| `MinSeverity` | Comma-separated severity levels for this platform only. Leave empty to use the global `Severity` filter. | `""` (inherit global) |
| `EventTypes` | Comma-separated NWS event names for this platform only. Leave empty to receive all event types. | `""` (all) |
| `IncludeSpcOutlooks` | Whether SPC Convective Outlook alerts are posted to this platform. Requires `Spc.Enabled = true`. | `true` |

**Geographic priority:** `Zones` > `Counties` > `State`. If Zones are specified, Counties and
State are ignored. If Counties are specified, State is ignored.

`Spc` (see [SPC Convective Outlook Monitoring](#spc-convective-outlook-monitoring)):

```json
"Spc": {
  "Enabled": false,
  "CheckIntervalSeconds": 1800,
  "TimeZone": "America/Chicago"
}
```

| Field | Description | Default |
|---|---|---|
| `Enabled` | Whether to monitor SPC Day 1/Day 2 Convective Outlooks | `false` |
| `CheckIntervalSeconds` | Minimum seconds between SPC outlook checks | `1800` |
| `TimeZone` | IANA timezone ID for formatting Valid/Expires times on SPC outlook posts. Works on Windows and Linux. | `"America/Chicago"` |

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
"Zones":    ["MOZ066", "MOZ067"],
"Counties": ["MOC217", "MOC039"]
```

When both are specified, the bot queries zones first, then counties separately, and deduplicates
by alert ID before posting.

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

- **Locations monitored** are derived from the same `Nws.Zones` (or `Nws.Counties` if `Zones`
  is empty) already configured for warning geo-filtering above — there is no separate location
  list to maintain. Each zone/county's polygon is fetched once from the NWS zone API and reduced
  to its area centroid (geometric center); that point is what gets checked against the SPC
  outlook polygons. Resolution happens once at startup and is cached for the life of the
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
  `Nws.PollIntervalSeconds`, since SPC re-issues the Day 1 outlook ~5x/day and Day 2 ~2x/day;
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

### Severity

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
"Severity": "Severe,Extreme"        // Warnings only — high-impact events
"Severity": "Moderate,Severe,Extreme" // Add winter storms, flood warnings, etc.
"Severity": ""                       // All alerts including advisories (noisy)
```

### Urgency

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
"Urgency": "Immediate,Expected"
```

### Certainty

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
"Certainty": "Observed,Likely"
```

### Per-Platform Severity Filter (`MinSeverity`)

Each platform has an optional `MinSeverity` field that lets you restrict which alerts that platform
receives, independently of the global `Severity` filter. Leave it empty to pass everything that
the global filter allows through.

**Example — Pushover gets all alerts, social media gets only Severe, Extreme:**

```json
"Nws": {
  "Severity": ""
},
"Facebook":  { "MinSeverity": "Severe,Extreme" },
"Instagram": { "MinSeverity": "Severe,Extreme" },
"X":         { "MinSeverity": "Severe,Extreme" },
"Bluesky":   { "MinSeverity": "Severe,Extreme" },
"Mastodon":  { "MinSeverity": "Severe,Extreme" },
"Pushover":  { "MinSeverity": "" },
"Twilio":    { "MinSeverity": "" }
```

The global `Severity` filter is applied server-side at the NWS API — it sets the floor for what
gets fetched at all. Per-platform `MinSeverity` and `EventTypes` are applied client-side after
the fetch. A platform cannot receive alerts below the global floor, only above it.

**Example — social media gets only tornado alerts; Pushover gets everything:**

```json
"Nws": { "Severity": "" },
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

### EventTypes

Override all severity/urgency/certainty filters and post only specific named event types.
Leave blank to rely on severity/urgency/certainty filtering instead.

```json
"EventTypes": "Tornado Warning,Tornado Watch,Flash Flood Warning"
```

See the complete list of event types in the next section.

---

## Complete NWS Event Type Reference

These are the official NWS event names used in the `event` field of each alert and in the
`EventTypes` filter. Source: NWS VTEC Version 9.0 (March 2025) —
https://www.weather.gov/media/vtec/VTEC_explanation_ver9.pdf

The **Severity** column shows the value(s) the NWS API returns for that event type. These are
the same values used in the `Severity` and `MinSeverity` config fields. Where two values are
listed (e.g. `Severe, Extreme`), the NWS assigns severity at issuance time — to reliably catch
that event type, include **both** values in your filter.

| Severity value | What to put in `Severity` or `MinSeverity` |
|---|---|
| Extreme only | `"Extreme"` |
| Severe or Extreme | `"Severe,Extreme"` |
| Moderate, Severe, or Extreme | `"Moderate,Severe,Extreme"` |
| Minor (advisories) | `"Minor,Moderate,Severe,Extreme"` or `""` (all) |

### Warnings
*Conditions are occurring or imminent. Take action now.*

#### Convective / Severe Weather
| Event Name | Severity | Notes |
|---|---|---|
| Tornado Warning | Severe, Extreme | Immediate life threat. Tornado observed or radar-indicated |
| Severe Thunderstorm Warning | Severe | Winds ≥ 58 mph and/or hail ≥ 1 inch diameter |
| Snow Squall Warning | Severe | Brief but intense snow + gusty winds causing rapid visibility/road condition changes |
| Extreme Wind Warning | Extreme | Surface winds ≥ 115 mph (non-convective) |

#### Flood / Hydrologic
| Event Name | Severity | Notes |
|---|---|---|
| Flash Flood Warning | Severe, Extreme | Sudden flooding occurring or imminent |
| Flood Warning | Moderate, Severe | River/stream flooding occurring or imminent |
| Coastal Flood Warning | Moderate, Severe | Significant coastal flooding from storm surge or waves |
| Lakeshore Flood Warning | Moderate | Lakeshore flooding occurring or imminent |
| Storm Surge Warning | Extreme | Life-threatening inundation (tropical systems) within 36 hours |
| Tsunami Warning | Extreme | Take action immediately — move to high ground |
| High Surf Warning | Moderate, Severe | Dangerously high breaking waves |

#### Winter Weather
| Event Name | Severity | Notes |
|---|---|---|
| Blizzard Warning | Severe | Sustained/frequent winds ≥ 35 mph + considerable snow/blowing snow |
| Winter Storm Warning | Moderate, Severe | Hazardous mix of winter precipitation meeting warning criteria |
| Ice Storm Warning | Severe | Significant ice accumulation (typically ≥ 0.25 inch) |
| Heavy Snow Warning | Moderate, Severe | Heavy snowfall meeting warning threshold (varies by region) |
| Lake Effect Snow Warning | Moderate, Severe | Heavy snow from lake-effect bands |
| Freezing Rain Warning | Moderate, Severe | Significant freezing rain accumulation |
| Sleet Warning | Moderate | Significant sleet accumulation |

#### Wind
| Event Name | Severity | Notes |
|---|---|---|
| High Wind Warning | Moderate, Severe | Sustained winds ≥ 40 mph or gusts ≥ 58 mph (inland) |
| Hurricane Warning | Extreme | Sustained winds ≥ 74 mph from tropical system within 36 hours |
| Typhoon Warning | Extreme | Pacific equivalent of Hurricane Warning |
| Tropical Storm Warning | Severe | Sustained winds 39–73 mph from tropical system within 36 hours |
| Hurricane Force Wind Warning | Severe | Sustained winds ≥ 64 knots (marine) |
| Gale Warning | Moderate | Sustained winds 34–47 knots (marine) |
| Storm Warning | Severe | Sustained winds ≥ 48 knots (marine) |

#### Fire Weather
| Event Name | Severity | Notes |
|---|---|---|
| Red Flag Warning | Moderate, Severe | Critical fire weather conditions (combination of low humidity, high winds, dry fuels) |

#### Other Warnings
| Event Name | Severity | Notes |
|---|---|---|
| Dust Storm Warning | Severe | Blowing dust reducing visibility to ¼ mile or less |
| Dense Fog Warning | Moderate | Visibility reduced to ¼ mile or less by fog |
| Freeze Warning | Moderate | Temperatures dropping to 32°F or below during growing season |
| Ashfall Warning | Moderate, Severe | Heavy volcanic ash accumulation expected |
| Debris Flow Warning | Severe | Significant debris flow (mudslide) occurring or imminent |
| Avalanche Warning | Extreme | Life-threatening avalanche conditions |

---

### Watches
*Conditions are favorable for the hazard to develop. Stay alert and be prepared.*

| Event Name | Severity | Notes |
|---|---|---|
| Tornado Watch | Severe | Conditions favorable for tornadoes. Issued by Storm Prediction Center |
| Severe Thunderstorm Watch | Moderate, Severe | Conditions favorable for severe thunderstorms. Issued by SPC |
| Flash Flood Watch | Moderate | Flash flooding possible within watch area |
| Flood Watch | Minor, Moderate | Flooding possible |
| Winter Storm Watch | Minor, Moderate | Significant winter storm possible 24–48 hours out |
| Blizzard Watch | Moderate | Blizzard conditions possible |
| Ice Storm Watch | Moderate | Significant ice accumulation possible |
| High Wind Watch | Moderate | High winds possible |
| Hurricane Watch | Severe, Extreme | Hurricane conditions possible within 48 hours |
| Tropical Storm Watch | Moderate | Tropical storm conditions possible within 48 hours |
| Storm Surge Watch | Severe, Extreme | Life-threatening storm surge possible (tropical) within 48 hours |
| Coastal Flood Watch | Moderate | Coastal flooding possible |
| Freeze Watch | Minor, Moderate | Temperatures may drop to 32°F or below during growing season |
| Fire Weather Watch | Moderate | Critical fire weather conditions possible |
| Avalanche Watch | Severe | Dangerous avalanche conditions possible |
| Tsunami Watch | Severe, Extreme | Tsunami possible — monitor for further information |

---

### Advisories
*Less severe conditions that may still cause significant inconvenience or hazard.*

#### Winter/Precipitation
| Event Name | Severity | Notes |
|---|---|---|
| Winter Weather Advisory | Minor | Mix of winter precipitation below warning criteria |
| Wind Chill Advisory | Minor | Wind chills that are cold but below Warning threshold |
| Freezing Rain Advisory | Minor | Light freezing rain or drizzle — slippery surfaces |
| Frost Advisory | Minor | Near-freezing temperatures during the growing season |
| Freeze Advisory | Minor | Sub-freezing temperatures (slightly above Freeze Warning threshold) |
| Lake Effect Snow Advisory | Minor | Light to moderate lake-effect snow |

#### Wind
| Event Name | Severity | Notes |
|---|---|---|
| Wind Advisory | Minor | Sustained winds 25–39 mph or gusts 40–57 mph (inland) |
| Lake Wind Advisory | Minor | Winds causing hazardous conditions on lakes |

#### Visibility / Air Quality
| Event Name | Severity | Notes |
|---|---|---|
| Dense Fog Advisory | Minor | Visibility reduced to ¼ mile or less |
| Dense Smoke Advisory | Minor | Visibility reduced to ¼ mile or less by smoke |
| Blowing Dust Advisory | Minor | Blowing dust reducing visibility |
| Air Quality Alert | Minor | Air quality index reaching unhealthy levels |
| Ashfall Advisory | Minor | Light volcanic ash fall |

#### Flood / Water
| Event Name | Severity | Notes |
|---|---|---|
| Flood Advisory | Minor | Minor flooding in low-lying or flood-prone areas |
| Coastal Flood Advisory | Minor | Minor coastal flooding — nuisance levels |
| Lakeshore Flood Advisory | Minor | Minor lakeshore flooding |
| Rip Current Statement | Minor | High risk of rip currents at beaches |
| Beach Hazard Statement | Minor | Combination of hazardous beach conditions |
| Small Craft Advisory | Minor | Marine: winds/seas making conditions hazardous for small craft |
| Hazardous Seas Warning | Moderate | Significant wave heights hazardous for all vessels |
| Low Water Advisory | Minor | Abnormally low water levels on rivers or lakes |

#### Heat / Cold
| Event Name | Severity | Notes |
|---|---|---|
| Heat Advisory | Minor | Heat index 100–104°F (thresholds vary by region) |
| Excessive Heat Warning | Severe | Heat index ≥ 105°F for 2+ consecutive days |
| Excessive Heat Watch | Moderate | Excessive heat conditions possible |
| Wind Chill Warning | Moderate, Severe | Dangerously cold wind chills |
| Extreme Cold Warning | Severe | Dangerously cold air temperatures |
| Extreme Cold Watch | Moderate | Dangerously cold conditions possible |

---

### Statements and Outlooks
*Informational products — less urgent, lower severity.*

| Event Name | Type | Notes |
|---|---|---|
| Special Weather Statement | Statement | Short-term advisory for conditions below advisory criteria; may include brief tornado/thunderstorm info |
| Hydrologic Outlook | Outlook | Long-range flood potential; not an immediate alert |
| Marine Weather Statement | Statement | Significant marine weather not meeting advisory criteria |
| Air Stagnation Advisory | Advisory | Poor ventilation conditions |

---

## Recommended Filter Configurations

### Life-threatening emergencies only
```json
"Severity": "Extreme",
"Urgency":  "Immediate"
```
Posts: Tornado Warnings, Flash Flood Emergencies, Storm Surge Warnings, Tsunami Warnings

### All warnings (no watches or advisories)
```json
"Severity":   "Severe,Extreme",
"Urgency":    "",
"Certainty":  "",
"EventTypes": ""
```

### Warnings and watches (no advisories)
```json
"Severity":   "Moderate,Severe,Extreme",
"Urgency":    "",
"Certainty":  "Observed,Likely,Possible",
"EventTypes": ""
```

### Specific event types only
```json
"Severity":   "",
"EventTypes": "Tornado Warning,Tornado Watch,Severe Thunderstorm Warning,Flash Flood Warning,Flash Flood Watch"
```

### Everything (high volume — use with specific zone filtering)
```json
"Severity":   "",
"Urgency":    "",
"Certainty":  "",
"EventTypes": ""
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

## Map Images (Mapbox)

When enabled, the bot generates a **Mapbox Static Images** URL for each NWS alert (warnings,
watches, advisories) and attaches it via the alert's `MapImageUrl` field to every platform that
supports images. SPC Convective Outlook posts get their own image independently of this setting
— see [Outlook map image](#how-it-works) above — through the same `MapImageUrl` field, so the
platform behavior table below applies to both.

The Mapbox map area is determined by:
1. The **alert's own GeoJSON geometry polygon** (included in most NWS alerts)
2. **Fallback:** the union bounding box of your configured zones/counties, fetched once from the
   NWS zone API on first use and cached for the life of the process

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
| Facebook | Posted to `/photos` (URL + caption) instead of `/feed`, so the map renders inline. No image download needed — Facebook fetches the URL itself. |
| Instagram | Uses the map URL instead of the static `ImageUrl`. Falls back to `ImageUrl` if no map is available. |
| X (Twitter) | Image is downloaded and uploaded via the v1.1 media endpoint, then attached to the tweet by `media_id`. If upload fails, the tweet still posts as text-only. |
| Bluesky | Image is downloaded and uploaded via `uploadBlob`, then attached as an `app.bsky.embed.images` embed. If upload fails, the post still goes out as text-only. |
| Mastodon | Image is downloaded and uploaded via the media endpoint, then attached by `media_ids[]`. If upload fails, the status still posts as text-only. |
| Discord | Map appears as the embed image below the alert text. |
| Telegram | Sent as a photo (`sendPhoto`) with the alert text as the caption (1,024-character limit) instead of a plain text message. |
| Twilio | Sent as MMS via the `MediaUrl` field — Twilio fetches the URL itself, no download needed. |
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
3. Add your server's public IP address to the API allow list on the same page
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

**SMS length:** Messages are kept to 160 characters (1 SMS segment).

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

## Recent Changes

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
