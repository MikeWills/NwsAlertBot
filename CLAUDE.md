# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**NwsAlertBot** is a .NET 10 C# console application (Generic Host / BackgroundService pattern) that polls the NWS REST API for active weather alerts — plus four separate synthetic-alert feeds (SPC Convective Outlooks, SPC Mesoscale Discussions, Hazardous Weather Outlooks, WPC Excessive Rainfall Outlooks) — and distributes them concurrently to social media (Facebook, Instagram, X, Bluesky, Mastodon, Discord, Discord DM, Telegram), push notifications (Pushover), and SMS (Twilio, VoIP.ms).

---

## Build & Run

```powershell
# Build
dotnet build

# Run (uses appsettings.json + optional appsettings.Local.json override)
dotnet run

# Publish self-contained
dotnet publish -c Release -r win-x64 --self-contained

# Smoke-test image attachment against live platform APIs (posts a real test post, then exits
# without starting the polling loop). See ImageSmokeTestService / README.md "Image Smoke Test".
dotnet run -- --smoke-test-image

# Run the unit test suite (NwsAlertBot.Tests) -- pure logic only (parsing, formatting,
# geometry), no live HTTP calls. See "Automated Tests" below.
dotnet test NwsAlertBot.Tests/NwsAlertBot.Tests.csproj
```

**Local configuration**: Create `appsettings.Local.json` alongside `appsettings.json` to override settings without modifying the committed file. It is loaded automatically and is `.gitignore`d. Keep real credentials here, not in `appsettings.json`.

**Runtime state files** written to the working directory:
- `posted_alerts.txt` — deduplication log; persists across restarts. Safe to delete to re-post all active alerts.
- `confirmed_platforms.txt` — tracks which platforms have sent a startup confirmation. Delete to re-run confirmation on next startup.
- `x_post_count.txt`, `twilio_sms_count.txt` — `RateLimitTracker` quota/cost-guard state (window start + count) for `XSettings.MaxPostsPerMonth`/`TwilioSettings.MaxSmsPerDay`. Safe to delete to reset the counter early.

**Automated tests** (`NwsAlertBot.Tests/`, xunit): covers pure logic only — parsing (SPC MCD's
LAT...LON/valid-window/MCD-number regexes), formatting (`NwsAlert.FormatPost`, `PlatformHelpers`),
and geometry (`PolygonGeometry`). Nothing that makes a live HTTP call is tested. Several of the
tested methods (`SpcMcdService.ParseLatLon`/`ParseMcdNumber`/`ParseValidWindow`,
`NwsAlertService.NormalizeNwsText`, `MapService.BuildIemSpsUrl`) are `internal` rather than
`public` — the test project sees them via `InternalsVisibleTo` (`InternalsVisibleTo.cs` at the
repo root), not by widening the public API. When adding tests for a new pure-logic method that's
currently `private`, change it to `internal` (not `public`) and it becomes visible to
`NwsAlertBot.Tests` automatically.

---

## Architecture & Execution Flow

`AlertPollingService` (defined at the bottom of `Program.cs`, registered as a `BackgroundService`) drives the main loop:

1. On first run, calls `StartupConfirmationService.RunAsync()` — sends a one-time test message to every enabled platform not yet in `confirmed_platforms.txt`. Each platform is confirmed independently.
2. Enters the poll loop: calls `SocialMediaOrchestrator.RunAsync()`, then sleeps for `PollIntervalSeconds`.
3. Each `SocialMediaOrchestrator` cycle: fetches active alerts from `NwsAlertService` → filters via `AlertTrackerService.HasBeenPosted()` → posts new alerts to all platforms concurrently via `Task.WhenAll()` → marks each posted alert via `AlertTrackerService.MarkPosted()`.
4. `NwsAlertService.BuildUrl()` pushes all filters (zone/county/state, severity, urgency, certainty, event type) to the NWS API as query parameters — no client-side filtering.
5. `NwsAlert.FormatPost(maxLength)` formats and truncates the alert text for each platform's character limit.
6. After the main NWS loop, the same `RunAsync` cycle also checks `SpcOutlookService`, `SpcMcdService`, `HwoService`, and `WpcEroService` (if each is individually `Enabled`) — separate synthetic-alert feeds with their own severity assignment (not the NWS `Filter*` gate), each self-throttled by its own `CheckIntervalSeconds` in `appsettings.json` rather than the main `PollIntervalSeconds`. Posted through the same per-platform pipeline as NWS alerts, gated by each platform's own `IncludeSpcOutlooks`/`IncludeSpcMcd`/`IncludeHwo`/`IncludeEro` flags.

**DI pattern**: Settings classes are bound once in `Program.cs` and registered as singletons. Every platform service receives its typed `{Platform}Settings` singleton via constructor injection. All platform `HttpClient`s are registered with `AddHttpClient<T>()`.

---

## Non-Negotiable Rules

### 1. Always update documentation

Documentation is split three ways — any change (new feature, new service, new config field,
behavior change, bug fix) **must** include a corresponding update to the relevant file(s), plus a
new bullet at the top of `CHANGELOG.md` describing it:

- **`README.md`** — user-facing setup/running/configuration. New platform → add a setup section
  under [API Credentials](README.md#api-credentials). New NWS filter capability → add it to
  [Alert Filtering](README.md#alert-filtering).
- **`docs/TECHNICAL.md`** — architecture, internals, the full field-by-field Configuration
  Reference, and how each synthetic feed (SPC Outlook/MCD, HWO, ERO) works under the hood. New
  `appsettings.json` field → add it to the Configuration Reference table there, not in
  `README.md` (`README.md` only shows `Location`/`Polling` with recommended presets, not every
  field).
- **`CONTRIBUTING.md`** — dev workflow: NuGet packages, running tests, cutting a release,
  deploying the project's own CI/CD instance.

Renamed or removed anything → update all references across all three files (a straight-text
search for the old name across `README.md`, `docs/TECHNICAL.md`, and `CONTRIBUTING.md` is the
fastest way to catch stale references, since they cross-link each other by section anchor).

If unsure which file a change belongs in, ask: would a normal user configuring/running the bot
need this (`README.md`), or only someone modifying the code / debugging internals
(`docs/TECHNICAL.md`), or only someone building/testing/deploying the project itself
(`CONTRIBUTING.md`)? If genuinely unsure, put it in `CHANGELOG.md` and flag it for a human to
re-file.

### 2. Always update appsettings.json

Any new settings class in `Config/AppSettings.cs` must have a matching block in `appsettings.json` with sensible defaults, placeholder values clearly marked (e.g. `"YOUR_API_TOKEN"`), and `"Enabled": false`.

### 3. No Twilio SDK — use REST directly

Twilio uses the REST API with Basic auth. Do **not** add the `Twilio` NuGet package — reconsidered under Rule #4 and confirmed still correct on its own terms, independent of the Newtonsoft question: the official SDK (`Twilio` on NuGet, v7.14.9) also pulls in `Microsoft.IdentityModel.Tokens` and `System.IdentityModel.Tokens.Jwt` for API-key/JWT auth flows this app doesn't use (it authenticates with Basic auth only). That's a real dependency footprint for zero functional gain over the existing ~50-line REST call.

### 4. Third-party NuGet packages require asking first

The project currently uses only:
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Configuration.Json`
- `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`
- `NetTopologySuite`, `NetTopologySuite.IO.GeoJSON4STJ` (GeoJSON geometry — union/dissolve, point-in-polygon, convex hull, simplification; the STJ IO variant needs no Newtonsoft)
- `Microsoft.Extensions.Http.Resilience` (Polly-based retry/circuit-breaker for read-only weather/mapping HTTP clients — see "HTTP resilience" below)
- `Microsoft.Extensions.Hosting.WindowsServices` (`.UseWindowsService()` — a no-op unless actually running under the Windows SCM; required for `scripts/setup-service.ps1`'s Windows Service mode to work at all, see Common Pitfalls)
- `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` (test-only, in `NwsAlertBot.Tests`, not the main app)
- Built-in `System.Text.Json` — no other JSON library needed so far, but pull in `Newtonsoft.Json`
  (or anything else) if a package genuinely earns its place; it's not a standing ban.

Adding a well-justified third-party package is fine — flag it and explain the tradeoff, but do **not** add one without asking first.

### 5. All new platforms follow the established pattern

Every new platform must:

1. Have a `{Platform}Settings` class in `Config/AppSettings.cs` with `Enabled` bool first, XML doc
   comments on every property, and implementing `IPlatformFilterSettings` (its six members —
   `MinSeverity`, `EventTypes`, `IncludeSpcOutlooks`, `IncludeSpcMcd`, `IncludeHwo`, `IncludeEro` —
   already match every existing platform's property names, so this is a one-line `: IPlatformFilterSettings`).
2. Have a `{Platform}Service.cs` in `Services/` with:
   - Constructor: `HttpClient`, `{Platform}Settings`, `ILogger<{Platform}Service>`
   - `public bool IsEnabled => _settings.Enabled;` and `public IPlatformFilterSettings Filter => _settings;`
   - `public Task<bool> SendConfirmationAsync(string message)`
   - `public Task<bool> PostAlertAsync(NwsAlert alert)` or `SendAlertAsync`
   - Both public methods delegate to one shared private method — no duplicated logic
   - Guard clause: `if (!_settings.Enabled) return false;`
   - Structured logging via `ILogger`; try/catch returning `false` on failure
3. Be registered in `Program.cs` with `services.AddHttpClient<{Platform}Service>()`.
4. Be injected into both `SocialMediaOrchestrator` and `StartupConfirmationService` and added to their respective task lists.
5. Have a settings block in `appsettings.json` (alphabetical within category: "social media" — Bluesky,
   Discord, DiscordDm, Facebook, Instagram, Mastodon, Telegram, X — first, then "notifications" —
   Pushover, Twilio, VoipMs. Chat/bot-posted platforms like Discord and Telegram count as social
   media, not notifications, even though they're not traditional public broadcast platforms.).
6. Have a setup section in `README.md`.

### 6. NWS API filtering is server-side

All filters are query parameters to `api.weather.gov`. Do **not** pull all alerts and filter in C#. See `NwsAlertService.BuildUrl()`.

### 7. Logging conventions

- `ILogger<T>` structured logging throughout; never `Console.WriteLine`
- `LogInformation` — successful operations and normal status
- `LogWarning` — non-fatal issues (disabled platform, missing optional config)
- `LogError` — failures (API errors, exceptions)
- Platform name as first token: `"Facebook: Post failed. Status={Status}"`

### 8. Always work on a branch and open a pull request — never push directly to `master`

Even for a single small fix. Create a feature branch, commit there, push it, and open a PR with
`gh pr create` — then stop and let the human merge it (or explicitly ask before merging). Do not
push straight to `master`, and do not merge a PR yourself unless explicitly told to. `master`'s
branch protection allows this repo's owner to bypass its required "test" status check, but
bypassing it skips CI entirely — a PR is what actually runs `dotnet test` before anything lands.

---

## Key External APIs

| Service | Auth method |
|---|---|
| NWS API | None (User-Agent header required — set in `Program.cs`, do not remove) |
| Bluesky | AT Protocol App Password — `accessJwt` cached; re-authenticates on 401 (do not remove) |
| Discord | Webhook URL — no separate auth header, the token is embedded in the URL itself |
| Discord DM | Bot token — `Authorization: Bot {token}` header (Discord Bot API) |
| Facebook/Instagram | Long-lived Page Access Token |
| Mastodon | Bearer token |
| Telegram | Bot token (Bot API) |
| X (Twitter) | OAuth 1.0a HMAC-SHA1 — signature recalculated per-request in `XService.BuildOAuth1Header()` |
| Pushover | API token + User key (form POST) |
| Twilio | Basic auth (AccountSid:AuthToken) |
| VoIP.ms | `api_username`/`api_password` as query parameters on a GET request (see Common Pitfalls — must be GET, not POST) |
| GitHub Releases API | None (public repo) — `UpdateCheckService` reads `tag_name` from `/repos/{Update.GitHubRepo}/releases/latest` |

---

## NwsAlert Character Limits

`NwsAlert.FormatPost(maxLength)` handles truncation. Use these values:

| Platform | Limit | Notes |
|---|---|---|
| Bluesky | 300 | Hard limit |
| Discord | 4,096 | Embed description limit |
| Discord DM | 4,096 | Embed description limit |
| Facebook | 63,206 | Effectively unlimited |
| Instagram | 2,200 | Caption limit |
| Mastodon | 500 | Default; varies by instance |
| Telegram | 4,096 | 1,024 if a map image caption is attached |
| X | 280 | Hard limit |
| Pushover | 1,024 | Message body limit |
| Twilio | 320 | Keep to 2 SMS segments |
| VoIP.ms | 160 | Stricter than Twilio's 320 — single SMS segment budget |

---

## Common Pitfalls

- **HTTP resilience is scoped to read-only weather/mapping clients only** — `Program.cs` adds `.AddStandardResilienceHandler()` to `NwsAlertService`, `NwsZoneService`, `SpcOutlookService`, `SpcMcdService`, `HwoService`, `WpcEroService`, and the named `"WeatherImagery"`/`"WeatherImageryPrimary"` clients (MapService's IEM pre-flight checks; `SocialMediaOrchestrator`'s map image download). Do **not** add it to `XService` — its OAuth1.0a signature includes a per-request timestamp/nonce, so an automatic retry resending an identical signed request looks like a replay to X's API. Do **not** add it to `BlueskyService` either — it already has its own hand-rolled 401-reauth retry (see below); a generic retry layered on top risks double-retrying auth failures.
- **Instagram requires an image** — text-only posts are not supported. If `ImageUrl` is not set, the service logs a warning and skips.
- **Facebook personal profiles** — Graph API cannot post to personal profiles (deprecated since 2018). Pages only.
- **Facebook Business Manager System User tokens can fail with OAuthException #200** even with correct scopes (`pages_read_engagement`, `pages_manage_posts`) and Full control on the Page — confirmed in production debugging. The fix that worked: derive the Page token from a personal long-lived **User** Access Token (`GET /{page-id}?fields=access_token`) instead of generating one via Business Settings → System Users. See README.md "Facebook Page" setup section for the full gotcha writeup before assuming a config/code bug.
- **Bluesky tokens expire** — `BlueskyService` caches `accessJwt` and re-authenticates on 401. Do not remove this logic.
- **X OAuth 1.0a** — signature must be recalculated per-request (timestamp + nonce). See `XService.BuildOAuth1Header()`.
- **X media upload signing** — `XService.UploadMediaAsync()` reuses `BuildOAuth1Header(method, url)` with no body params signed. This matches X's own (non-strict-OAuth1.0a) behavior for multipart media upload — do not add multipart fields to the signature base, it will break the upload.
- **`scripts/update.ps1` must never overwrite `appsettings.json`/`appsettings.Local.json` or any runtime state file** — it only replaces the executable and itself. It's tempting to simplify the script by extracting the whole downloaded release archive over the install directory, but that archive's `appsettings.json` is a blank template — doing so would silently destroy the user's real configuration. If you touch the "what gets replaced" logic in `update.ps1`, keep this constraint.
- **A Windows Service wrapping this exe needs both `.UseWindowsService()` and a pinned working directory, or it won't start at all.** A bare Generic Host console app doesn't respond to the Windows Service Control Manager's start/stop handshake — Windows kills it almost immediately (error 1053) without `Microsoft.Extensions.Hosting.WindowsServices`' `.UseWindowsService()` in the host builder (harmless no-op everywhere else: interactive runs, systemd). Separately, Windows Services default their working directory to `C:\Windows\System32`, not the executable's folder, and there's no service-creation parameter that overrides this — every relative path in this app (`appsettings.json`, `posted_alerts.txt`, `logs/`, etc.) would resolve against the wrong directory. Fixed by `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` as the very first line in `Program.cs`, before anything touches the filesystem. Both fixes are required together; either alone still leaves the service broken.
- **`Update.ServiceName` in `appsettings.json` is the single source of truth for the service name — don't reintroduce a second place to type it.** Early in this feature's design, `setup-service.ps1 -ServiceName` and `UpdateSettings.ServiceName` were two independently-typed values that merely had to happen to match; a typo in either one would silently break `AutoApply`'s restart-after-update (or restart a different instance's service). Fixed: both `scripts/setup-service.ps1` and `scripts/update.ps1` now default `-ServiceName` to `Resolve-ServiceName` — a small function (intentionally duplicated identically in both scripts, since each is meant to run standalone) that reads `Update.ServiceName` out of `appsettings.Local.json` (checked first) or `appsettings.json` in `-InstallDir`, falling back to `"nwsalertbot"` only if neither sets it. `UpdateCheckService.LaunchUpdater` still passes `-ServiceName` explicitly from the C# side (it already has the value from config binding, so passing it is free and avoids any ambiguity) — but a human only ever types the name in one place: `appsettings.json`. If you touch either script's `-ServiceName` handling, preserve this — don't go back to requiring it as a required/duplicated CLI argument. `scripts/uninstall-service.ps1` breaks this "duplicated for standalone-ness" pattern deliberately: it's a thin wrapper that just forwards `-ServiceName`/`-InstallDir`/`-DryRun` to `setup-service.ps1 -Uninstall` rather than re-implementing `Resolve-ServiceName` and the actual removal logic a third time — it only exists for discoverability (an obviously-named file beats a hidden flag), not to run standalone, so it's fine for it to depend on `setup-service.ps1` being present alongside it.
- **Auto-Update has known unattended-operation gaps, documented in docs/TECHNICAL.md "Known Limitations" (under Auto-Update — Full Reference) — read that before touching `UpdateCheckService`/`update.ps1`/`setup-service.ps1`.** Remaining: the Windows-Service self-stop-during-update path is theoretically safe but has never been exercised against a real service (possible race between SCM failure-recovery and the file swap). See `docs/plans/auto-update-remaining-limitations.md` for how to actually verify this live — don't reinvent it from scratch. Fixed since first written: Linux passwordless-sudo is now opt-in via `setup-service.ps1 -ConfigurePasswordlessSudo` (a scoped `/etc/sudoers.d/` rule, `visudo -c`-validated before install); `AlertPollingService` now logs the running version at startup (closes "no confirmation an update succeeded"); `setup-service.ps1` pre-checks `appsettings.json` exists; `UpdateCheckService` skips the check entirely on an unversioned (`0.0.0`) dev build (`UnversionedDevBuild` constant); `update.ps1` now health-checks the new version after restarting (`Start-BotService`/`Test-BotIsRunning`, `-RollbackCheckDelaySeconds` default 15) and automatically restores `.bak` + restarts if it didn't stay running — closes "no automatic rollback if a bad release is applied"; `release.yml` now publishes a `checksums.txt` (SHA256 per asset) and `update.ps1` verifies the downloaded archive against it before extracting, aborting on a missing/mismatched entry — closes "no integrity check on downloaded releases" (this protects against corrupted uploads/transport tampering only, not a compromised `release.yml`/repo/token — see docs/TECHNICAL.md for the honest scope). The health check is process/service-alive only, not a real app health check (no endpoint exists to call) — it catches "didn't start at all," not "started but is subtly broken." None of the remaining gaps are regressions — they're accepted-for-now, called out explicitly so they don't get rediscovered as surprises.
- **`UpdateCheckService` version comparison depends on `-p:Version=` being injected at publish time** — `release.yml`'s publish step strips the leading `v` from the pushed tag and passes it as `-p:Version=X.Y.Z`; `NwsAlertBot.csproj`'s default `<Version>0.0.0</Version>` only applies to local/dev builds (`deploy.yml` doesn't inject a version either, since it deploys from master, not tags — self-update isn't relevant to that continuously-deployed instance). If `release.yml`'s version injection is ever removed, every future release will compare as version 0.0.0 and `UpdateCheckService` will never detect it as newer.
- **`RateLimitTracker` is hand-rolled, not `System.Threading.RateLimiting`** — `XService`/`TwilioService` use it to guard `MaxPostsPerMonth`/`MaxSmsPerDay`. The BCL's rate limiters (e.g. `FixedWindowRateLimiter`) are in-memory only with no way to reconstruct state from external storage, and this bot redeploys on every push to master (`deploy.yml`) — an in-memory-only counter would reset on every deploy, making a monthly/daily cap nearly meaningless. `RateLimitTracker` persists window-start + count to a small state file instead (`x_post_count.txt`, `twilio_sms_count.txt`). Don't swap it for a BCL limiter without solving the persistence problem first.
- **Pushover priority 2 (emergency) requires `retry` + `expire`** — omitting causes a 400 error. Always include them when priority == 2.
- **Image attachment failure modes differ by platform** — X/Bluesky/Mastodon have a separate upload step; if it fails, they fall back to posting text-only (don't change this to abort the whole post). Facebook (`/photos`) and Twilio (`MediaUrl`) instead pass the image URL directly in the same request as the text — if that URL is bad, the entire post/SMS fails, since there's no separate upload step to fail independently.
- **IEM autoplot URL format** — IEM path-based PNG URLs use `key:value` (colon) separated by `::`, NOT `key=value` (equals). Wrong separators are silently ignored and IEM returns its default demo image (HTTP 200, not a 404). Correct format: `/plotting/auto/plot/208/network:WFO::wfo:MPX::year:2026::phenomenav:XH::significancev:W::etn:1::opt:single::n:auto::_r:t::dpi:100.png`. Note `phenomenav` and `significancev` (with `v` suffix), and `n` for NEXRAD (not `nexrad`).
- **IEM returns demo image for unknown events** — IEM's autoplot returns HTTP 200 with a fixed demo image ("2024 DMX SV.W #1") when the requested VTEC event is not in its database. Always call the VTEC JSON API (`/json/vtec_event.py`) to verify `event_exists: true` before using the autoplot URL. Checking for absence of an "error" key is NOT sufficient — IEM returns no "error" field even for missing events.
- **NWS→IEM phenomena code mismatch** — NWS issues some products under different codes than IEM stores them. Known difference: NWS `HT.W` / `EH.W` → IEM stores as `XH.W` (Extreme Heat Warning, added to IEM March 2025). `MapService.IemPhenomenaAliases` handles this automatically.
- **Untrusted fields embedded in IEM's colon-delimited URLs must be character-class validated, not just length-checked.** The VTEC fields used by `BuildIemUrl`/`ResolveIemPhenomenaAsync` are safe because `NwsAlertService.VtecPattern`'s regex already constrains them (`[A-Z]{4}`/`[A-Z]{2}`/`[A-Z]`/`\d{4}`) before they reach `MapService`. `AfosId`/`WmoIdentifier` (used by `BuildIemSpsUrl`/`VerifyIemSpsAsync`) don't go through an equivalent parser-side regex, so `MapService` validates them itself (`AfosSpsPattern`, `Wmo6Pattern`) before building the URL — do not revert to a length-only check.
