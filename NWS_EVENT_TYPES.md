# NWS Event Type Reference

Complete list of National Weather Service event types, their severity classifications, and notes.
Source: NWS VTEC Version 9.0 (March 2025) — https://www.weather.gov/media/vtec/VTEC_explanation_ver9.pdf

Use event names exactly as shown in the `EventTypes` config field.

**Severity values** (used in `Severity`, `MinSeverity`, and `EventTypes` filters):

| Value | Meaning |
|---|---|
| `Extreme` | Extraordinary threat to life or property |
| `Severe` | Significant threat |
| `Moderate` | Possible threat |
| `Minor` | Minimal threat |

Where two severity values are listed (e.g. `Severe, Extreme`), the NWS assigns severity at issuance time.
To reliably catch that event type, include **both** values in your filter.

---

## Warnings
*Conditions are occurring or imminent. Take action now.*

### Convective / Severe Weather

| Event Name | Severity | Notes |
|---|---|---|
| Tornado Warning | Severe, Extreme | Extreme when a tornado emergency is declared |
| Severe Thunderstorm Warning | Severe | Winds ≥ 58 mph and/or hail ≥ 1 inch |
| Snow Squall Warning | Severe | Brief but intense snow + gusty winds |
| Extreme Wind Warning | Extreme | Surface winds ≥ 115 mph (non-convective) |

### Flood / Hydrologic

| Event Name | Severity | Notes |
|---|---|---|
| Flash Flood Warning | Severe, Extreme | Extreme when a Flash Flood Emergency is declared |
| Flood Warning | Moderate, Severe | River/stream flooding occurring or imminent |
| Coastal Flood Warning | Moderate, Severe | Significant coastal flooding from storm surge or waves |
| Lakeshore Flood Warning | Moderate | Lakeshore flooding occurring or imminent |
| Storm Surge Warning | Extreme | Life-threatening inundation within 36 hours (tropical) |
| Tsunami Warning | Extreme | Move to high ground immediately |
| High Surf Warning | Moderate, Severe | Dangerously high breaking waves |
| Hazardous Seas Warning | Moderate | Significant wave heights hazardous for all vessels |

### Winter Weather

| Event Name | Severity | Notes |
|---|---|---|
| Blizzard Warning | Severe | Sustained/frequent winds ≥ 35 mph + considerable snow/blowing snow |
| Winter Storm Warning | Moderate, Severe | Hazardous mix of winter precipitation |
| Ice Storm Warning | Severe | Significant ice accumulation (typically ≥ 0.25 inch) |
| Heavy Snow Warning | Moderate, Severe | Heavy snowfall meeting warning threshold (varies by region) |
| Lake Effect Snow Warning | Moderate, Severe | Heavy snow from lake-effect bands |
| Freezing Rain Warning | Moderate, Severe | Significant freezing rain accumulation |
| Sleet Warning | Moderate | Significant sleet accumulation |

### Wind

| Event Name | Severity | Notes |
|---|---|---|
| High Wind Warning | Moderate, Severe | Sustained winds ≥ 40 mph or gusts ≥ 58 mph (inland) |
| Hurricane Warning | Extreme | Sustained winds ≥ 74 mph from tropical system within 36 hours |
| Typhoon Warning | Extreme | Pacific equivalent of Hurricane Warning |
| Tropical Storm Warning | Severe | Sustained winds 39–73 mph from tropical system within 36 hours |
| Hurricane Force Wind Warning | Severe | Sustained winds ≥ 64 knots (marine) |
| Gale Warning | Moderate | Sustained winds 34–47 knots (marine) |
| Storm Warning | Severe | Sustained winds ≥ 48 knots (marine) |

### Fire Weather

| Event Name | Severity | Notes |
|---|---|---|
| Red Flag Warning | Moderate, Severe | Critical fire weather: low humidity + high winds + dry fuels |

### Heat / Cold

| Event Name | Severity | Notes |
|---|---|---|
| Excessive Heat Warning | Severe | Heat index ≥ 105°F for 2+ consecutive days |
| Wind Chill Warning | Moderate, Severe | Dangerously cold wind chills |
| Extreme Cold Warning | Severe | Dangerously cold air temperatures |
| Freeze Warning | Moderate | Temperatures dropping to 32°F or below during growing season |

### Other

| Event Name | Severity | Notes |
|---|---|---|
| Dust Storm Warning | Severe | Blowing dust reducing visibility to ¼ mile or less |
| Dense Fog Warning | Moderate | Visibility reduced to ¼ mile or less by fog |
| Ashfall Warning | Moderate, Severe | Heavy volcanic ash accumulation expected |
| Debris Flow Warning | Severe | Significant debris flow (mudslide) occurring or imminent |
| Avalanche Warning | Extreme | Life-threatening avalanche conditions |

---

## Watches
*Conditions are favorable for the hazard to develop. Stay alert and be prepared.*

### Convective / Severe Weather

| Event Name | Severity | Notes |
|---|---|---|
| Tornado Watch | Severe | Conditions favorable for tornadoes — issued by SPC |
| Severe Thunderstorm Watch | Moderate, Severe | Conditions favorable for severe thunderstorms — issued by SPC |

### Flood / Hydrologic

| Event Name | Severity | Notes |
|---|---|---|
| Flash Flood Watch | Moderate | Flash flooding possible |
| Flood Watch | Minor, Moderate | Flooding possible |
| Coastal Flood Watch | Moderate | Coastal flooding possible |
| Storm Surge Watch | Severe, Extreme | Life-threatening storm surge possible within 48 hours (tropical) |
| Tsunami Watch | Severe, Extreme | Tsunami possible — monitor for further information |

### Winter Weather

| Event Name | Severity | Notes |
|---|---|---|
| Winter Storm Watch | Minor, Moderate | Significant winter storm possible 24–48 hours out |
| Blizzard Watch | Moderate | Blizzard conditions possible |
| Ice Storm Watch | Moderate | Significant ice accumulation possible |

### Wind / Tropical

| Event Name | Severity | Notes |
|---|---|---|
| High Wind Watch | Moderate | High winds possible |
| Hurricane Watch | Severe, Extreme | Hurricane conditions possible within 48 hours |
| Tropical Storm Watch | Moderate | Tropical storm conditions possible within 48 hours |

### Fire / Other

| Event Name | Severity | Notes |
|---|---|---|
| Fire Weather Watch | Moderate | Critical fire weather conditions possible |
| Freeze Watch | Minor, Moderate | Temperatures may drop to 32°F or below during growing season |
| Excessive Heat Watch | Moderate | Excessive heat conditions possible |
| Extreme Cold Watch | Moderate | Dangerously cold conditions possible |
| Avalanche Watch | Severe | Dangerous avalanche conditions possible |

---

## Advisories
*Less severe conditions that may still cause significant inconvenience or hazard.*

### Winter / Precipitation

| Event Name | Severity | Notes |
|---|---|---|
| Winter Weather Advisory | Minor | Mix of winter precipitation below warning criteria |
| Wind Chill Advisory | Minor | Cold wind chills below Warning threshold |
| Freezing Rain Advisory | Minor | Light freezing rain or drizzle — slippery surfaces |
| Frost Advisory | Minor | Near-freezing temperatures during growing season |
| Freeze Advisory | Minor | Sub-freezing temperatures slightly above Freeze Warning threshold |
| Lake Effect Snow Advisory | Minor | Light to moderate lake-effect snow |

### Wind

| Event Name | Severity | Notes |
|---|---|---|
| Wind Advisory | Minor | Sustained winds 25–39 mph or gusts 40–57 mph (inland) |
| Lake Wind Advisory | Minor | Winds causing hazardous conditions on lakes |

### Visibility / Air Quality

| Event Name | Severity | Notes |
|---|---|---|
| Dense Fog Advisory | Minor | Visibility reduced to ¼ mile or less |
| Dense Smoke Advisory | Minor | Visibility reduced to ¼ mile or less by smoke |
| Blowing Dust Advisory | Minor | Blowing dust reducing visibility |
| Air Quality Alert | Minor | Air quality index reaching unhealthy levels |
| Ashfall Advisory | Minor | Light volcanic ash fall |

### Flood / Water

| Event Name | Severity | Notes |
|---|---|---|
| Flood Advisory | Minor | Minor flooding in low-lying or flood-prone areas |
| Coastal Flood Advisory | Minor | Minor coastal flooding — nuisance levels |
| Lakeshore Flood Advisory | Minor | Minor lakeshore flooding |
| Rip Current Statement | Minor | High risk of rip currents at beaches |
| Beach Hazard Statement | Minor | Combination of hazardous beach conditions |
| Small Craft Advisory | Minor | Marine: winds/seas hazardous for small craft |
| Low Water Advisory | Minor | Abnormally low water levels on rivers or lakes |

### Heat

| Event Name | Severity | Notes |
|---|---|---|
| Heat Advisory | Minor | Heat index 100–104°F (thresholds vary by region) |

---

## Statements and Outlooks
*Informational products — less urgent, lower severity.*

| Event Name | Severity | Notes |
|---|---|---|
| Special Weather Statement | Minor | Short-term advisory for conditions below advisory criteria |
| Hydrologic Outlook | Minor | Long-range flood potential; not an immediate alert |
| Marine Weather Statement | Minor | Significant marine weather not meeting advisory criteria |
| Air Stagnation Advisory | Minor | Poor ventilation conditions |

---

## Quick Filter Reference

### Recommended `Severity` settings

| Goal | Setting |
|---|---|
| Life-threatening emergencies only | `"Extreme"` |
| All warnings (no watches or advisories) | `"Severe,Extreme"` |
| Warnings + watches | `"Moderate,Severe,Extreme"` |
| Everything including advisories | `""` (empty = all) |

### Example `EventTypes` overrides

```json
// Tornado and severe thunderstorm only
"EventTypes": "Tornado Warning,Tornado Watch,Severe Thunderstorm Warning,Severe Thunderstorm Watch"

// All convective warnings + watches
"EventTypes": "Tornado Warning,Tornado Watch,Severe Thunderstorm Warning,Severe Thunderstorm Watch,Flash Flood Warning,Flash Flood Watch"

// Winter storm alerts only
"EventTypes": "Blizzard Warning,Winter Storm Warning,Winter Storm Watch,Ice Storm Warning,Ice Storm Watch"

// Fire weather only
"EventTypes": "Red Flag Warning,Fire Weather Watch"
```
