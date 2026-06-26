# NWS Event Type Reference

Complete list of National Weather Service event types sorted by severity — lowest to highest.
Source: NWS VTEC Version 9.0 (March 2025) — https://www.weather.gov/media/vtec/VTEC_explanation_ver9.pdf

Use event names exactly as shown in the `EventTypes` config field.

Where a **Severity** cell shows two values (e.g. `Moderate / Severe`), the NWS assigns severity
at issuance time. To reliably catch that event type, include **both** values in your filter.

---

## Minor
*Minimal threat. Advisories, informational statements, and some lower-end watches.*

| Event Name | Type | Notes |
|---|---|---|
| Air Quality Alert | Advisory | Air quality index reaching unhealthy levels |
| Air Stagnation Advisory | Advisory | Poor ventilation conditions |
| Ashfall Advisory | Advisory | Light volcanic ash fall |
| Beach Hazard Statement | Statement | Combination of hazardous beach conditions |
| Blowing Dust Advisory | Advisory | Blowing dust reducing visibility |
| Coastal Flood Advisory | Advisory | Minor coastal flooding — nuisance levels |
| Dense Fog Advisory | Advisory | Visibility reduced to ¼ mile or less |
| Dense Smoke Advisory | Advisory | Visibility reduced to ¼ mile or less by smoke |
| Flood Advisory | Advisory | Minor flooding in low-lying or flood-prone areas |
| Freeze Advisory | Advisory | Sub-freezing temperatures slightly above Freeze Warning threshold |
| Freezing Rain Advisory | Advisory | Light freezing rain or drizzle — slippery surfaces |
| Frost Advisory | Advisory | Near-freezing temperatures during growing season |
| Heat Advisory | Advisory | Heat index 100–104°F (thresholds vary by region) |
| Hydrologic Outlook | Outlook | Long-range flood potential; not an immediate alert |
| Lake Effect Snow Advisory | Advisory | Light to moderate lake-effect snow |
| Lake Wind Advisory | Advisory | Winds causing hazardous conditions on lakes |
| Lakeshore Flood Advisory | Advisory | Minor lakeshore flooding |
| Low Water Advisory | Advisory | Abnormally low water levels on rivers or lakes |
| Marine Weather Statement | Statement | Significant marine weather not meeting advisory criteria |
| Rip Current Statement | Statement | High risk of rip currents at beaches |
| Small Craft Advisory | Advisory | Marine: winds/seas hazardous for small craft |
| Special Weather Statement | Statement | Short-term advisory for conditions below advisory criteria |
| Wind Advisory | Advisory | Sustained winds 25–39 mph or gusts 40–57 mph (inland) |
| Wind Chill Advisory | Advisory | Cold wind chills below Warning threshold |
| Winter Weather Advisory | Advisory | Mix of winter precipitation below warning criteria |

---

## Moderate
*Possible threat. Includes watches for significant events and lower-end warnings.*

| Event Name | Type | Severity | Notes |
|---|---|---|---|
| Coastal Flood Warning | Warning | Moderate / Severe | Significant coastal flooding from storm surge or waves |
| Coastal Flood Watch | Watch | Moderate | Coastal flooding possible |
| Dense Fog Warning | Warning | Moderate | Visibility reduced to ¼ mile or less by fog |
| Excessive Heat Watch | Watch | Moderate | Excessive heat conditions possible |
| Extreme Cold Watch | Watch | Moderate | Dangerously cold conditions possible |
| Fire Weather Watch | Watch | Moderate | Critical fire weather conditions possible |
| Flash Flood Watch | Watch | Moderate | Flash flooding possible |
| Freeze Warning | Warning | Moderate | Temperatures dropping to 32°F or below during growing season |
| Freeze Watch | Watch | Minor / Moderate | Temperatures may drop to 32°F or below during growing season |
| Gale Warning | Warning | Moderate | Sustained winds 34–47 knots (marine) |
| Hazardous Seas Warning | Warning | Moderate | Significant wave heights hazardous for all vessels |
| Heavy Snow Warning | Warning | Moderate / Severe | Heavy snowfall meeting warning threshold (varies by region) |
| High Surf Warning | Warning | Moderate / Severe | Dangerously high breaking waves |
| High Wind Watch | Watch | Moderate | High winds possible |
| High Wind Warning | Warning | Moderate / Severe | Sustained winds ≥ 40 mph or gusts ≥ 58 mph (inland) |
| Ice Storm Watch | Watch | Moderate | Significant ice accumulation possible |
| Lake Effect Snow Warning | Warning | Moderate / Severe | Heavy snow from lake-effect bands |
| Lakeshore Flood Warning | Warning | Moderate | Lakeshore flooding occurring or imminent |
| Blizzard Watch | Watch | Moderate | Blizzard conditions possible |
| Flood Watch | Watch | Minor / Moderate | Flooding possible |
| Flood Warning | Warning | Moderate / Severe | River/stream flooding occurring or imminent |
| Freezing Rain Warning | Warning | Moderate / Severe | Significant freezing rain accumulation |
| Red Flag Warning | Warning | Moderate / Severe | Critical fire weather: low humidity + high winds + dry fuels |
| Severe Thunderstorm Watch | Watch | Moderate / Severe | Conditions favorable for severe thunderstorms — issued by SPC |
| Sleet Warning | Warning | Moderate | Significant sleet accumulation |
| Tropical Storm Watch | Watch | Moderate | Tropical storm conditions possible within 48 hours |
| Wind Chill Warning | Warning | Moderate / Severe | Dangerously cold wind chills |
| Winter Storm Watch | Watch | Minor / Moderate | Significant winter storm possible 24–48 hours out |
| Winter Storm Warning | Warning | Moderate / Severe | Hazardous mix of winter precipitation |

---

## Severe
*Significant threat. Most warnings fall here.*

| Event Name | Type | Severity | Notes |
|---|---|---|---|
| Avalanche Watch | Watch | Severe | Dangerous avalanche conditions possible |
| Blizzard Warning | Warning | Severe | Sustained/frequent winds ≥ 35 mph + considerable snow/blowing snow |
| Debris Flow Warning | Warning | Severe | Significant debris flow (mudslide) occurring or imminent |
| Dust Storm Warning | Warning | Severe | Blowing dust reducing visibility to ¼ mile or less |
| Excessive Heat Warning | Warning | Severe | Heat index ≥ 105°F for 2+ consecutive days |
| Extreme Cold Warning | Warning | Severe | Dangerously cold air temperatures |
| Hurricane Force Wind Warning | Warning | Severe | Sustained winds ≥ 64 knots (marine) |
| Hurricane Watch | Watch | Severe / Extreme | Hurricane conditions possible within 48 hours |
| Ice Storm Warning | Warning | Severe | Significant ice accumulation (typically ≥ 0.25 inch) |
| Snow Squall Warning | Warning | Severe | Brief but intense snow + gusty winds |
| Severe Thunderstorm Warning | Warning | Severe | Winds ≥ 58 mph and/or hail ≥ 1 inch |
| Storm Surge Watch | Watch | Severe / Extreme | Life-threatening storm surge possible within 48 hours (tropical) |
| Storm Warning | Warning | Severe | Sustained winds ≥ 48 knots (marine) |
| Tornado Warning | Warning | Severe / Extreme | Extreme when a Tornado Emergency is declared |
| Tornado Watch | Watch | Severe | Conditions favorable for tornadoes — issued by SPC |
| Tropical Storm Warning | Warning | Severe | Sustained winds 39–73 mph from tropical system within 36 hours |
| Tsunami Watch | Watch | Severe / Extreme | Tsunami possible — monitor for further information |
| Flash Flood Warning | Warning | Severe / Extreme | Extreme when a Flash Flood Emergency is declared |

---

## Extreme
*Extraordinary threat to life or property.*

| Event Name | Type | Notes |
|---|---|---|
| Avalanche Warning | Warning | Life-threatening avalanche conditions |
| Extreme Wind Warning | Warning | Surface winds ≥ 115 mph (non-convective) |
| Hurricane Warning | Warning | Sustained winds ≥ 74 mph from tropical system within 36 hours |
| Storm Surge Warning | Warning | Life-threatening inundation within 36 hours (tropical) |
| Tsunami Warning | Warning | Move to high ground immediately |
| Typhoon Warning | Warning | Pacific equivalent of Hurricane Warning |

---

## Quick Filter Reference

### `Severity` settings

| Goal | Setting |
|---|---|
| Life-threatening emergencies only | `"Extreme"` |
| All warnings — no watches or advisories | `"Severe,Extreme"` |
| Warnings and watches — no advisories | `"Moderate,Severe,Extreme"` |
| Everything including advisories | `""` (empty = all) |

### Example `EventTypes` overrides

```json
// Tornado and severe thunderstorm only
"EventTypes": "Tornado Warning,Tornado Watch,Severe Thunderstorm Warning,Severe Thunderstorm Watch"

// All convective warnings and watches
"EventTypes": "Tornado Warning,Tornado Watch,Severe Thunderstorm Warning,Severe Thunderstorm Watch,Flash Flood Warning,Flash Flood Watch"

// Winter storm alerts only
"EventTypes": "Blizzard Warning,Winter Storm Warning,Winter Storm Watch,Ice Storm Warning,Ice Storm Watch"

// Fire weather only
"EventTypes": "Red Flag Warning,Fire Weather Watch"
```
