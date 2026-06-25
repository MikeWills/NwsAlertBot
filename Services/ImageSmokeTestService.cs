using Microsoft.Extensions.Logging;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Dev tool: posts one synthetic alert carrying a real image URL to every enabled platform
/// that supports image attachment, exercising the same MapImageUrl code path used for Mapbox
/// alert maps and SPC outlook maps. Unlike StartupConfirmationService (text-only), this is the
/// only way to verify the image-upload code (Facebook /photos, X media upload, Bluesky
/// uploadBlob, Mastodon media upload, Twilio MMS) actually works end-to-end against live APIs.
/// Run with: dotnet run -- --smoke-test-image
/// </summary>
public class ImageSmokeTestService
{
    // A stable, public, auth-free test image (an SPC outlook plot) — no Mapbox token required.
    private const string TestImageUrl =
        "https://mesonet.agron.iastate.edu/plotting/auto/plot/220/which:1C::cat:categorical::t:cwa::network:WFO::wfo:MPX::csector:MN::_r:t::dpi:100.png";

    private readonly FacebookService  _facebook;
    private readonly InstagramService _instagram;
    private readonly XService         _x;
    private readonly BlueskyService   _bluesky;
    private readonly MastodonService  _mastodon;
    private readonly TwilioService    _twilio;
    private readonly DiscordService   _discord;
    private readonly TelegramService  _telegram;
    private readonly ILogger<ImageSmokeTestService> _logger;

    public ImageSmokeTestService(
        FacebookService  facebook,
        InstagramService instagram,
        XService         x,
        BlueskyService   bluesky,
        MastodonService  mastodon,
        TwilioService    twilio,
        DiscordService   discord,
        TelegramService  telegram,
        ILogger<ImageSmokeTestService> logger)
    {
        _facebook  = facebook;
        _instagram = instagram;
        _x         = x;
        _bluesky   = bluesky;
        _mastodon  = mastodon;
        _twilio    = twilio;
        _discord   = discord;
        _telegram  = telegram;
        _logger    = logger;
    }

    public async Task RunAsync()
    {
        var alert = new NwsAlert
        {
            Id          = $"smoketest-image-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            Event       = "Image Smoke Test",
            Headline    = "🧪 NWS Alert Bot — image smoke test",
            AreaDesc    = "Test Area",
            Severity    = "Severe",
            SenderName  = "NwsAlertBot",
            Instruction = "This is an automated test post verifying image attachment works. Please delete it once confirmed.",
            Sent        = DateTimeOffset.UtcNow,
            MapImageUrl = TestImageUrl,
        };

        var platforms = new (string Name, bool Enabled, Func<Task<bool>> Action)[]
        {
            ("Facebook",  _facebook.IsEnabled,  () => _facebook.PostAlertAsync(alert)),
            ("Instagram", _instagram.IsEnabled, () => _instagram.PostAlertAsync(alert)),
            ("X",         _x.IsEnabled,         () => _x.PostAlertAsync(alert)),
            ("Bluesky",   _bluesky.IsEnabled,   () => _bluesky.PostAlertAsync(alert)),
            ("Mastodon",  _mastodon.IsEnabled,  () => _mastodon.PostAlertAsync(alert)),
            ("Discord",   _discord.IsEnabled,   () => _discord.PostAlertAsync(alert)),
            ("Telegram",  _telegram.IsEnabled,  () => _telegram.SendAlertAsync(alert)),
            ("Twilio",    _twilio.IsEnabled,    () => _twilio.SendAlertAsync(alert)),
        };

        var enabled = platforms.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
        {
            _logger.LogWarning("ImageSmokeTest: No image-capable platforms are enabled. Nothing to test.");
            return;
        }

        _logger.LogInformation("ImageSmokeTest: Posting test image to {Count} enabled platform(s): {Platforms}",
            enabled.Count, string.Join(", ", enabled.Select(p => p.Name)));

        foreach (var platform in enabled)
        {
            try
            {
                bool success = await platform.Action();
                if (success)
                    _logger.LogInformation(
                        "ImageSmokeTest: {Platform} — OK. Verify the image rendered correctly, then delete the test post.",
                        platform.Name);
                else
                    _logger.LogWarning("ImageSmokeTest: {Platform} — FAILED. See the error above for details.", platform.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImageSmokeTest: {Platform} — threw an exception.", platform.Name);
            }
        }

        _logger.LogInformation("ImageSmokeTest: Done.");
    }
}
