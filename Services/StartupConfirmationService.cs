using Microsoft.Extensions.Logging;

namespace NwsAlertBot.Services;

/// <summary>
/// On first startup, sends a confirmation message to each enabled platform so you can
/// verify credentials are working. Each platform is confirmed independently — a failure
/// on one does not prevent others from confirming.
///
/// Once a platform has confirmed successfully, it is recorded in "confirmed_platforms.txt"
/// and will never receive another confirmation message, even after restarts.
///
/// The confirmation post should be deleted by the user once they have verified it appeared.
/// </summary>
public class StartupConfirmationService
{
    private const string ConfirmationFile = "confirmed_platforms.txt";

    private readonly FacebookService  _facebook;
    private readonly InstagramService _instagram;
    private readonly XService         _x;
    private readonly BlueskyService   _bluesky;
    private readonly MastodonService  _mastodon;
    private readonly PushoverService  _pushover;
    private readonly TwilioService    _twilio;
    private readonly NtfyService      _ntfy;
    private readonly DiscordService   _discord;
    private readonly VoipMsService    _voipMs;
    private readonly ILogger<StartupConfirmationService> _logger;

    private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public StartupConfirmationService(
        FacebookService  facebook,
        InstagramService instagram,
        XService         x,
        BlueskyService   bluesky,
        MastodonService  mastodon,
        PushoverService  pushover,
        TwilioService    twilio,
        NtfyService      ntfy,
        DiscordService   discord,
        VoipMsService    voipMs,
        ILogger<StartupConfirmationService> logger)
    {
        _facebook  = facebook;
        _instagram = instagram;
        _x         = x;
        _bluesky   = bluesky;
        _mastodon  = mastodon;
        _pushover  = pushover;
        _twilio    = twilio;
        _ntfy      = ntfy;
        _discord   = discord;
        _voipMs    = voipMs;
        _logger    = logger;

        LoadConfirmed();
    }

    /// <summary>
    /// Run once at startup. Sends a confirmation message to every enabled platform that
    /// has not previously confirmed. Skips platforms that already confirmed in a prior run.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var platforms = BuildPlatformList();
        var pending = platforms.Where(p => !IsConfirmed(p.Name)).ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation("Startup confirmation: all enabled platforms already confirmed.");
            return;
        }

        _logger.LogInformation(
            "Startup confirmation: sending test message to {Count} unconfirmed platform(s): {Platforms}",
            pending.Count, string.Join(", ", pending.Select(p => p.Name)));

        string message = BuildConfirmationMessage();

        var tasks = pending.Select(p => SendAndRecord(p, message, ct));
        await Task.WhenAll(tasks);

        int confirmedNow = pending.Count(p => IsConfirmed(p.Name));
        _logger.LogInformation(
            "Startup confirmation complete. {Success}/{Total} platform(s) confirmed. " +
            "Delete the test posts from your accounts once verified.",
            confirmedNow, pending.Count);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private record PlatformEntry(string Name, Func<string, Task<bool>> Send);

    private List<PlatformEntry> BuildPlatformList() => new()
    {
        new("Facebook",  msg => _facebook.SendConfirmationAsync(msg)),
        new("Instagram", msg => _instagram.SendConfirmationAsync(msg)),
        new("X",         msg => _x.SendConfirmationAsync(msg)),
        new("Bluesky",   msg => _bluesky.SendConfirmationAsync(msg)),
        new("Mastodon",  msg => _mastodon.SendConfirmationAsync(msg)),
        new("Pushover",  msg => _pushover.SendConfirmationAsync(msg)),
        new("Twilio",    msg => _twilio.SendConfirmationAsync(msg)),
        new("Ntfy",      msg => _ntfy.SendConfirmationAsync(msg)),
        new("Discord",   msg => _discord.SendConfirmationAsync(msg)),
        new("VoipMs",    msg => _voipMs.SendConfirmationAsync(msg)),
    };

    private static string BuildConfirmationMessage() =>
        $"✅ NWS Alert Bot — connection confirmed on {DateTime.Now:dddd, MMMM d, yyyy 'at' h:mm tt}. " +
        $"This is a one-time test message. Please delete it once you have verified it arrived.";

    private async Task SendAndRecord(PlatformEntry platform, string message, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            bool success = await platform.Send(message);
            if (success)
            {
                MarkConfirmed(platform.Name);
                _logger.LogInformation("Startup confirmation: {Platform} confirmed successfully.", platform.Name);
            }
            else
            {
                _logger.LogWarning(
                    "Startup confirmation: {Platform} delivery failed — check credentials and Enabled flag. " +
                    "Will retry on next startup.", platform.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup confirmation: {Platform} threw an exception.", platform.Name);
        }
    }

    private bool IsConfirmed(string platformName)
    {
        lock (_lock) return _confirmed.Contains(platformName);
    }

    private void MarkConfirmed(string platformName)
    {
        lock (_lock)
        {
            _confirmed.Add(platformName);
            Save();
        }
    }

    private void LoadConfirmed()
    {
        if (!File.Exists(ConfirmationFile)) return;
        try
        {
            foreach (var line in File.ReadAllLines(ConfirmationFile))
                if (!string.IsNullOrWhiteSpace(line))
                    _confirmed.Add(line.Trim());

            if (_confirmed.Count > 0)
                _logger.LogInformation(
                    "Startup confirmation: previously confirmed platforms: {Platforms}",
                    string.Join(", ", _confirmed));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup confirmation: could not read confirmation file.");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllLines(ConfirmationFile, _confirmed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup confirmation: could not write confirmation file.");
        }
    }
}
