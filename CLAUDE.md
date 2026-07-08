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
# without starting the polling loop). See ImageSmokeTestService / README "Image Smoke Test".
dotnet run -- --smoke-test-image

# There are no automated tests in this project.
```

**Local configuration**: Create `appsettings.Local.json` alongside `appsettings.json` to override settings without modifying the committed file. It is loaded automatically and is `.gitignore`d. Keep real credentials here, not in `appsettings.json`.

**Runtime state files** written to the working directory:
- `posted_alerts.txt` — deduplication log; persists across restarts. Safe to delete to re-post all active alerts.
- `confirmed_platforms.txt` — tracks which platforms have sent a startup confirmation. Delete to re-run confirmation on next startup.

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

### 1. Always update README.md

Any change — new feature, new service, new config field, behavior change, bug fix — **must** include a corresponding README.md update.

- New platform → add a setup section under the appropriate heading
- New `appsettings.json` field → add it to the Configuration Reference table
- New NWS filter capability → add it to the Alert Filtering section
- Renamed or removed anything → update all references

If unsure which section to update, add a note under `## Recent Changes` at the bottom.

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
- Built-in `System.Text.Json` — no other JSON library needed so far, but pull in `Newtonsoft.Json`
  (or anything else) if a package genuinely earns its place; it's not a standing ban.

Adding a well-justified third-party package is fine — flag it and explain the tradeoff, but do **not** add one without asking first.

### 5. All new platforms follow the established pattern

Every new platform must:

1. Have a `{Platform}Settings` class in `Config/AppSettings.cs` with `Enabled` bool first and XML doc comments on every property.
2. Have a `{Platform}Service.cs` in `Services/` with:
   - Constructor: `HttpClient`, `{Platform}Settings`, `ILogger<{Platform}Service>`
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

- **Instagram requires an image** — text-only posts are not supported. If `ImageUrl` is not set, the service logs a warning and skips.
- **Facebook personal profiles** — Graph API cannot post to personal profiles (deprecated since 2018). Pages only.
- **Facebook Business Manager System User tokens can fail with OAuthException #200** even with correct scopes (`pages_read_engagement`, `pages_manage_posts`) and Full control on the Page — confirmed in production debugging. The fix that worked: derive the Page token from a personal long-lived **User** Access Token (`GET /{page-id}?fields=access_token`) instead of generating one via Business Settings → System Users. See README "Facebook Page" setup section for the full gotcha writeup before assuming a config/code bug.
- **Bluesky tokens expire** — `BlueskyService` caches `accessJwt` and re-authenticates on 401. Do not remove this logic.
- **X OAuth 1.0a** — signature must be recalculated per-request (timestamp + nonce). See `XService.BuildOAuth1Header()`.
- **X media upload signing** — `XService.UploadMediaAsync()` reuses `BuildOAuth1Header(method, url)` with no body params signed. This matches X's own (non-strict-OAuth1.0a) behavior for multipart media upload — do not add multipart fields to the signature base, it will break the upload.
- **Pushover priority 2 (emergency) requires `retry` + `expire`** — omitting causes a 400 error. Always include them when priority == 2.
- **Image attachment failure modes differ by platform** — X/Bluesky/Mastodon have a separate upload step; if it fails, they fall back to posting text-only (don't change this to abort the whole post). Facebook (`/photos`) and Twilio (`MediaUrl`) instead pass the image URL directly in the same request as the text — if that URL is bad, the entire post/SMS fails, since there's no separate upload step to fail independently.
- **IEM autoplot URL format** — IEM path-based PNG URLs use `key:value` (colon) separated by `::`, NOT `key=value` (equals). Wrong separators are silently ignored and IEM returns its default demo image (HTTP 200, not a 404). Correct format: `/plotting/auto/plot/208/network:WFO::wfo:MPX::year:2026::phenomenav:XH::significancev:W::etn:1::opt:single::n:auto::_r:t::dpi:100.png`. Note `phenomenav` and `significancev` (with `v` suffix), and `n` for NEXRAD (not `nexrad`).
- **IEM returns demo image for unknown events** — IEM's autoplot returns HTTP 200 with a fixed demo image ("2024 DMX SV.W #1") when the requested VTEC event is not in its database. Always call the VTEC JSON API (`/json/vtec_event.py`) to verify `event_exists: true` before using the autoplot URL. Checking for absence of an "error" key is NOT sufficient — IEM returns no "error" field even for missing events.
- **NWS→IEM phenomena code mismatch** — NWS issues some products under different codes than IEM stores them. Known difference: NWS `HT.W` / `EH.W` → IEM stores as `XH.W` (Extreme Heat Warning, added to IEM March 2025). `MapService.IemPhenomenaAliases` handles this automatically.
- **Untrusted fields embedded in IEM's colon-delimited URLs must be character-class validated, not just length-checked.** The VTEC fields used by `BuildIemUrl`/`ResolveIemPhenomenaAsync` are safe because `NwsAlertService.VtecPattern`'s regex already constrains them (`[A-Z]{4}`/`[A-Z]{2}`/`[A-Z]`/`\d{4}`) before they reach `MapService`. `AfosId`/`WmoIdentifier` (used by `BuildIemSpsUrl`/`VerifyIemSpsAsync`) don't go through an equivalent parser-side regex, so `MapService` validates them itself (`AfosSpsPattern`, `Wmo6Pattern`) before building the URL — do not revert to a length-only check.
