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
        var all = new (string Name, bool Enabled, IPlatformFilterSettings Filter, Func<Task<bool>> Action)[]
        {
            ("Facebook",  _facebook.IsEnabled,  _facebook.Filter,  () => _facebook.PostAlertAsync(alert)),
            ("Instagram", _instagram.IsEnabled, _instagram.Filter, () => _instagram.PostAlertAsync(alert)),
            ("X",         _x.IsEnabled,         _x.Filter,         () => _x.PostAlertAsync(alert)),
            ("Bluesky",   _bluesky.IsEnabled,   _bluesky.Filter,   () => _bluesky.PostAlertAsync(alert)),
            ("Mastodon",  _mastodon.IsEnabled,  _mastodon.Filter,  () => _mastodon.PostAlertAsync(alert)),
            ("Pushover",  _pushover.IsEnabled,  _pushover.Filter,  () => _pushover.SendAlertAsync(alert)),
            ("Twilio",    _twilio.IsEnabled,    _twilio.Filter,    () => _twilio.SendAlertAsync(alert)),
            ("Discord",   _discord.IsEnabled,   _discord.Filter,   () => _discord.PostAlertAsync(alert)),
            ("DiscordDm", _discordDm.IsEnabled, _discordDm.Filter, () => _discordDm.PostAlertAsync(alert)),
            ("Telegram",  _telegram.IsEnabled,  _telegram.Filter,  () => _telegram.SendAlertAsync(alert)),
            ("VoipMs",    _voipMs.IsEnabled,    _voipMs.Filter,    () => _voipMs.SendAlertAsync(alert)),
        };

        var filtered = all
            .Where(p => p.Enabled && (
                (alert.IsSpcOutlook && !p.Filter.IncludeSpcOutlooks) ||
                (alert.IsSpcMcd && !p.Filter.IncludeSpcMcd) ||
                (alert.IsHwo && !p.Filter.IncludeHwo) ||
                (alert.IsEro && !p.Filter.IncludeEro) ||
                !PassesFilter(alert.Severity, p.Filter.MinSeverity) ||
                !PassesFilter(alert.Event, p.Filter.EventTypes)))
            .Select(p => p.Name)
            .ToList();
        if (filtered.Count > 0)
            _logger.LogInformation("{Platforms}: Skipped [{Severity}] {Event} — platform filter.",
                string.Join(", ", filtered), alert.Severity, alert.Event);

        var tasks = all
            .Where(p => p.Enabled &&
                !(alert.IsSpcOutlook && !p.Filter.IncludeSpcOutlooks) &&
                !(alert.IsSpcMcd && !p.Filter.IncludeSpcMcd) &&
                !(alert.IsHwo && !p.Filter.IncludeHwo) &&
                !(alert.IsEro && !p.Filter.IncludeEro) &&
                PassesFilter(alert.Severity, p.Filter.MinSeverity) &&
                PassesFilter(alert.Event, p.Filter.EventTypes))
            .Select(p => WrapPost(p.Name, p.Action))
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks);
    }

    private async Task DownloadMapImageAsync(NwsAlert alert, CancellationToken ct = default)
    {
        // If a primary URL is set, try it first ("WeatherImageryPrimary" retries transient
        // failures — IEM may take a few seconds to index a new event).
        if (!string.IsNullOrEmpty(alert.MapImageUrl) &&
            await TryDownloadAsync("WeatherImageryPrimary", alert.MapImageUrl, alert, ct))
            return;

        // Fall back to Mapbox when the primary URL is absent or its download fails. No retry here
        // ("WeatherImageryFallback") — this is already the last resort.
        var mapboxUrl = await _map.GetMapboxFallbackUrlAsync(alert);
        if (mapboxUrl != null)
        {
            _logger.LogInformation("Map: Primary image unavailable; trying Mapbox fallback for {Event}.", alert.Event);
            if (await TryDownloadAsync("WeatherImageryFallback", mapboxUrl, alert, ct))
                return;
        }

        _logger.LogWarning("Map: Image unavailable for {Event}; platforms will post without image.", alert.Event);
    }

    private async Task<bool> TryDownloadAsync(string clientName, string url, NwsAlert alert, CancellationToken ct)
    {
        try
        {
            alert.MapImageBytes = await _httpClientFactory.CreateClient(clientName).GetByteArrayAsync(url, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Map: Download failed for {Event} — {Error}.", alert.Event, ex.Message);
            return false;
        }
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
