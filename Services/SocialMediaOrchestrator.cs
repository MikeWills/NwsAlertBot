using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Coordinates fetching alerts and posting to all enabled platforms,
/// including social media and push/SMS notification channels.
/// </summary>
public class SocialMediaOrchestrator
{
    private readonly PollingSettings _polling;
    private readonly NwsAlertService _nws;
    private readonly SpcOutlookService _spc;
    private readonly SpcMcdService _spcMcd;
    private readonly HwoService _hwo;
    private readonly WpcEroService _ero;
    private readonly AlertTrackerService _tracker;
    private readonly MapService _map;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FacebookService _facebook;
    private readonly InstagramService _instagram;
    private readonly XService _x;
    private readonly BlueskyService _bluesky;
    private readonly MastodonService _mastodon;
    private readonly PushoverService _pushover;
    private readonly TwilioService _twilio;
    private readonly DiscordService _discord;
    private readonly DiscordDmService _discordDm;
    private readonly TelegramService _telegram;
    private readonly VoipMsService _voipMs;
    private readonly ILogger<SocialMediaOrchestrator> _logger;

    public SocialMediaOrchestrator(
        PollingSettings polling,
        NwsAlertService nws,
        SpcOutlookService spc,
        SpcMcdService spcMcd,
        HwoService hwo,
        WpcEroService ero,
        AlertTrackerService tracker,
        MapService map,
        IHttpClientFactory httpClientFactory,
        FacebookService facebook,
        InstagramService instagram,
        XService x,
        BlueskyService bluesky,
        MastodonService mastodon,
        PushoverService pushover,
        TwilioService twilio,
        DiscordService discord,
        DiscordDmService discordDm,
        TelegramService telegram,
        VoipMsService voipMs,
        ILogger<SocialMediaOrchestrator> logger)
    {
        _polling          = polling;
        _nws              = nws;
        _spc              = spc;
        _spcMcd           = spcMcd;
        _hwo              = hwo;
        _ero              = ero;
        _tracker          = tracker;
        _map              = map;
        _httpClientFactory = httpClientFactory;
        _facebook         = facebook;
        _instagram = instagram;
        _x         = x;
        _bluesky   = bluesky;
        _mastodon  = mastodon;
        _pushover  = pushover;
        _twilio    = twilio;
        _discord   = discord;
        _discordDm = discordDm;
        _telegram  = telegram;
        _voipMs    = voipMs;
        _logger    = logger;
    }

    /// <summary>
    /// Runs one poll cycle. Returns the number of new alerts that qualify as active-storm
    /// triggers (i.e. meet ActiveAlertMinSeverity). Lower-severity alerts are still posted
    /// but do not count toward triggering accelerated polling.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Checking for new NWS alerts...");

        var alerts = await _nws.GetActiveAlertsAsync();

        int newCount = 0;
        int stormCount = 0;
        foreach (var alert in alerts)
        {
            if (ct.IsCancellationRequested) break;
            if (_tracker.HasBeenPosted(alert.Id)) continue;

            _logger.LogInformation("New {MessageType}: [{Severity}] {Event} — {AreaDesc}",
                alert.MessageType, alert.Severity, alert.Event, alert.AreaDesc);

            alert.MapImageUrl = await _map.GetMapUrlAsync(alert);
            await DownloadMapImageAsync(alert);

            await PostToAllPlatformsAsync(alert);
            _tracker.MarkPosted(alert.Id);
            newCount++;

            if (PassesFilter(alert.Severity, _polling.ActiveAlertMinSeverity))
                stormCount++;

            // Brief delay between alerts to avoid rate limit bursts
            if (newCount < alerts.Count)
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (newCount == 0)
            _logger.LogInformation("No new alerts to post.");
        else
            _logger.LogInformation("Posted {Count} new alert(s) ({StormCount} storm-triggering).", newCount, stormCount);

        if (_spc.IsEnabled)
            await CheckSpcOutlooksAsync(ct);

        if (_spcMcd.IsEnabled)
            stormCount += await CheckSpcMcdsAsync(ct);

        if (_hwo.IsEnabled)
            await CheckHwoAsync(ct);

        if (_ero.IsEnabled)
            await CheckEroAsync(ct);

        return stormCount;
    }

    private async Task CheckSpcOutlooksAsync(CancellationToken ct)
    {
        var outlookAlerts = await _spc.GetOutlookAlertsAsync();

        foreach (var alert in outlookAlerts)
        {
            if (ct.IsCancellationRequested) break;
            if (_tracker.HasBeenPosted(alert.Id)) continue;

            _logger.LogInformation("New SPC outlook: [{Severity}] {Event} — {AreaDesc}",
                alert.Severity, alert.Event, alert.AreaDesc);

            await DownloadMapImageAsync(alert);
            await PostToAllPlatformsAsync(alert);
            _tracker.MarkPosted(alert.Id);
        }
    }

    /// <summary>
    /// Returns the number of new MCDs posted (each counts as one storm-triggering event
    /// for expedited polling, since MCDs indicate active or developing severe weather).
    /// </summary>
    private async Task<int> CheckSpcMcdsAsync(CancellationToken ct)
    {
        var mcdAlerts = await _spcMcd.GetMcdAlertsAsync();
        int count = 0;

        foreach (var alert in mcdAlerts)
        {
            if (ct.IsCancellationRequested) break;
            if (_tracker.HasBeenPosted(alert.Id)) continue;

            _logger.LogInformation("New SPC MCD: {Event} — {AreaDesc}", alert.Event, alert.AreaDesc);

            await DownloadMapImageAsync(alert);
            await PostToAllPlatformsAsync(alert);
            _tracker.MarkPosted(alert.Id);
            count++;
        }

        return count;
    }

    /// <summary>
    /// HWO is text-only (no polygon, no map image), so unlike the MCD/outlook checks this
    /// skips DownloadMapImageAsync entirely rather than attempting a pointless Mapbox fallback.
    /// </summary>
    private async Task CheckHwoAsync(CancellationToken ct)
    {
        var hwoAlerts = await _hwo.GetHwoAlertsAsync();

        foreach (var alert in hwoAlerts)
        {
            if (ct.IsCancellationRequested) break;
            if (_tracker.HasBeenPosted(alert.Id)) continue;

            _logger.LogInformation("New HWO: {AreaDesc}", alert.AreaDesc);

            await PostToAllPlatformsAsync(alert);
            _tracker.MarkPosted(alert.Id);
        }
    }

    private async Task CheckEroAsync(CancellationToken ct)
    {
        var eroAlerts = await _ero.GetEroAlertsAsync();

        foreach (var alert in eroAlerts)
        {
            if (ct.IsCancellationRequested) break;
            if (_tracker.HasBeenPosted(alert.Id)) continue;

            _logger.LogInformation("New WPC ERO: [{Severity}] {Event} — {AreaDesc}",
                alert.Severity, alert.Event, alert.AreaDesc);

            await DownloadMapImageAsync(alert);
            await PostToAllPlatformsAsync(alert);
            _tracker.MarkPosted(alert.Id);
        }
    }

    private async Task PostToAllPlatformsAsync(NwsAlert alert)
    {
        var all = new (string Name, bool Enabled, bool IncludeSpc, bool IncludeMcd, bool IncludeHwo, bool IncludeEro, string MinSeverity, string EventTypes, Func<Task<bool>> Action)[]
        {
            ("Facebook",  _facebook.IsEnabled,  _facebook.IncludeSpcOutlooks,  _facebook.IncludeSpcMcd,  _facebook.IncludeHwo,  _facebook.IncludeEro,  _facebook.MinSeverity,  _facebook.EventTypes,  () => _facebook.PostAlertAsync(alert)),
            ("Instagram", _instagram.IsEnabled, _instagram.IncludeSpcOutlooks, _instagram.IncludeSpcMcd, _instagram.IncludeHwo, _instagram.IncludeEro, _instagram.MinSeverity, _instagram.EventTypes, () => _instagram.PostAlertAsync(alert)),
            ("X",         _x.IsEnabled,         _x.IncludeSpcOutlooks,         _x.IncludeSpcMcd,         _x.IncludeHwo,         _x.IncludeEro,         _x.MinSeverity,         _x.EventTypes,         () => _x.PostAlertAsync(alert)),
            ("Bluesky",   _bluesky.IsEnabled,   _bluesky.IncludeSpcOutlooks,   _bluesky.IncludeSpcMcd,   _bluesky.IncludeHwo,   _bluesky.IncludeEro,   _bluesky.MinSeverity,   _bluesky.EventTypes,   () => _bluesky.PostAlertAsync(alert)),
            ("Mastodon",  _mastodon.IsEnabled,  _mastodon.IncludeSpcOutlooks,  _mastodon.IncludeSpcMcd,  _mastodon.IncludeHwo,  _mastodon.IncludeEro,  _mastodon.MinSeverity,  _mastodon.EventTypes,  () => _mastodon.PostAlertAsync(alert)),
            ("Pushover",  _pushover.IsEnabled,  _pushover.IncludeSpcOutlooks,  _pushover.IncludeSpcMcd,  _pushover.IncludeHwo,  _pushover.IncludeEro,  _pushover.MinSeverity,  _pushover.EventTypes,  () => _pushover.SendAlertAsync(alert)),
            ("Twilio",    _twilio.IsEnabled,    _twilio.IncludeSpcOutlooks,    _twilio.IncludeSpcMcd,    _twilio.IncludeHwo,    _twilio.IncludeEro,    _twilio.MinSeverity,    _twilio.EventTypes,    () => _twilio.SendAlertAsync(alert)),
            ("Discord",   _discord.IsEnabled,   _discord.IncludeSpcOutlooks,   _discord.IncludeSpcMcd,   _discord.IncludeHwo,   _discord.IncludeEro,   _discord.MinSeverity,   _discord.EventTypes,   () => _discord.PostAlertAsync(alert)),
            ("DiscordDm", _discordDm.IsEnabled, _discordDm.IncludeSpcOutlooks, _discordDm.IncludeSpcMcd, _discordDm.IncludeHwo, _discordDm.IncludeEro, _discordDm.MinSeverity, _discordDm.EventTypes, () => _discordDm.PostAlertAsync(alert)),
            ("Telegram",  _telegram.IsEnabled,  _telegram.IncludeSpcOutlooks,  _telegram.IncludeSpcMcd,  _telegram.IncludeHwo,  _telegram.IncludeEro,  _telegram.MinSeverity,  _telegram.EventTypes,  () => _telegram.SendAlertAsync(alert)),
            ("VoipMs",    _voipMs.IsEnabled,    _voipMs.IncludeSpcOutlooks,    _voipMs.IncludeSpcMcd,    _voipMs.IncludeHwo,    _voipMs.IncludeEro,    _voipMs.MinSeverity,    _voipMs.EventTypes,    () => _voipMs.SendAlertAsync(alert)),
        };

        var filtered = all
            .Where(p => p.Enabled && (
                (alert.IsSpcOutlook && !p.IncludeSpc) ||
                (alert.IsSpcMcd && !p.IncludeMcd) ||
                (alert.IsHwo && !p.IncludeHwo) ||
                (alert.IsEro && !p.IncludeEro) ||
                !PassesFilter(alert.Severity, p.MinSeverity) ||
                !PassesFilter(alert.Event, p.EventTypes)))
            .Select(p => p.Name)
            .ToList();
        if (filtered.Count > 0)
            _logger.LogInformation("{Platforms}: Skipped [{Severity}] {Event} — platform filter.",
                string.Join(", ", filtered), alert.Severity, alert.Event);

        var tasks = all
            .Where(p => p.Enabled &&
                !(alert.IsSpcOutlook && !p.IncludeSpc) &&
                !(alert.IsSpcMcd && !p.IncludeMcd) &&
                !(alert.IsHwo && !p.IncludeHwo) &&
                !(alert.IsEro && !p.IncludeEro) &&
                PassesFilter(alert.Severity, p.MinSeverity) &&
                PassesFilter(alert.Event, p.EventTypes))
            .Select(p => WrapPost(p.Name, p.Action))
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks);
    }

    private async Task DownloadMapImageAsync(NwsAlert alert, CancellationToken ct = default)
    {
        // If a primary URL is set, try it first (IEM may take a few seconds to index a new event).
        if (!string.IsNullOrEmpty(alert.MapImageUrl) &&
            await TryDownloadAsync(alert.MapImageUrl, maxAttempts: 4, retryDelay: 5, alert, ct))
            return;

        // Fall back to Mapbox when the primary URL is absent or its download fails.
        var mapboxUrl = await _map.GetMapboxFallbackUrlAsync(alert);
        if (mapboxUrl != null)
        {
            _logger.LogInformation("Map: Primary image unavailable; trying Mapbox fallback for {Event}.", alert.Event);
            if (await TryDownloadAsync(mapboxUrl, maxAttempts: 1, retryDelay: 0, alert, ct))
                return;
        }

        _logger.LogWarning("Map: Image unavailable for {Event}; platforms will post without image.", alert.Event);
    }

    private async Task<bool> TryDownloadAsync(string url, int maxAttempts, int retryDelay, NwsAlert alert, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                alert.MapImageBytes = await _httpClientFactory.CreateClient().GetByteArrayAsync(url, ct);
                return true;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogInformation(
                    "Map: Download attempt {Attempt}/{Max} failed ({Error}); retrying in {Delay}s.",
                    attempt, maxAttempts, ex.Message, retryDelay);
                await Task.Delay(TimeSpan.FromSeconds(retryDelay), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Map: Download failed after {Max} attempt(s) — {Error}.", maxAttempts, ex.Message);
            }
        }
        return false;
    }

    private static bool PassesFilter(string? value, string? allowList)
    {
        if (string.IsNullOrWhiteSpace(allowList)) return true;
        return allowList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private async Task WrapPost(string platform, Func<Task<bool>> action)
    {
        try
        {
            var success = await action();
            if (!success)
                _logger.LogWarning("{Platform}: Delivery was not successful (see above for details).", platform);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Platform}: Unhandled exception.", platform);
        }
    }
}
