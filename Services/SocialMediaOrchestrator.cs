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
    private readonly NwsSettings _nwsSettings;
    private readonly NwsAlertService _nws;
    private readonly SpcOutlookService _spc;
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
        NwsSettings nwsSettings,
        NwsAlertService nws,
        SpcOutlookService spc,
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
        _nwsSettings      = nwsSettings;
        _nws              = nws;
        _spc              = spc;
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
            await _map.CleanupAsync(alert.Id);

            await PostToAllPlatformsAsync(alert);
            _tracker.MarkPosted(alert.Id);
            newCount++;

            if (PassesFilter(alert.Severity, _nwsSettings.ActiveAlertMinSeverity))
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

    private async Task PostToAllPlatformsAsync(NwsAlert alert)
    {
        var all = new (string Name, bool Enabled, bool IncludeSpc, string MinSeverity, string EventTypes, Func<Task<bool>> Action)[]
        {
            ("Facebook",  _facebook.IsEnabled,  _facebook.IncludeSpcOutlooks,  _facebook.MinSeverity,  _facebook.EventTypes,  () => _facebook.PostAlertAsync(alert)),
            ("Instagram", _instagram.IsEnabled, _instagram.IncludeSpcOutlooks, _instagram.MinSeverity, _instagram.EventTypes, () => _instagram.PostAlertAsync(alert)),
            ("X",         _x.IsEnabled,         _x.IncludeSpcOutlooks,         _x.MinSeverity,         _x.EventTypes,         () => _x.PostAlertAsync(alert)),
            ("Bluesky",   _bluesky.IsEnabled,   _bluesky.IncludeSpcOutlooks,   _bluesky.MinSeverity,   _bluesky.EventTypes,   () => _bluesky.PostAlertAsync(alert)),
            ("Mastodon",  _mastodon.IsEnabled,  _mastodon.IncludeSpcOutlooks,  _mastodon.MinSeverity,  _mastodon.EventTypes,  () => _mastodon.PostAlertAsync(alert)),
            ("Pushover",  _pushover.IsEnabled,  _pushover.IncludeSpcOutlooks,  _pushover.MinSeverity,  _pushover.EventTypes,  () => _pushover.SendAlertAsync(alert)),
            ("Twilio",    _twilio.IsEnabled,    _twilio.IncludeSpcOutlooks,    _twilio.MinSeverity,    _twilio.EventTypes,    () => _twilio.SendAlertAsync(alert)),
            ("Discord",   _discord.IsEnabled,   _discord.IncludeSpcOutlooks,   _discord.MinSeverity,   _discord.EventTypes,   () => _discord.PostAlertAsync(alert)),
            ("DiscordDm", _discordDm.IsEnabled, _discordDm.IncludeSpcOutlooks, _discordDm.MinSeverity, _discordDm.EventTypes, () => _discordDm.PostAlertAsync(alert)),
            ("Telegram",  _telegram.IsEnabled,  _telegram.IncludeSpcOutlooks,  _telegram.MinSeverity,  _telegram.EventTypes,  () => _telegram.SendAlertAsync(alert)),
            ("VoipMs",    _voipMs.IsEnabled,    _voipMs.IncludeSpcOutlooks,    _voipMs.MinSeverity,    _voipMs.EventTypes,    () => _voipMs.SendAlertAsync(alert)),
        };

        var filtered = all
            .Where(p => p.Enabled && (
                (alert.IsSpcOutlook && !p.IncludeSpc) ||
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
                PassesFilter(alert.Severity, p.MinSeverity) &&
                PassesFilter(alert.Event, p.EventTypes))
            .Select(p => WrapPost(p.Name, p.Action))
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks);
    }

    private async Task DownloadMapImageAsync(NwsAlert alert)
    {
        if (string.IsNullOrEmpty(alert.MapImageUrl)) return;
        try
        {
            alert.MapImageBytes = await _httpClientFactory.CreateClient().GetByteArrayAsync(alert.MapImageUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Map image download failed for {Event}; platforms will post without image.", alert.Event);
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
