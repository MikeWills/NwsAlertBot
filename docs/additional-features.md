# Additional NWS/NOAA Products — Feature Ideas

> **Status: options survey, nothing here is planned or implemented yet.** This is a menu to pick
> from, organized by which of the bot's two audiences each product fits best. Items with an
> existing deep-dive get their own plan doc under `docs/plans/`.

## The two audiences

- **Public / social media** (Facebook, Instagram, X, Bluesky, Mastodon) — broad, general-interest,
  wants a simple headline and a picture, doesn't want to be overwhelmed with technical detail or
  low-stakes noise.
- **Private / spotters & chasers** (Discord DM, Telegram, Pushover, personal SMS) — technical
  audience that wants more detail, more lead time, and doesn't mind noise if it's actionable.

## Already covered (so nothing below re-proposes it)

| Feed | Source |
|---|---|
| NWS warnings/watches/advisories | `api.weather.gov/alerts` |
| SPC Convective Outlook (Day 1/2) | `SpcOutlookService` |
| SPC Mesoscale Discussion (MCD) | `SpcMcdService` |
| Hazardous Weather Outlook (HWO) | `HwoService` |
| WPC Excessive Rainfall Outlook (ERO) | `WpcEroService` |
| WPC Winter Storm Severity Index (WSSI) | proposed, see `docs/plans/wssi-monitoring.md` |

---

## Zero-code wins (config only — do these first)

The main NWS alerts feed already carries far more event types than severe weather. `Nws.FilterSeverity`
defaults to `"Severe,Extreme"`, which silently excludes a lot of public-interest event types that
are otherwise fully supported today — no new code, just `Nws.AdditionalEventTypes`. Two confirmed
in `NWS_EVENT_TYPES.md`:

- **Air Quality Alert** — good public-audience fit (health-relevant, not just storm chasers).
- **Rip Current Statement** — good public-audience fit for coastal deployments.

Worth a pass through `NWS_EVENT_TYPES.md` for other advisory-level types (Heat Advisory, Winter
Weather Advisory, Freeze Warning, Dense Fog Advisory, Small Craft Advisory, etc.) that fit a given
deployment's region and audience appetite — same zero-code lever, just add them to
`AdditionalEventTypes` and/or a platform's own `EventTypes` allowlist.

---

## Candidates — Public / social media audience

### NOAA SWPC Geomagnetic Storm & Aurora Alerts
- **Agency**: NOAA Space Weather Prediction Center (a different NOAA line office than NWS — not
  weather.gov/SPC/WPC like everything else in this bot).
- **Data**: JSON/text feeds at `services.swpc.noaa.gov` (e.g. geomagnetic storm scale alerts,
  G1-G5). Confirmed to exist; did not verify exact endpoint shapes in this pass.
- **Why public**: aurora-visibility content reliably outperforms almost everything else on weather
  social accounts. This would be a genuine engagement win, not just a completeness box-check.
- **Complexity**: Low-Medium. No polygon/point-in-geometry needed — it's a national/regional
  Kp-index-driven scale, not tied to your monitored zones. Simplest version: alert nationally on
  G2+ (or whatever threshold), let each deployment's config decide if it's relevant (e.g. a
  far-southern deployment might set a higher G-level floor than a northern one).
- **Note**: this is the one candidate here that isn't a "weather" product at all — worth a gut check
  on whether it fits the bot's identity before building it.

### NHC Tropical Cyclone Products (cone of uncertainty, track, watches/warnings)
- **Agency**: National Hurricane Center.
- **Data**: Confirmed ArcGIS GeoJSON feeds (`mapservices.weather.noaa.gov/tropical/...` and
  `idpgis.ncep.noaa.gov/.../NHC_Atl_trop_cyclones`), same MapServer family already proven out for
  ERO/WSSI. Updates every 6h (3h near landfall).
  - Note: hurricane *watches/warnings* for US coastal counties already arrive through the existing
    NWS alerts feed as regular CAP event types — this candidate is specifically about the *forecast
    cone/track* graphic, which is a distinct GIS product NHC publishes separately.
- **Why public**: high-interest, visual, well understood by a general audience.
- **Complexity**: Medium — same shape as ERO/WSSI (fetch GeoJSON, point-in-polygon against
  monitored area for the cone, or just check if a storm exists in-basin at all for a lighter-touch
  "storm exists" post). Only relevant for coastal/hurricane-prone deployments — dormant otherwise
  (same as ERO in summer), so it's a bot capability question, not specific to this Minnesota
  deployment.

### US Drought Monitor
- **Agency**: NOAA NIDIS / drought.gov (joint with USDA, NDMC).
- **Data**: Weekly GeoJSON/TopoJSON, categorical (D0-D4).
- **Why public**: awareness/informational value, but weekly cadence and slow-changing conditions
  make it a poor fit for an "alert bot" posting cadence — better suited to a scheduled weekly
  summary post than a threshold-triggered alert.
- **Complexity**: Low (same GeoJSON + point-in-polygon pattern as everything else), but **lowest
  priority** here — doesn't fit the bot's alerting model well without a different posting cadence.

---

## Candidates — Private / spotter & chaser audience

### NWS Local Storm Reports (LSR)
- **Agency**: NWS (all WFOs feed into one national product).
- **Data**: Confirmed official ArcGIS feed (`mapservices.weather.noaa.gov/vector/rest/services/obs/nws_local_storm_reports/MapServer`,
  geojson/JSON/PBF, updates every 30 min) and a mirror on IEM.
- **Why spotters/chasers**: this is ground-truth — confirmed hail size, wind damage, tornado
  sightings, flooding reports phoned/relayed in by spotters and the public in real time. Exactly
  the kind of situational-awareness detail this audience wants that the public audience doesn't
  need.
- **Complexity**: Medium, but **different shape than every other feed in this bot** — LSRs are
  individual point reports, not polygons to test monitored centroids against. Two viable matching
  strategies: filter by the monitored area's WFO(s) (already resolved via `NwsZoneService` for
  HWO), or a distance/radius check from monitored centroids. Also a different posting cadence
  question — likely many small reports during active weather, so this would want its own
  `IncludeLsr`-style opt-in (very likely `false` by default, this is not a broad-audience product)
  and probably shouldn't feed the accelerated-polling "storm mode" trigger the way MCDs do (it's a
  symptom of active weather, not an independent trigger).

### SPC Fire Weather Outlook
- **Agency**: Storm Prediction Center (same office as the Convective Outlook already implemented).
- **Data**: Confirmed ArcGIS GeoJSON (`mapservices.weather.noaa.gov/vector/rest/services/fire_weather/SPC_firewx/MapServer`),
  Day 1-8, categorical (`ELEVATED`/`CRITICAL`/`EXTREME`).
- **Why spotters/chasers first, public second**: red-flag/fire-weather conditions matter a lot to
  storm chasers (dry lightning, outflow winds) and to anyone in fire-prone regions, but it's more
  niche for a general social audience than severe convective weather.
- **Complexity**: Low — this is the closest analog to `SpcOutlookService` of anything on this list;
  it's the *same office*, same GeoJSON shape, just a 3-tier category instead of 6-tier. Easiest
  build here if you want a quick win.

### SPC Mesoscale Analysis (CAPE/shear/sounding parameter maps)
- **Agency**: Storm Prediction Center.
- **Data**: Static reference images (CAPE, shear, etc.), not a categorical alert feed.
- **Why spotters/chasers**: this isn't really "a new feed" — it's an **enrichment** idea. When an
  MCD, Tornado Watch, or Severe Thunderstorm Watch already posts, attach a live regional CAPE/shear
  snapshot alongside it for extra situational context, the way MCD posts already attach SPC's own
  MCD graphic.
- **Complexity**: Low as an attachment to an existing post; doesn't fit the "poll → threshold →
  alert" model as its own feed, since there's no categorical threshold to cross — it's always
  "current conditions," not a forecast.

### Richer SPC Watch details (tornado/wind/hail probabilities, PDS flag)
- Tornado Watches and Severe Thunderstorm Watches already arrive today via the main NWS alerts
  feed (they're regular CAP event types). What's missing is the extra detail SPC's own watch
  product carries — the tornado/wind/hail probability breakdown and the "particularly dangerous
  situation" (PDS) designation, similar in spirit to how MCDs already bundle that kind of detail.
- **Why spotters/chasers**: a PDS tag or high tornado-probability watch is meaningfully different
  from a routine one, and this audience cares about that distinction more than the public does.
- **Complexity**: Medium — would need to correlate the CAP alert with SPC's own watch text product
  (not yet confirmed which endpoint carries this cleanly; would need the same kind of live-data
  verification pass done for MCD/ERO/WSSI before committing to an approach).

---

## Suggested next step

If you want to keep moving, cheapest-to-priciest in roughly this order: **zero-code event type
additions** → **SPC Fire Weather Outlook** (closest analog to code that already exists) →
**Local Storm Reports** (new shape, but well-documented feed) → everything else. Let me know which
one you want a full research-and-plan pass on next, same depth as the WSSI doc.
