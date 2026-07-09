# Deferred Duplication Cleanup

> **Status: both items implemented (see below).** Two items from a full-codebase duplication
> review were deliberately deferred — lower value or higher risk than the items already fixed in
> `Services/PlatformHelpers.cs` (`TruncateWithEllipsis`, `CacheBust`, `BuildSmsText`,
> `DiscordSeverityColor`). This doc exists so the history isn't lost.

## 1. Multi-recipient fan-out + aggregate pattern

> **Status: implemented (2026-07-09).** Added `PlatformHelpers.FanOutAsync<T>(IEnumerable<T>
> recipients, Func<T, Task<bool>> sendOne)` exactly as sketched below. Each service's own
> guard-clause validation was left untouched (that's what made this a real design decision rather
> than copy-paste) — only the 2-line `Task.WhenAll`/`.All()` pattern after it was replaced with a
> single `return await PlatformHelpers.FanOutAsync(...)` call, in `DiscordService.SendAsync`,
> `DiscordDmService.SendToAllUsersAsync`, `TwilioService.SendToAllAsync`, and
> `VoipMsService.SendToAllAsync`.

**Where** (identical 2-line pattern, confirmed current as of this doc):
- `Services/DiscordService.cs:68-69` — `SendAsync`, fans out to `WebhookUrls`
- `Services/DiscordDmService.cs:78-79` — `SendToAllUsersAsync`, fans out to `UserIds`
- `Services/TwilioService.cs:65-66` — `SendToAllAsync`, fans out to `ToNumbers`
- `Services/VoipMsService.cs:82-83` — `SendToAllAsync`, fans out to `ToNumbers`

Each does:
```csharp
var tasks = <recipients>.Select(r => <SendOneAsync>(r, ...));
var results = await Task.WhenAll(tasks);
return results.All(r => r);
```

**Why deferred**: the guard clauses immediately *before* this pattern differ per service
(Discord checks `WebhookUrls.Count == 0`; DiscordDm checks both `BotToken` and `UserIds.Count`;
Twilio/VoipMs check `ToNumbers.Count` plus service-specific message prep — VoipMs additionally
runs `SanitizeForSms`). A shared helper would need to either take a pre-validated recipient list
(pushing the guard clause out of the helper, which is fine) or accept a delegate for the
not-quite-uniform per-service setup — either way it's a real design decision, not a pure
copy-paste extraction like the ones already done.

**Sketch, if picked up**: extract to `PlatformHelpers.FanOutAsync<T>(IEnumerable<T> recipients,
Func<T, Task<bool>> sendOne)` returning `Task<bool>`, called *after* each service's own
recipient-list validation. Four call sites, ~2 lines saved each — small win, low risk once the
guard-clause question above is settled.

## 2. Per-platform settings pass-through properties

> **Status: implemented.** `IPlatformFilterSettings` added to `Config/AppSettings.cs`; all 11
> `{Platform}Settings` classes implement it; each service now exposes `IsEnabled` (kept, since
> `ImageSmokeTestService`/`StartupConfirmationService`/the orchestrator's feed-level checks all
> read it directly) plus a single `Filter => _settings` instead of the 6 other pass-through
> properties. `SocialMediaOrchestrator`'s tuple array and both `Where` filter clauses read
> through `.Filter.X`. `CLAUDE.md` Rule #5 updated to reference the interface.

**Where**: every one of the 11 delivery-platform services (`FacebookService`,
`InstagramService`, `XService`, `BlueskyService`, `MastodonService`, `PushoverService`,
`TwilioService`, `DiscordService`, `DiscordDmService`, `TelegramService`, `VoipMsService`) repeats
the identical 7-line block:
```csharp
public bool IsEnabled => _settings.Enabled;
public string MinSeverity => _settings.MinSeverity;
public string EventTypes => _settings.EventTypes;
public bool IncludeSpcOutlooks => _settings.IncludeSpcOutlooks;
public bool IncludeSpcMcd     => _settings.IncludeSpcMcd;
public bool IncludeHwo        => _settings.IncludeHwo;
public bool IncludeEro        => _settings.IncludeEro;
```
(~77 lines total across the 11 files.) Consumed by `Services/SocialMediaOrchestrator.cs:220-232`,
which builds one large tuple array reading all 7 properties per platform to decide who gets each
alert (`RunAsync`'s `all` array and the filter check right after it, ~line 239).

**Why deferred**: biggest total footprint (12 files touched — the 11 services plus
`SocialMediaOrchestrator`) for the smallest per-line win, and it's a structural change rather
than a pure extraction.

**Sketch, if picked up**:
1. Define `internal interface IPlatformFilterSettings { bool Enabled; string MinSeverity; string
   EventTypes; bool IncludeSpcOutlooks; bool IncludeSpcMcd; bool IncludeHwo; bool IncludeEro; }`
   in `Config/AppSettings.cs`.
2. Have each `{Platform}Settings` class implement it (property names already match exactly, so
   this is a one-line `: IPlatformFilterSettings` per class — see the corresponding block CLAUDE.md
   Rule #5 already documents as mandatory for every settings class).
3. Replace each service's 7-line pass-through block with a single `public IPlatformFilterSettings
   Filter => _settings;`.
4. Update `SocialMediaOrchestrator`'s tuple array (lines 220-232) and the `PassesFilter`/`IncludeHwo`
   /`IncludeEro` checks right after it to read through `.Filter.X` instead of the 7 separate
   properties — this is the part that actually touches a 12th file and makes this a real refactor,
   not just a mechanical extraction.

Net effect: ~65 fewer lines, at the cost of touching every platform service plus the orchestrator
in one PR. Rule #5 in `CLAUDE.md` would also need updating to reference the interface instead of
spelling out the 7 properties per new platform.
