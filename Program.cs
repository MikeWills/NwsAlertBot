using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Services;

// ---------------------------------------------------------------
// NWS Alert Social Media + Push Notification Bot
// Built with .NET 8 Console App / Generic Host
// ---------------------------------------------------------------

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(config =>
    {
        config.AddJsonFile("appsettings.json",       optional: false, reloadOnChange: false);
        config.AddJsonFile("appsettings.Local.json", optional: true,  reloadOnChange: false);
    })
    .ConfigureServices((context, services) =>
    {
        var cfg = context.Configuration;

        // Bind all config sections
        var nwsSettings       = cfg.GetSection("Nws").Get<NwsSettings>()             ?? new NwsSettings();
        var facebookSettings  = cfg.GetSection("Facebook").Get<FacebookSettings>()   ?? new FacebookSettings();
        var instagramSettings = cfg.GetSection("Instagram").Get<InstagramSettings>() ?? new InstagramSettings();
        var xSettings         = cfg.GetSection("X").Get<XSettings>()                 ?? new XSettings();
        var blueskySettings   = cfg.GetSection("Bluesky").Get<BlueskySettings>()     ?? new BlueskySettings();
        var mastodonSettings  = cfg.GetSection("Mastodon").Get<MastodonSettings>()   ?? new MastodonSettings();
        var pushoverSettings  = cfg.GetSection("Pushover").Get<PushoverSettings>()   ?? new PushoverSettings();
        var twilioSettings    = cfg.GetSection("Twilio").Get<TwilioSettings>()       ?? new TwilioSettings();
        var discordSettings   = cfg.GetSection("Discord").Get<DiscordSettings>()     ?? new DiscordSettings();
        var telegramSettings  = cfg.GetSection("Telegram").Get<TelegramSettings>()   ?? new TelegramSettings();
        var voipMsSettings    = cfg.GetSection("VoipMs").Get<VoipMsSettings>()       ?? new VoipMsSettings();
        var mapSettings       = cfg.GetSection("Map").Get<MapSettings>()             ?? new MapSettings();
        var spcSettings       = cfg.GetSection("Spc").Get<SpcSettings>()             ?? new SpcSettings();

        // Register settings as singletons
        services.AddSingleton(nwsSettings);
        services.AddSingleton(facebookSettings);
        services.AddSingleton(instagramSettings);
        services.AddSingleton(xSettings);
        services.AddSingleton(blueskySettings);
        services.AddSingleton(mastodonSettings);
        services.AddSingleton(pushoverSettings);
        services.AddSingleton(twilioSettings);
        services.AddSingleton(discordSettings);
        services.AddSingleton(telegramSettings);
        services.AddSingleton(voipMsSettings);
        services.AddSingleton(mapSettings);
        services.AddSingleton(spcSettings);

        // HttpClients — each service gets its own typed client
        services.AddHttpClient<NwsAlertService>(client =>
        {
            // NWS API requires a descriptive User-Agent with contact info
            client.DefaultRequestHeaders.Add("User-Agent", "NwsAlertBot/1.0 (contact@yourorg.com)");
            client.DefaultRequestHeaders.Add("Accept", "application/geo+json");
        });

        services.AddHttpClient<FacebookService>();
        services.AddHttpClient<InstagramService>();
        services.AddHttpClient<XService>();
        services.AddHttpClient<BlueskyService>();
        services.AddHttpClient<MastodonService>();
        services.AddHttpClient<PushoverService>();
        services.AddHttpClient<TwilioService>();
        services.AddHttpClient<DiscordService>();
        services.AddHttpClient<TelegramService>();
        services.AddHttpClient<VoipMsService>();
        services.AddHttpClient<NwsZoneService>(client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "NwsAlertBot/1.0 (contact@yourorg.com)");
            client.DefaultRequestHeaders.Add("Accept", "application/geo+json");
        });
        services.AddHttpClient<SpcOutlookService>(client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "NwsAlertBot/1.0 (contact@yourorg.com)");
            client.DefaultRequestHeaders.Add("Accept", "application/geo+json");
        });

        // Core services
        services.AddSingleton<AlertTrackerService>();
        services.AddSingleton<MapService>();
        services.AddSingleton<StartupConfirmationService>();
        services.AddSingleton<SocialMediaOrchestrator>();
        services.AddSingleton<ImageSmokeTestService>();

        // Background polling loop
        services.AddHostedService<AlertPollingService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

// Dev tool: posts a synthetic alert with a test image to every enabled image-capable platform,
// then exits without starting the live polling loop. See ImageSmokeTestService for details.
if (args.Contains("--smoke-test-image"))
{
    await host.Services.GetRequiredService<ImageSmokeTestService>().RunAsync();
    return;
}

await host.RunAsync();


// ---------------------------------------------------------------
// Background polling service
// ---------------------------------------------------------------
public class AlertPollingService : BackgroundService
{
    private readonly StartupConfirmationService  _confirmation;
    private readonly SocialMediaOrchestrator     _orchestrator;
    private readonly NwsSettings                 _settings;
    private readonly ILogger<AlertPollingService> _logger;

    // Tracks when the last new alert was posted; null means no alert seen yet this session.
    private DateTime? _lastNewAlertUtc;

    public AlertPollingService(
        StartupConfirmationService  confirmation,
        SocialMediaOrchestrator     orchestrator,
        NwsSettings                 settings,
        ILogger<AlertPollingService> logger)
    {
        _confirmation = confirmation;
        _orchestrator = orchestrator;
        _settings     = settings;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string geoFilter = _settings.Zones.Count > 0
            ? $"Zones: {string.Join(", ", _settings.Zones)}"
            : _settings.Counties.Count > 0
                ? $"Counties: {string.Join(", ", _settings.Counties)}"
                : string.IsNullOrWhiteSpace(_settings.State) ? "Nationwide" : $"State: {_settings.State}";

        _logger.LogInformation(
            "NWS Alert Bot started. Idle poll: {Idle}s | Active poll: {Active}s (window: {Hours}h) | {GeoFilter} | Min severity: {Severity}",
            _settings.PollIntervalSeconds, _settings.ActiveAlertPollIntervalSeconds,
            _settings.ActiveAlertWindowHours, geoFilter, _settings.Severity);

        // Send one-time confirmation to any platform not yet verified
        await _confirmation.RunAsync(stoppingToken);

        // Main polling loop
        while (!stoppingToken.IsCancellationRequested)
        {
            int newAlerts = await _orchestrator.RunAsync(stoppingToken);

            if (newAlerts > 0)
            {
                if (_lastNewAlertUtc == null)
                    _logger.LogInformation("Active storm mode engaged — polling every {Interval}s for {Hours}h.",
                        _settings.ActiveAlertPollIntervalSeconds, _settings.ActiveAlertWindowHours);
                else
                    _logger.LogInformation("Active storm window reset — {Count} new alert(s) posted.", newAlerts);

                _lastNewAlertUtc = DateTime.UtcNow;
            }

            int delaySeconds;
            if (_lastNewAlertUtc.HasValue &&
                (DateTime.UtcNow - _lastNewAlertUtc.Value).TotalHours < _settings.ActiveAlertWindowHours)
            {
                delaySeconds = _settings.ActiveAlertPollIntervalSeconds;
            }
            else
            {
                if (_lastNewAlertUtc.HasValue)
                {
                    _logger.LogInformation("Active storm window expired — returning to idle poll interval ({Interval}s).",
                        _settings.PollIntervalSeconds);
                    _lastNewAlertUtc = null;
                }
                delaySeconds = _settings.PollIntervalSeconds;
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }

        _logger.LogInformation("NWS Alert Bot stopped.");
    }
}
