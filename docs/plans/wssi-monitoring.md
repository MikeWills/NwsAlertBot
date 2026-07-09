# Add WPC Winter Storm Severity Index (WSSI) Monitoring

> **Status: proposed, not yet implemented.** This is a design doc for review — see CLAUDE.md's
> "Non-Negotiable Rules" for what a real implementation must also update (README.md,
> docs/TECHNICAL.md, appsettings.json, CHANGELOG.md) once this is approved.

## Context

The user asked whether WPC's Winter Weather Desk (`https://www.wpc.ncep.noaa.gov/wwd/winter_wx.shtml`)
could be added as a new alert feed, but was unsure how much detail it should carry. That page
actually bundles several distinct WPC products with very different data-feed quality:

- **Snowfall/ice accumulation probability forecasts** (`≥4"/8"/12" snow`, `≥0.25" ice`) — these are
  **shapefile-only** (`https://ftp.wpc.ncep.noaa.gov/shapefiles/ww/day{1,2,3}/*.tar`). No GeoJSON,
  no ArcGIS REST layer. Parsing them would mean either writing a `.shp`/`.dbf` binary reader from
  scratch or asking to add a shapefile-reading NuGet package (the project has no GIS library today —
  see CLAUDE.md Rule #4) — a much bigger lift than every other feed in this bot, for a product that's
  arguably redundant with WSSI's own Snow Amount/Ice Accumulation components below.
  **Recommendation: skip these entirely.**
- **Winter Storm Severity Index (WSSI)** — a categorical, impact-based product (Minor/Moderate/
  Major/Extreme + a "Winter Weather Area" placeholder tier) for Days 1-3, **served as a clean ArcGIS
  REST FeatureLayer that supports `f=geojson`** — same shape of integration as `SpcOutlookService`/
  `WpcEroService` already use, and confirmed live during research (see Data Source below).
  **Recommendation: build this one.**

This plan covers WSSI only. The shapefile-only snowfall/ice products are out of scope.

## Research Findings (verified live, 2026-07-07)

- **Source**: `https://mapservices.weather.noaa.gov/vector/rest/services/outlooks/wpc_wssi/MapServer`
  — an ArcGIS REST MapServer, same family as the one already confirmed working for ERO
  (`hazards/wpc_precip_hazards/MapServer`).
- **Layers used**: `Overall_Impact_Day_1` (id 1), `Overall_Impact_Day_2` (id 2),
  `Overall_Impact_Day_3` (id 3). (Layer 4, `Overall_Impact_Days_1-3`, is a redundant combined
  window — skip it, same reasoning as why SPC Outlook uses separate Day 1/Day 2 feeds rather than
  a combined one.)
- **Query**: `GET {MapServer}/{layerId}/query?where=1=1&outFields=*&f=geojson` returns a standard
  `FeatureCollection` — confirmed live, returns real features with proper `Polygon`/`MultiPolygon`
  geometry.
- **Feature properties** (confirmed via live query): `impact` (string category, values below),
  `issue_time` (e.g. `"2026-07-07 1015Z"`), `start_time`/`end_time` (e.g. `"10Z 07/07/26"`,
  `"06Z 07/10/26"` — same `HHZ MM/DD/YY` convention as ERO's `VALID_TIME`), `component`
  (`"WSSI_OVERALL"` for these three layers), `product`.
- **Category values** (confirmed via the layer's renderer `uniqueValueInfos`, exact strings):
  `"WINTER WEATHER AREA"`, `"MINOR"`, `"MODERATE"`, `"MAJOR"`, `"EXTREME"`.
- **Overall Impact methodology** (confirmed via WSSI literature/user guide search): Overall Impact
  is **the maximum of the four component indices** (Snow Amount, Snow Load, Ice Accumulation,
  Blowing Snow) — it is not a separate independent metric. This resolves the "how much to present"
  question directly: fetching Overall Impact alone already reflects the worst-case driver: you are
  not missing signal by skipping the 4 component layers, only the detail of *which* hazard is
  driving the number. Component layers exist (ids 5-27) if we ever want that detail later, but are
  out of scope for this plan (5x the HTTP calls per cycle — 15 vs 3 — and longer post bodies, for
  marginal added value).
- **Update cadence**: WSSI updates every 2 hours (per the service's own `serviceDescription`).
- **No pre-rendered map image exists** (unlike SPC MCD/Outlook/ERO). The WSSI web page is an
  interactive Esri JS map, not a static PNG. The MapServer's `export` operation *can* generate a
  PNG server-side (confirmed working via a live test), but this MapServer is data-only — no
  basemap/state-lines layer — so an export would render as an unlabeled colored blob with no
  geographic context.
  **Checked IEM as an alternative** (it supplies the WFO-cropped categorical map image used for
  both SPC Outlook and ERO, via autoplot #220): confirmed IEM has **no WSSI or winter-forecast
  equivalent**. Autoplot #220 explicitly only supports Convective/Fire Weather/Excessive Rainfall
  (`which: {day}C/{day}F/{day}E` — no winter/snow/ice option), and a full scan of IEM's autoplot
  index turned up only climatological station plots (snow depth history, season snow coverage,
  etc.) — nothing that renders WPC's forecast products the way #220 does. So unlike ERO, there is
  no "reuse IEM's rendering" shortcut available for WSSI.
  Decision: skip building a custom image; rely on the existing
  `MapService.GetMapboxFallbackUrlAsync()` fallback path, which already renders a legible basemap
  of the configured monitored area whenever a synthetic alert has no `MapImageUrl` — zero new code.
- **Details link**: no per-day static HTML page like ERO's `ero.php?day=N`. Use the single
  interactive page URL for all three days: `https://www.wpc.ncep.noaa.gov/wwd/wssi/wssi.php`.

## Decisions (discussed with user 2026-07-07)

1. **Detail level**: Overall Impact only (Day 1/2/3), no component breakdown. Justified above —
   Overall Impact is already the max of the components, so nothing is lost.
2. **Minimum tier**: alert on all 5 tiers, including `"WINTER WEATHER AREA"` — do not skip it.
   To keep it filterable despite WPC's own description of that tier as "not anticipated to impact
   daily life," map it to `Severity: "Unknown"` (same pattern HWO already uses for its own
   no-severity-concept product) rather than `"Minor"`. This means by default (empty `MinSeverity`)
   every platform still receives it, exactly as requested, but any platform that wants to exclude
   the noise floor can do so via its existing `MinSeverity` filter (e.g. `"Minor,Moderate,Severe,
   Extreme"` would exclude `"Unknown"` while keeping everything else) — no new filter field needed.
3. **Map image**: no custom image generation. Let the existing Mapbox-fallback path in
   `SocialMediaOrchestrator.DownloadMapImageAsync` handle it exactly as it already does for
   SPC Outlook/MCD when their primary image source is unavailable.

### Severity mapping

| WSSI `impact` value | Mapped Severity |
|---|---|
| `EXTREME` | Extreme |
| `MAJOR` | Severe |
| `MODERATE` | Moderate |
| `MINOR` | Minor |
| `WINTER WEATHER AREA` | Unknown |

## Implementation (mirrors the ERO feature added earlier this session)

This follows the exact pattern already established by `WpcEroService`/`SpcOutlookService`, so most
of this is repeating a known-good shape rather than inventing a new one.

### New files
- **`Services/WpcWssiService.cs`** — new service, closely mirrors `WpcEroService.cs`:
  - `EnsureLocationsResolvedAsync()`: resolves `Location.Zones`/`Location.Counties` centroids via
    `NwsZoneService` + `PolygonGeometry.ComputeCentroid` (same as ERO/Outlook). Since no per-WFO
    map image is generated here, this can return just `(Code, Lat, Lon)` — simpler than ERO's tuple,
    which also carries `Wfo`/`State` only for its IEM image URL.
  - `GetWssiAlertsAsync()`: for `day` in `{1, 2, 3}`, fetch
    `{MapServer}/{day}/query?where=1=1&outFields=*&f=geojson` (layer ids 1/2/3 = day 1/2/3), find
    the highest-ranked `impact` value whose polygon contains any monitored centroid (reuse
    `PolygonGeometry.PointInGeometry` — **no new geometry code needed**, this is exactly why that
    method was extracted to a shared location during the ERO work).
  - Category rank lookup: `["WINTER WEATHER AREA", "MINOR", "MODERATE", "MAJOR", "EXTREME"]`
    (index = rank, mirrors `WpcEroService.CategoryNames`).
  - New small timestamp parser for `issue_time`/`start_time`/`end_time`'s `"HHZ MM/DD/YY"` /
    `"yyyy-MM-dd HHmmZ"` shapes — distinct enough from ERO's `"yyyy-MM-dd HH:mm:ss"` and MCD's
    `"DDHHMM"` that it needs its own small regex, following the same per-service custom-parser
    precedent already set by `SpcMcdService.ParseValidWindow`/`WpcEroService.ParseTimestamp`.
  - `BuildAlert()`: sets `Event = "WPC Day {day} Winter Storm Severity Index"`,
    `Headline = "{Category} Impact — Day {day} Winter Storm Severity Index"`,
    `DetailsUrl = "https://www.wpc.ncep.noaa.gov/wwd/wssi/wssi.php"`, `Instruction` = a short
    one/two-liner (impact description + details link), `IsWssi = true`, **no `MapImageUrl`**,
    `Severity` per the table above. Dedup `Id = "WPC-WSSI-Day{day}-{issueStamp}"`.

### Modified files (same touch points as the ERO feature)
- **`Config/AppSettings.cs`**: new `WssiSettings` class (`Enabled` default `false`,
  `CheckIntervalSeconds` default `7200` — matches WSSI's own 2-hour update cadence, longer than
  ERO's 1800s default since checking more often than the source updates has no benefit). Add
  `IncludeWssi` (default `true`) to all 11 platform settings classes, same spot as `IncludeEro`.
- **`Models/NwsAlert.cs`**: add `IsWssi` bool, same shape as `IsEro`.
- **`Program.cs`**: bind `WssiSettings` from `"Wssi"` config section, register as singleton, add
  `services.AddHttpClient<WpcWssiService>(...)` with the same User-Agent/Accept headers as the
  other NWS/WPC clients.
- **`Services/SocialMediaOrchestrator.cs`**: inject `WpcWssiService`, add `CheckWssiAsync()`
  (mirrors `CheckEroAsync`, calls `DownloadMapImageAsync` so the Mapbox fallback gets a chance to
  run), call it from `RunAsync()`, extend the platform tuple in `PostToAllPlatformsAsync` with an
  `IncludeWssi` column and the corresponding `alert.IsWssi && !p.IncludeWssi` filter clause (same
  edit shape as the `IncludeEro` change made earlier).
- **11 platform `Service.cs` files** (`FacebookService`, `InstagramService`, `XService`,
  `BlueskyService`, `MastodonService`, `PushoverService`, `TwilioService`, `DiscordService`,
  `DiscordDmService`, `TelegramService`, `VoipMsService`): add
  `public bool IncludeWssi => _settings.IncludeWssi;` alongside the existing `IncludeEro` line.
- **`appsettings.json`**: new `"Wssi": { "Enabled": false, "CheckIntervalSeconds": 7200 }` block
  next to `"Ero"`, plus `"IncludeWssi": true` added to all 11 platform blocks. Update the shared
  header comments (Location/Polling/SPC-feeds sections) to mention `Wssi` alongside `Ero`, same as
  was done for the ERO feature.
- **`README.md`**: new row in the "Additional Alert Feeds" table (feed name, one-line description,
  `Enabled`/`CheckIntervalSeconds` snippet, link to its docs/TECHNICAL.md section) — same pattern
  as the existing SPC Outlook/MCD/HWO/ERO rows.
- **`docs/TECHNICAL.md`**: new "WPC Winter Storm Severity Index (WSSI) — How It Works" section
  (How it works / severity table / per-platform filtering), plus Configuration Reference entries
  (`Wssi` block + `IncludeWssi` row) alongside the existing `Ero` ones. Explicitly note in the doc
  that the shapefile-only snowfall/ice probability products from the same Winter Weather Desk page
  were evaluated and intentionally left out (so a future session doesn't re-litigate that
  research).
- **`CHANGELOG.md`**: new entry at the top describing the feature.

### Not in scope for this plan
- The shapefile-only snowfall/ice accumulation probability products (no GeoJSON/REST feed exists).
- WSSI's 4 component layers (Snow Amount/Snow Load/Ice Accumulation/Blowing Snow) — Overall Impact
  already subsumes them.
- Any custom-rendered map image for WSSI.

## Verification (once implemented)

- `dotnet build` — must succeed with 0 warnings/errors, same bar as the ERO work.
- Live data check against the user's real configured Minnesota zones (same technique used to
  validate ERO): fetch the three `Overall_Impact_Day_{1,2,3}` GeoJSON layers, run the real
  `PointInGeometry` logic against the real zone centroids, and confirm the highest-ranked matching
  `impact` value and resulting `Severity` look sane for current conditions.
- Confirm `LocalConfigSync` picks up the new `Wssi` section/`IncludeWssi` keys into
  `appsettings.Local.json` on next run (disabled by default) without manual edits.
