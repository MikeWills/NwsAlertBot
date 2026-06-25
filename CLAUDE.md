# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**NwsAlertBot** is a .NET 8 C# console application (Generic Host / BackgroundService pattern) that polls the NWS REST API for active weather alerts and distributes them concurrently to social media (Facebook, Instagram, X, Bluesky, Mastodon, Discord, Telegram), push notifications (Pushover), and SMS (Twilio, VoIP.ms).

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

Twilio uses the REST API with Basic auth. Do **not** add the `Twilio` NuGet package.

### 4. No external NuGet packages without asking

The project uses only:
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Configuration.Json`
- Built-in `System.Text.Json` (no Newtonsoft)

Do **not** add third-party packages without explicit instruction.

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
5. Have a settings block in `appsettings.json` (alphabetical within category: social media first, then notifications).
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
| Facebook/Instagram | Long-lived Page Access Token |
| X (Twitter) | OAuth 1.0a HMAC-SHA1 — signature recalculated per-request in `XService.BuildOAuth1Header()` |
| Bluesky | AT Protocol App Password — `accessJwt` cached; re-authenticates on 401 (do not remove) |
| Mastodon | Bearer token |
| Pushover | API token + User key (form POST) |
| Twilio | Basic auth (AccountSid:AuthToken) |
| Telegram | Bot token (Bot API) |

---

## NwsAlert Character Limits

`NwsAlert.FormatPost(maxLength)` handles truncation. Use these values:

| Platform | Limit | Notes |
|---|---|---|
| Facebook | 63,206 | Effectively unlimited |
| Instagram | 2,200 | Caption limit |
| X | 280 | Hard limit |
| Bluesky | 300 | Hard limit |
| Mastodon | 500 | Default; varies by instance |
| Pushover | 1,024 | Message body limit |
| Twilio | 320 | Keep to 2 SMS segments |
| Telegram | 4,096 | 1,024 if a map image caption is attached |

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
