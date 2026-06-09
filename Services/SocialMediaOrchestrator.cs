using Microsoft.Extensions.Logging;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Coordinates fetching alerts and posting to all enabled platforms,
/// including social media and push/SMS notification channels.
/// </summary>
public class SocialMediaOrchestrator
{
    private readonly NwsAlertService _nws;
    private readonly AlertTrackerService _tracker;
    private readonly FacebookService _facebook;
    private readonly InstagramService _instagram;
    private readonly XService _x;
    private readonly BlueskyService _bluesky;
    private readonly MastodonService _mastodon;
    private readonly PushoverService _pushover;
    private readonly TwilioService _twilio;
    private readonly NtfyService _ntfy;
    private readonly ILogger<SocialMediaOrchestrator> _logger;

    public SocialMediaOrchestrator(
        NwsAlertService nws,
        AlertTrackerService tracker,
        FacebookService facebook,
        InstagramService instagram,
        XService x,
        BlueskyService bluesky,
        MastodonService mastodon,
        PushoverService pushover,
        TwilioService twilio,
        NtfyService ntfy,
        ILogger<SocialMediaOrchestrator> logger)
    {
        _nws       = nws;
        _tracker   = tracker;
        _facebook  = facebook;
        _instagram = instagram;
        _x         = x;
        _bluesky   = bluesky;
        _mastodon  = mastodon;
        _pushover  = pushover;
        _twilio    = twilio;
        _ntfy      = ntfy;
        _logger    = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Checking for new NWS alerts...");

        var alerts = await _nws.GetActiveAlertsAsync();

        int newCount = 0;
        foreach (var alert in alerts)
        {
            if (ct.IsCancellationRequested) break;
            if (_tracker.HasBeenPosted(alert.Id)) continue;

            _logger.LogInformation("New alert: [{Severity}] {Event} — {AreaDesc}",
                alert.Severity, alert.Event, alert.AreaDesc);

            await PostToAllPlatformsAsync(alert);
            _tracker.MarkPosted(alert.Id);
            newCount++;

            // Brief delay between alerts to avoid rate limit bursts
            if (newCount < alerts.Count)
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (newCount == 0)
            _logger.LogInformation("No new alerts to post.");
        else
            _logger.LogInformation("Posted {Count} new alert(s).", newCount);

        _tracker.PruneOldEntries();
    }

    private async Task PostToAllPlatformsAsync(NwsAlert alert)
    {
        var all = new (string Name, bool Enabled, string MinSeverity, string EventTypes, Func<Task<bool>> Action)[]
        {
            ("Facebook",  _facebook.IsEnabled,  _facebook.MinSeverity,  _facebook.EventTypes,  () => _facebook.PostAlertAsync(alert)),
            ("Instagram", _instagram.IsEnabled, _instagram.MinSeverity, _instagram.EventTypes, () => _instagram.PostAlertAsync(alert)),
            ("X",         _x.IsEnabled,         _x.MinSeverity,         _x.EventTypes,         () => _x.PostAlertAsync(alert)),
            ("Bluesky",   _bluesky.IsEnabled,   _bluesky.MinSeverity,   _bluesky.EventTypes,   () => _bluesky.PostAlertAsync(alert)),
            ("Mastodon",  _mastodon.IsEnabled,  _mastodon.MinSeverity,  _mastodon.EventTypes,  () => _mastodon.PostAlertAsync(alert)),
            ("Pushover",  _pushover.IsEnabled,  _pushover.MinSeverity,  _pushover.EventTypes,  () => _pushover.SendAlertAsync(alert)),
            ("Twilio",    _twilio.IsEnabled,    _twilio.MinSeverity,    _twilio.EventTypes,    () => _twilio.SendAlertAsync(alert)),
            ("Ntfy",      _ntfy.IsEnabled,      _ntfy.MinSeverity,      _ntfy.EventTypes,      () => _ntfy.SendAlertAsync(alert)),
        };

        var filtered = all
            .Where(p => p.Enabled && (!PassesFilter(alert.Severity, p.MinSeverity) || !PassesFilter(alert.Event, p.EventTypes)))
            .Select(p => p.Name)
            .ToList();
        if (filtered.Count > 0)
            _logger.LogInformation("{Platforms}: Skipped [{Severity}] {Event} — platform filter.",
                string.Join(", ", filtered), alert.Severity, alert.Event);

        var tasks = all
            .Where(p => p.Enabled && PassesFilter(alert.Severity, p.MinSeverity) && PassesFilter(alert.Event, p.EventTypes))
            .Select(p => WrapPost(p.Name, p.Action))
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks);
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
