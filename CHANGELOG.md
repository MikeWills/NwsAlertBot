# Changelog

Notable changes to NwsAlertBot, most recent first. For setup and usage, see
[README.md](README.md); for architecture and internals, see [docs/TECHNICAL.md](docs/TECHNICAL.md).

- **Fix: `Map.Enabled: false` didn't actually stop Mapbox fallback calls.** `MapService.GetMapUrlAsync`
  (the primary path) correctly checked `Enabled` first, but `GetMapboxFallbackUrlAsync` — called by
  `SocialMediaOrchestrator.DownloadMapImageAsync` whenever the primary image is unavailable — only
  checked whether `AccessToken` was non-empty. Since the shipped template's placeholder token
  (`"YOUR_MAPBOX_ACCESS_TOKEN"`) is non-empty, every alert fired a doomed HTTP call to Mapbox and
  logged a 401 warning even with Map fully disabled. Harmless functionally (falls back to
  text-only either way) but pure log noise with the feature off. Confirmed live: before the fix,
  every alert logged "trying Mapbox fallback" + a 401; after, it goes straight to "Image
  unavailable" with no Mapbox call at all.
- **Add `scripts/uninstall-service.ps1`.** A thin, discoverably-named wrapper around
  `setup-service.ps1 -Uninstall` — stops and removes the systemd unit / Windows Service
  registration without touching `appsettings.json`, credentials, or any runtime state file.
  `-Uninstall` already did this, but it required knowing the flag existed; this gives it its own
  obvious file, bundled in every release archive and refreshed on self-update alongside
  `update.ps1`/`setup-service.ps1`.
- **Fix: a long-running Watch's cancellation could be silently missed.** NWS only keeps a
  cancellation message in its active-alerts feed for a few minutes after issuance (confirmed as
  short as ~16 minutes on a real cancel) — if the bot had already dropped back to idle polling by
  then, the cancellation was gone before the next poll and unrecoverable. This happened whenever a
  Watch ran longer than `ActiveAlertWindowHours` (e.g. an 8h Severe Thunderstorm Watch vs. the 4h
  default), since only the Watch's *issuance* was Severe enough to trigger accelerated polling —
  the eventual cancellation is always downgraded to `severity: Minor` by NWS and couldn't
  re-trigger it. Fixed: a storm-triggering alert whose event name contains "Watch" now also sets
  an independent expiry-based window (`SocialMediaOrchestrator.RunAsync` returns the Watch's own
  `ends`/`expires` time; `AlertPollingService` keeps accelerated polling going until that time,
  regardless of `ActiveAlertWindowHours`). Warnings and every other event type are unaffected —
  they still use the fixed window as before.
- **Docs: trimmed developer/implementation rationale out of README.md.** Several spots explained
  *why* the code works a certain way (rate-limit counters persisting because of redeploy
  frequency, cancellations bypassing filters via a separate query, a Program.cs-only log-retention
  setting) — README.md now states only the user-facing behavior; the implementation reasoning
  moved to docs/TECHNICAL.md (Architecture, and the `Nws` config section) so it isn't lost, just
  relocated to the doc aimed at people modifying the code. Also fixed a couple of pre-existing
  typos in the Setup steps.
- **Change: `scripts/update.ps1 -Tag` is now optional, defaulting to the latest release.** Running
  `./update.ps1` with no arguments resolves the latest tag via GitHub's `releases/latest` API
  (the same lookup `UpdateCheckService` already does) instead of requiring you to check the
  releases page and type the tag yourself. `-Tag v1.2.3` still works to pin a specific version.
- **Refactor: extracted the multi-recipient fan-out pattern into `PlatformHelpers.FanOutAsync`.**
  `DiscordService`, `DiscordDmService`, `TwilioService`, and `VoipMsService` each repeated the same
  `Task.WhenAll(...).Select(...)` + `.All(r => r)` pair after their own (differing) recipient-list
  guard clauses. No behavior change — pure extraction, the second deferred item from the
  duplication-cleanup review.
- **Fix: `update.ps1` now automatically rolls back a bad release.** After restarting, it waits
  `-RollbackCheckDelaySeconds` (default 15s) and checks whether the new version is still
  running/active (`Start-BotService` reports how it was started — systemd, Windows Service, or a
  direct process; `Test-BotIsRunning` checks accordingly). If not, the previous executable is
  automatically restored from its `.bak` backup and restarted, instead of leaving the bot down
  indefinitely with no recovery. Lightweight by design — process/service-alive only, not a real
  app health check, since there's no health endpoint to call — but it catches the most common
  failure mode (a release that doesn't start at all). Verified end-to-end against real PE
  executables standing in for a healthy vs. crashing release (a tiny purpose-built
  `Thread.Sleep`-only console app for "stays running," `hostname.exe` for "exits immediately"),
  since neither an interactive shell nor a GUI app reliably stays running in a sandboxed/headless
  test environment. Closes one of the three remaining Auto-Update "Known Limitations."
- **Fix: four of the Auto-Update / Running as a Service "Known Limitations" from the previous
  entry.** `AlertPollingService` now logs the running version at startup, closing "no confirmation
  an update succeeded" — after `AutoApply` restarts the bot, the new version shows up in logs.
  `setup-service.ps1` now errors out upfront if `appsettings.json` is missing from `-InstallDir`,
  instead of creating a service that immediately crash-loops. `UpdateCheckService` now skips the
  update check entirely on an unversioned (`0.0.0`) dev build, rather than treating every GitHub
  release as newer and silently overwriting the dev build on first check. New
  `setup-service.ps1 -ConfigurePasswordlessSudo` (Linux, opt-in, off by default) writes a
  narrowly-scoped `/etc/sudoers.d/` rule granting only `systemctl restart <ServiceName>` —
  validated with `visudo -c` before being installed — so `AutoApply`'s restart step doesn't
  silently fail/hang without a password. Remaining known limitations (untested Windows-Service
  self-stop race, no rollback on a bad release, no release integrity check) are unchanged.
- **Add: `.github/workflows/ci.yml` to run the test suite on every pull request.** Previously
  tests only ran in `deploy.yml`, on push to master — meaning a broken PR could merge with no
  automated signal until it had already landed and `deploy.yml` failed afterward, discovered only
  by whoever noticed the deploy failure. This new workflow runs independently on `pull_request`
  (and `push` to master, for a clean "tests pass" signal separate from `deploy.yml`'s deployment
  concerns) so every PR gets a dedicated, fast check before merge. Pairs with a GitHub branch
  protection rule requiring it, to actually block a bad merge rather than just flag one.
- **Add: `scripts/setup-service.ps1` to install NwsAlertBot as a background service.** Creates a
  systemd unit (Linux) or Windows Service (Windows), pointed at the executable with its working
  directory pinned correctly, set to start on boot and restart on failure. Running more than one
  instance on the same machine (e.g. one bot per Discord server) needs a distinct service name
  per instance — set once via `Update.ServiceName` (default `"nwsalertbot"`) in that instance's
  `appsettings.json`; both `setup-service.ps1` and `update.ps1` read it from there automatically
  (a `-ServiceName` argument on either still overrides it), so there's a single place to set the
  name instead of two independently-typed values that merely have to happen to match. Required
  two supporting fixes for Windows Service mode to
  work at all: added `Microsoft.Extensions.Hosting.WindowsServices` + `.UseWindowsService()` (a
  no-op everywhere else) since a bare console app doesn't respond to the Windows Service Control
  Manager's start/stop handshake and gets killed almost immediately otherwise; and pinned the
  process's working directory to the executable's own folder at startup, since Windows Services
  otherwise default to `C:\Windows\System32` and every relative path in this app
  (`appsettings.json`, state files, `logs/`) would resolve against the wrong directory. `-DryRun`
  mode (works without elevation) reports what would happen without creating/removing anything.
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
  release before landing (see docs/TECHNICAL.md "Auto-Update").
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
  against WPC's live GeoJSON feed before implementing (see docs/TECHNICAL.md "WPC Excessive
  Rainfall Outlook (ERO)").
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
  excludes `Minor` — the default, and every recommended config in the README except "everything"
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
  new top-level `Polling` section — see docs/TECHNICAL.md's Configuration Reference for the exact
  shape. `LocalConfigSync` does not auto-migrate this rename (it only adds missing keys within
  sections that already match by name), so old-style configs will silently fall back to defaults
  for these fields until moved manually.
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
- Added an Image Smoke Test dev tool (`ImageSmokeTestService`, run via
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
  the Map Images platform behavior table in the README.
- Removed ntfy support (`NtfyService`, `Ntfy` settings block) — Telegram replaces it as the
  free/self-hostable push notification option.
- Added Telegram support (`TelegramService`, `Telegram` settings block) — posts alerts to a
  Telegram chat or channel via the Bot API. Sends `sendPhoto` with a caption when a Mapbox map
  image is available, otherwise a plain `sendMessage`. See the Telegram setup section and the
  confirmation/map-image behavior tables in the README.
- Added SPC Convective Outlook Monitoring — alerts on Day 1/Day 2 categorical risk (Thunderstorm
  or higher) plus tornado/wind/hail probability for monitored zones/counties, posted through the
  existing platform pipeline. New `Spc` settings block. `MapService`'s zone/county geometry fetch
  was extracted into a shared `NwsZoneService` (used by both the map bounding-box fallback and the
  new SPC location resolution).
