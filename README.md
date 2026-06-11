# NWS Alert Social Media Bot

A .NET 8 C# console application that polls the National Weather Service API for active weather
alerts and posts them to Facebook, Instagram, X (Twitter), Bluesky, and Mastodon — and sends
real-time push notifications and SMS via Pushover, ntfy, and Twilio.

---

## Table of Contents

1. [Setup](#setup)
2. [Configuration Reference](#configuration-reference)
3. [Geographic Filtering: Zones and Counties](#geographic-filtering-zones-and-counties)
4. [Alert Filtering: Severity, Urgency, Certainty, Event Types](#alert-filtering)
5. [Complete NWS Event Type Reference](#complete-nws-event-type-reference)
6. [API Credentials — Social Media](#api-credentials)
7. [Push / SMS Notifications](#push--sms-notifications)
8. [Running the Bot](#running-the-bot)
9. [Deploying to Ubuntu (GitHub Actions)](#deploying-to-ubuntu-github-actions)

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
    "PollIntervalSeconds": 60,
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
| `PollIntervalSeconds` | How often to check for new alerts | `60` |
| `Severity` | Comma-separated severity levels to include | `"Severe,Extreme"` |
| `Urgency` | Comma-separated urgency levels to include | `""` (all) |
| `Certainty` | Comma-separated certainty levels to include | `""` (all) |
| `EventTypes` | Comma-separated event names to include | `""` (all) |

Each platform block also accepts:

| Field | Description | Default |
|---|---|---|
| `MinSeverity` | Comma-separated severity levels for this platform only. Leave empty to use the global `Severity` filter. | `""` (inherit global) |
| `EventTypes` | Comma-separated NWS event names for this platform only. Leave empty to receive all event types. | `""` (all) |

**Geographic priority:** `Zones` > `Counties` > `State`. If Zones are specified, Counties and
State are ignored. If Counties are specified, State is ignored.

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

## Alert Filtering

All filter fields are pushed directly to the NWS API — the bot does not pull all alerts and
filter locally. This keeps API responses small and fast.

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

**Example — ntfy gets all alerts, social media gets only Severe, Extreme:**

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
"Twilio":    { "MinSeverity": "" },
"Ntfy":      { "MinSeverity": "" }
```

The global `Severity` filter is applied server-side at the NWS API — it sets the floor for what
gets fetched at all. Per-platform `MinSeverity` and `EventTypes` are applied client-side after
the fetch. A platform cannot receive alerts below the global floor, only above it.

**Example — social media gets only tornado alerts; ntfy gets everything:**

```json
"Nws": { "Severity": "" },
"Facebook": { "EventTypes": "Tornado Warning,Tornado Watch" },
"X":        { "EventTypes": "Tornado Warning,Tornado Watch" },
"Bluesky":  { "EventTypes": "Tornado Warning,Tornado Watch" },
"Ntfy":     { "EventTypes": "" }
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
Requires a Meta Developer account and app review for `pages_manage_posts` permission.
- Developer portal: https://developers.facebook.com/
- You need a **never-expiring Page Access Token** (generated via a long-lived user token)
- Guide: https://postproxy.dev/blog/facebook-graph-api-posting-guide/
- **Note:** Automated posting to personal profiles is not supported by the API.

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

### Bluesky
- No developer account or review process required
- Generate an **App Password** (not your main password): bsky.app → Settings → Privacy and
  Security → App Passwords
- Free, no rate limit concerns for typical alert volumes

### Mastodon
- No developer account required
- Generate an access token from your instance: Settings → Development → New Application
- Set scope to `write:statuses`
- Free, open source, no rate limit concerns for typical alert volumes

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

The bot supports three notification providers simultaneously. Enable any combination in
`appsettings.json` by setting `"Enabled": true`. All three send at the same time as social
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

API docs: https://www.twilio.com/docs/messaging/api/message-resource

---

### ntfy (Push Notification — Free, Self-Hostable)

**Cost:** Free (hosted) or self-hosted on your own server
**Latency:** Near-instant (<5 seconds typical)

ntfy is an open-source HTTP pub/sub notification service. You subscribe to a topic in the ntfy
app, and the bot POSTs to that topic URL to send a notification. No account required for basic
use on ntfy.sh.

**Setup (hosted ntfy.sh — easiest):**
1. Install the ntfy app: https://ntfy.sh/ (iOS and Android)
2. In the app, subscribe to a topic — use a long, random string (e.g. `nws-alerts-j7k2m9p4`)
   because topic names are public on ntfy.sh unless you use access control
3. Set `ServerUrl` to `"https://ntfy.sh"` and `Topic` to your topic name
4. Leave `Username` and `Password` empty

**Setup (self-hosted):**
1. Deploy ntfy: https://docs.ntfy.sh/install/ (Docker, binary, or apt)
2. Configure access control if desired: https://docs.ntfy.sh/config/#access-control
3. Set `ServerUrl` to your instance URL
4. Set `Username` and `Password` if using access control

**Priority levels (ntfy):**

| Value | Behavior |
|---|---|
| `1` | Min — no notification shown |
| `2` | Low — notification, no sound |
| `3` | Default — notification with sound |
| `4` | High — high-priority notification |
| `5` | Urgent (max) — bypasses Do Not Disturb on Android |

**Recommended configuration:**
```json
"Ntfy": {
  "Enabled": true,
  "ServerUrl": "https://ntfy.sh",
  "Topic": "nws-alerts-your-random-string-here",
  "DefaultPriority": 4,
  "ExtremePriority": 5
}
```

The bot automatically sets emoji tags in ntfy notifications based on event type
(⚠️ warning, 🌪️ tornado, 💧 flood, ❄️ snow, etc.) which appear in the app notification.

**Note on iOS and DND bypass:** On iOS, ntfy priority 5 triggers a "time-sensitive" notification
that can break through Focus modes, but this requires granting the ntfy app permission to send
time-sensitive notifications in iOS Settings.

API docs: https://docs.ntfy.sh/publish/

---

### Using Multiple Providers Together

All three providers can be enabled simultaneously and run concurrently per alert. A common
combination:

```json
"Pushover": { "Enabled": true, ... },   // Primary push for your phone
"Ntfy":     { "Enabled": true, ... },   // Free secondary push / family alerts
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
| ntfy | Priority 3 (default) | Shows with ✅ tag |
