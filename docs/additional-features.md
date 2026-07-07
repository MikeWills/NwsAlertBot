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

## Candidates — Both audiences

### Area Forecast Discussion "Key Messages"
- **Agency**: NWS (local WFO — e.g. MPX for the Twin Cities office).
- **Data**: Confirmed live via the same NWS products API already used for HWO/MCD:
  `https://api.weather.gov/products/types/AFD/locations/{wfo}`. The `.KEY MESSAGES...` section
  sits at the top of the AFD product text, between the header and a `&&` delimiter — same
  parse-the-teletype-text approach `HwoService` already uses for its own cleanup.
- **Why both audiences**: this is forecaster-written, plain-language, and pre-summarized —
  unlike ERO/WSSI/Fire Weather, there's no categorical-to-severity mapping to invent, no
  point-in-polygon math, and no probability jargon to translate. It's already written for public
  consumption. Confirmed by example: an MPX AFD Key Messages block read almost verbatim to a
  Slack "Key Messages" post seen in the wild (screenshot, 2026-07-07), which is what prompted this
  entry.
- **Complexity**: Low — closest in shape to `HwoService` (same product-listing/text-fetch API,
  same per-WFO resolution already used there), but notably *simpler* than HWO: the Key Messages
  block is short (2-4 bullet points) instead of HWO's full multi-paragraph 7-day text, so it fits
  short-form platforms (X, Bluesky) without truncation problems the way HWO does today.
- **Caveat**: not every AFD issuance has a `.KEY MESSAGES...` section — WFOs only include it when
  they judge there's a notable weather story worth calling out in plain language. Needs a
  presence check (skip alerting when the section is absent), same as `SpcMcdService.ParseLatLon`
  returning null when a section isn't found.

### Local WFO "Weather Story"
- **Agency**: NWS (local WFO — same office-hosted page family as Key Messages above). Example:
  `https://www.weather.gov/mpx/weatherstory`.
- **Data**: **No JSON/GeoJSON API at all** — confirmed this is a plain HTML page with a fixed
  tabbed-panel template (`.c-tabs-nav__link` / `.c-tab`), each panel holding one `<img>` pointing
  to `https://www.weather.gov/images/{office}/wxstory/Tab{N}FileL.png` plus a short forecaster-
  written caption paragraph embedded directly in the HTML. Confirmed the same template is used
  across multiple offices (checked MPX, DMX, LOT) — this is a general NWS local-page feature, not
  MPX-specific. Panel count varies by office and by how much is currently "in play" (saw 1 panel at
  DMX, 4 at MPX on the same day).
  - **`Tab{N}` is an arbitrary CMS slot ID, not a sequential index** — confirmed on MPX the display
    order was Tab3, Tab4, Tab5, Tab2 (out of order, with no Tab1 present), and per user observation
    other offices can show combinations like Tab3/Tab5/Tab2/Tab6 — gaps and out-of-order numbers
    are normal. **Do not try to construct/guess image URLs from a sequential range** (e.g. "try
    Tab1 through Tab4, stop at the first 404") — the only reliable approach is parsing the actual
    `<img src>` values out of the fetched HTML for that office's page at request time.
  - **Can scraping actually tell which slots are "live" right now, given the image files
    themselves apparently persist and don't change when unused (per user observation)?** Yes —
    but only because the *page HTML itself* is the authoritative signal, not the image files. The
    office's CMS only renders a `<div class="c-tab">` block (nav entry + caption + image tag) for
    slots currently in play — confirmed by DMX showing 1 rendered tab vs MPX's 4 on the same day,
    not some fixed maximum. So a scraper that re-fetches the live HTML on every poll and only acts
    on whatever `Tab{N}` values are *currently referenced in that HTML* is reading the same "what's
    active now" signal the public web page itself relies on. The failure mode is specifically
    trying to shortcut this by probing/caching image URLs directly (e.g. remembering "Tab3 existed
    last time, still poll it") instead of re-parsing the live page each cycle — since old images
    apparently persist at their URLs indefinitely, that approach would have no way to distinguish
    a currently-active panel from a stale leftover one.
  - **Residual risk**: this implies a slot's image file gets overwritten in place at the same URL
    the next time that slot number is reused for different content (not independently confirmed,
    but consistent with everything else observed here). So the bot must always download the image
    fresh at post time — never treat a `Tab{N}FileL.png` URL as stable/cacheable long-term, and
    apply the same cache-busting already used for Twilio MMS on other feeds (`TwilioService.CacheBust`)
    in case a platform/carrier caches the image by URL.
- **Why both audiences**: same appeal as Key Messages — short, plain-language, forecaster-curated
  — but with an eye-catching graphic per panel instead of bullet text, which suits image-first
  platforms (Instagram, Facebook) better than Key Messages does.
- **Complexity**: Medium-High, and a real outlier vs. everything else in this doc:
  - **No stable JSON schema to parse** — this would be HTML scraping (regex/HTML-parsing the tab
    nav titles + `.description` text + image `src`), which is fragile against any NWS template
    change, unlike every other candidate here (all backed by a documented JSON/GeoJSON API).
  - **No issuance timestamp anywhere in the page** — unlike every other feed in this bot (AFD,
    HWO, MCD, ERO, WSSI all carry an explicit issue/valid time used for the dedup `Id`), there's
    nothing here to key deduplication off. Would need a content-hash approach instead (hash the
    panel titles + captions + image URLs together, re-post only when the hash changes) — a new
    dedup pattern not used anywhere else in `AlertTrackerService`.
  - **No severity/category data** — purely narrative. A panel caption sometimes mentions a risk
    category inline as text (e.g. "Slight Risk (2/5)" was seen in one caption during this
    research), but parsing severity out of freeform prose is brittle; more realistic to post these
    at a fixed `Severity` (e.g. `"Unknown"`, same as HWO) rather than trying to extract one.
  - **Per-office, not single-endpoint** — same WFO-resolution need as HWO (`NwsZoneService` already
    resolves the covering WFO(s) for `Location.Zones`/`Location.Counties`), so no new geo-lookup
    code needed there.
- **Recommendation**: valuable content, but worth a deeper look at 3-4 more offices before
  committing to a scraping approach, specifically to confirm the HTML structure is *actually*
  consistent NWS-wide (not just re-skinned per office) and to see what a "nothing going on" state
  looks like (couldn't observe that during this research pass since real weather was active at
  every office checked).

#### The real feed behind this content: NWSChat 2.0 (confirmed, but gated)

The user correctly suspected there's a real feed driving this, not just a manually-updated web
page — confirmed via a screenshot of the `#wfo-twin-cities-mn-datafeed` channel. This is **NWSChat
2.0**, NWS's official real-time coordination platform (migrated from the legacy XMPP-based
NWSChat to a Slack Enterprise Grid deployment on 2023-08-01). Each WFO has one or more channels
(e.g. `wfo-twin-cities-mn`, `wfo-twin-cities-mn-datafeed`), and an automated office bot posts
timestamped graphics there — including "Today's/Tuesday's Severe T-Storm Risk" panels that carry
an explicit `Last Updated` / `Valid Until` timestamp baked into the image, unlike the public
`weatherstory` web page. This is almost certainly the same underlying content (and possibly the
same forecaster tool, internally called "GraphiCast" per a CSS class name seen on the weatherstory
page), just distributed through a different, timestamped channel.

#### Prior art check: "NWS Bot" (nwsbot.xyz)

The user pointed out a third-party Discord bot by Colin Santos (`nwsbot.xyz`) that already has a
working `/weatherstory` command reproducing this exact content (screenshot confirmed: same MPX
"Slight Risk Today" panel, same graphic, same tab-switching UI as buttons). Checked whether it's
open source to see its actual implementation:

- **Not open source** — no GitHub/GitLab link found on the bot's site, changelog, or Discord-bot
  listing pages (top.gg, discord.bots.gg); no license info published anywhere found. Can't inspect
  its actual data pipeline.
- Found one *unrelated* open-source project while searching (`Corpdraco/nwsbot-discord-link`,
  "XMPP (NWSChat) integration with Discord") — different author, different bot, and confirmed dead:
  last commit 2019, targets the legacy XMPP NWSChat that NWS shut off 2023-07-31. Not usable
  against current NWSChat 2.0 and not connected to Colin Santos's bot.
- **Useful signal despite being closed-source**: this bot is public and freely usable by anyone on
  Discord — it does not require the user to be a registered NWS core partner. That's good outside
  evidence that a public, non-NWSChat-gated path to this content exists and is viable at scale
  (whether that's the same `weatherstory` HTML page this doc already proposes scraping, or some
  other public source not yet identified) — it doesn't change the recommended approach here, but it
  does corroborate that the scraping-the-public-page angle isn't a dead end.

**This changes the integration story significantly, and not for the easier:**
- **NWSChat access requires registration as a qualifying NWS core partner** (confirmed categories
  include emergency management and water resources management; other categories such as media may
  also qualify) via `partnerservices.nws.noaa.gov/registration` — it is **not open to the general
  public** the way `api.weather.gov` is. Someone would need to personally register and be approved.
- It's a **Slack workspace**, not an HTTP API — reading it programmatically means building a Slack
  app/bot with OAuth access to that specific workspace and channel, a fundamentally different
  integration shape than every other feed in this bot (all plain unauthenticated HTTPS GETs).
- **Redistribution terms are unclear and worth checking before building anything.** NWSChat has its
  own Terms of Use (linked from partner registration) that a registrant must agree to; I could not
  access the actual terms text in this research pass to confirm whether automated public
  redistribution (e.g. reposting partner-channel content to social media) is permitted. Given this
  is explicitly a core-partner coordination tool, that's worth resolving directly with NWS (or
  reading the ToS after registering) before treating it as a data source, independent of the
  technical work involved.
- **Bottom line**: the public `weatherstory` HTML page (scraping approach above) is likely the only
  path that doesn't require special access — slower/less rich than what core partners see in
  NWSChat, but it's the same underlying content and it's actually public.

#### Prior art check: IEMBot — dead end, don't re-investigate this

The user asked whether IEM's own "iembot" (a separate IEM project that mirrors NWS products into
public chatrooms, no registration required) might be a public backdoor to this content. **Checked
and ruled out — confirmed via IEM's own iembot documentation page:**

- iembot is real and still actively maintained (changelog entry as recent as 2026-03-03), and it
  does run a genuinely public mirror (public XMPP chatrooms, RSS, and JSON feeds, no NWS
  registration needed).
- But its scope is **exclusively classic NWS text products** — scanned its full product-type
  reference (AFD, all warning/watch/advisory types, LSR, climate reports, dozens more) and there is
  no mention anywhere of Weather Story, "GraphiCast," or any image-based product. It mirrors the
  same AFOS/text-bulletin catalog this bot already reads directly from `api.weather.gov` — nothing
  more.
- **Conclusion**: iembot is not a path to the Weather Story graphics or the NWSChat 2.0 Slack
  "WxBot" content. For anything it *does* mirror (e.g. AFD Key Messages), this bot's existing direct
  `api.weather.gov` access is already strictly better (no extra hop, no dependency). **Don't
  re-investigate this angle** — the answer is a documented no, not "not enough time to check."

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
additions** → **AFD Key Messages** (low complexity, best audience fit of anything on this list) →
**SPC Fire Weather Outlook** (closest analog to code that already exists) → **Local Storm Reports**
(new shape, but well-documented feed) → everything else. Let me know which one you want a full
research-and-plan pass on next, same depth as the WSSI doc.
