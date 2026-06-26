using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using NwsAlertBot.Config;
using NwsAlertBot.Services;

// ---------------------------------------------------------------
// NWS Alert Social Media + Push Notification Bot
// Built with .NET 8 Console App / Generic Host
// ---------------------------------------------------------------

LocalConfigSync.Run();

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
        var discordSettings   = cfg.GetSection("Discord").Get<DiscordSettings>()       ?? new DiscordSettings();
        var discordDmSettings = cfg.GetSection("DiscordDm").Get<DiscordDmSettings>() ?? new DiscordDmSettings();
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
        services.AddSingleton(discordDmSettings);
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
        services.AddHttpClient<DiscordDmService>();
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
        // Telegram embeds the bot token in the URL path (required by the Bot API).
        // Suppress HttpClient request/response logging for this client to keep the token out of logs.
        logging.AddFilter("System.Net.Http.HttpClient.TelegramService", LogLevel.Warning);
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
// Local config sync — runs once at startup before the host is built.
// Compares appsettings.Local.json against appsettings.json and adds
// any missing keys (within sections that already exist in local) with
// conservative defaults: booleans → false, strings → "", numbers keep
// the base default. Top-level sections absent from local are skipped
// so unconfigured platforms don't get empty blocks injected.
// ---------------------------------------------------------------
static class LocalConfigSync
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Run(string basePath = "appsettings.json", string localPath = "appsettings.Local.json")
    {
        if (!File.Exists(localPath) || !File.Exists(basePath)) return;

        var localRoot = JsonNode.Parse(File.ReadAllText(localPath));
        var baseRoot  = JsonNode.Parse(File.ReadAllText(basePath));

        if (localRoot is not JsonObject localObj || baseRoot is not JsonObject baseObj) return;

        var added = new List<string>();
        Merge(localObj, baseObj, path: "", added);

        if (added.Count == 0) return;

        File.WriteAllText(localPath, localRoot.ToJsonString(WriteOptions));
        Console.WriteLine($"[Config] Added {added.Count} missing setting(s) to {localPath}: {string.Join(", ", added)}");
    }

    // Recursively copies keys present in source but absent from local.
    // Missing top-level sections that have an Enabled field are injected as disabled.
    // Sections without Enabled (e.g. Nws) are skipped at the top level.
    private static void Merge(JsonObject local, JsonObject source, string path, List<string> added)
    {
        foreach (var (key, sourceValue) in source)
        {
            string fullKey = path.Length > 0 ? $"{path}.{key}" : key;

            if (local.ContainsKey(key))
            {
                if (sourceValue is JsonObject srcChild && local[key] is JsonObject localChild)
                    Merge(localChild, srcChild, fullKey, added);
            }
            else if (path.Length == 0)
            {
                // Top-level: inject service sections (those with Enabled) as disabled
                if (sourceValue is JsonObject srcSection && srcSection.ContainsKey("Enabled"))
                {
                    local[key] = BuildOffSection(srcSection);
                    added.Add($"{key} (disabled)");
                }
            }
            else
            {
                local[key] = OffDefault(sourceValue);
                added.Add(fullKey);
            }
        }
    }

    // Builds a JsonObject from source with all values set to off defaults.
    private static JsonObject BuildOffSection(JsonObject source)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in source)
            obj[key] = value is JsonObject nested ? BuildOffSection(nested) : OffDefault(value);
        return obj;
    }

    // Returns a conservative "off" default for a missing key.
    // Booleans → false, strings → "", numbers keep the base value.
    private static JsonNode OffDefault(JsonNode? node) => node switch
    {
        JsonObject                                     => new JsonObject(),
        JsonArray                                      => new JsonArray(),
        JsonValue v when v.TryGetValue<bool>(out _)   => JsonValue.Create(false)!,
        JsonValue v when v.TryGetValue<string>(out _) => JsonValue.Create("")!,
        JsonValue                                      => node.DeepClone(), // numbers — keep base default
        _                                              => JsonValue.Create(false)!,
    };
}

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
