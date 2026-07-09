namespace NwsAlertBot.Config;

/// <summary>
/// Geographic area and time zone shared by every feed (Nws, Spc, SpcMcd, Hwo, Ero, and the map
/// image bounding-box fallback in MapService). None of the five alert feeds have their own
/// notion of location — they all resolve WFOs/centroids/geometry from these same Zones and
/// Counties. Adding a new feed in the future should inject LocationSettings, not duplicate
/// its own copy of these fields.
/// </summary>
public class LocationSettings
{
    /// <summary>
    /// NWS forecast zone codes to monitor. Format: {ST}Z{###}
    /// Example: ["MOZ066", "MOZ067", "MOZ068"]
    /// Find yours at: https://alerts.weather.gov/ or https://www.weather.gov/gis/
    /// </summary>
    public List<string> Zones { get; set; } = new();

    /// <summary>
    /// NWS county codes to monitor. Format: {ST}C{###}
    /// Example: ["MOC217", "MOC039", "MOC185"]
    /// County codes use 3-digit FIPS county numbers.
    /// Find yours at: https://alerts.weather.gov/
    /// </summary>
    public List<string> Counties { get; set; } = new();

    /// <summary>
    /// IANA time zone ID used to format Issued/Valid/Expires times on all alert posts
    /// (NWS, SPC, and HWO). Works on Windows and Linux. Examples: "America/Chicago",
    /// "America/New_York", "America/Denver", "America/Los_Angeles".
    /// See README for a full US reference table.
    /// </summary>
    public string TimeZone { get; set; } = "America/Chicago";
}

/// <summary>
/// Master polling loop cadence — how often SocialMediaOrchestrator.RunAsync() checks all
/// feeds. Each feed still self-gates on its own CheckIntervalSeconds, so this only controls
/// how quickly the bot notices a new poll cycle is due. Only NWS alerts and SPC MCDs ever
/// trigger the accelerated (ActiveAlert*) window — SPC Outlook and HWO never do.
/// </summary>
public class PollingSettings
{
    /// <summary>
    /// Idle poll interval in seconds — used when no active alerts have been seen recently.
    /// 300 (5 minutes) is a reasonable default to avoid hammering the NWS API.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Accelerated poll interval in seconds, active while within the storm alert window.
    /// Defaults to 60 seconds for near-real-time monitoring during active weather events.
    /// </summary>
    public int ActiveAlertPollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How many hours after the last new alert was posted to remain in accelerated polling mode.
    /// Resets to this full window each time a new alert is posted.
    /// </summary>
    public double ActiveAlertWindowHours { get; set; } = 4;

    /// <summary>
    /// Minimum severity required for a new alert to trigger (or extend) accelerated polling mode.
    /// Lower-severity alerts (e.g. Moderate advisories added via AdditionalEventTypes) are posted
    /// normally but do not engage the faster poll interval.
    /// Valid values: Extreme, Severe, Moderate, Minor, Unknown
    /// Comma-separated list: "Severe,Extreme" means only Severe or Extreme alerts trigger active mode.
    /// Leave empty to have any new alert trigger active mode.
    /// </summary>
    public string ActiveAlertMinSeverity { get; set; } = "Severe,Extreme";
}

/// <summary>
/// Query filters for the NWS CAP alerts feed only (regular warnings/watches/advisories + SPS).
/// These are sent directly to api.weather.gov as query parameters — a server-side, master
/// filter that nothing downstream can override. See LocationSettings for Zones/Counties/State
/// (shared by all feeds) and PollingSettings for poll cadence.
/// </summary>
public class NwsSettings
{
    /// <summary>
    /// Two-letter state code fallback, e.g. "MO".
    /// Used only if Location.Zones and Location.Counties are both empty.
    /// Only affects the main NWS alerts feed — SPC Outlook/MCD/HWO always require explicit
    /// Zones/Counties and do not fall back to a whole state.
    /// Leave empty for nationwide (not recommended — very noisy).
    /// </summary>
    public string State { get; set; } = "";

    /// <summary>
    /// Filters the feed at the server level — sent directly to the NWS API as a query
    /// parameter, so anything excluded here is never returned to the bot at all.
    /// Valid values: Extreme, Severe, Moderate, Minor, Unknown
    /// Can be a comma-separated list, e.g. "Extreme,Severe"
    /// Leave empty to receive all severities.
    /// </summary>
    public string FilterSeverity { get; set; } = "Severe,Extreme";

    /// <summary>
    /// Filters the feed at the server level — sent directly to the NWS API as a query
    /// parameter, so anything excluded here is never returned to the bot at all.
    /// Valid values: Immediate, Expected, Future, Past, Unknown
    /// Leave empty for all urgency levels.
    /// </summary>
    public string FilterUrgency { get; set; } = "";

    /// <summary>
    /// Filters the feed at the server level — sent directly to the NWS API as a query
    /// parameter, so anything excluded here is never returned to the bot at all.
    /// Valid values: Observed, Likely, Possible, Unlikely, Unknown
    /// Leave empty for all certainty levels.
    /// </summary>
    public string FilterCertainty { get; set; } = "";

    /// <summary>
    /// Filters the feed at the server level — sent directly to the NWS API as a query
    /// parameter, so anything excluded here is never returned to the bot at all.
    /// Examples: "Tornado Warning", "Flash Flood Warning", "Severe Thunderstorm Warning"
    /// Can be a comma-separated list.
    /// Leave empty for all event types.
    /// NOTE: If you set FilterSeverity above, you likely don't need this too.
    /// </summary>
    public string FilterEventTypes { get; set; } = "";

    /// <summary>
    /// The opposite of the Filter* fields above: pulls in extra event types that would
    /// otherwise be excluded by FilterSeverity, rather than restricting the feed further.
    /// Use this to include specific lower-severity events (e.g. "Special Weather Statement,
    /// Winter Weather Advisory") alongside a FilterSeverity like "Moderate,Severe,Extreme".
    /// These are fetched in a separate API call (no severity filter) and merged with the main results.
    /// Leave empty if not needed.
    /// </summary>
    public string AdditionalEventTypes { get; set; } = "";
}

/// <summary>
/// The six per-platform alert-filtering properties every delivery-platform settings class
/// exposes (MinSeverity/EventTypes plus the four synthetic-feed opt-ins). Implemented by
/// every {Platform}Settings class below so SocialMediaOrchestrator can read them through one
/// `Filter` property per service instead of six separate pass-through properties.
/// </summary>
public interface IPlatformFilterSettings
{
    string MinSeverity { get; }
    string EventTypes { get; }
    bool IncludeSpcOutlooks { get; }
    bool IncludeSpcMcd { get; }
    bool IncludeHwo { get; }
    bool IncludeEro { get; }
}

public class FacebookSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;
    public string PageAccessToken { get; set; } = "";
    public string PageId { get; set; } = "";

    /// <summary>
    /// Optional per-platform severity filter. Leave empty to post everything that passes the global filter.
    /// Use a comma-separated list to restrict this platform further: "Severe,Extreme" skips Moderate alerts.
    /// Valid values: Extreme, Severe, Moderate, Minor, Unknown
    /// </summary>
    public string MinSeverity { get; set; } = "";

    /// <summary>
    /// Optional per-platform event type filter. Comma-separated list of NWS event names this platform
    /// will post. Leave empty to post all event types that pass other filters.
    /// Example: "Tornado Warning,Tornado Watch" to post only tornado alerts to this platform.
    /// </summary>
    public string EventTypes { get; set; } = "";

    /// <summary>Whether to post SPC Convective Outlook alerts to this platform. Requires Spc.Enabled = true.</summary>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <summary>Whether to post SPC Mesoscale Discussion alerts to this platform. Requires SpcMcd.Enabled = true.</summary>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <summary>
    /// Whether to post the Hazardous Weather Outlook (HWO) text product to this platform.
    /// Requires Hwo.Enabled = true. Defaults to false — the full HWO text is long and best
    /// suited to a personal channel (e.g. Discord DM, Telegram) rather than short-form platforms.
    /// </summary>
    public bool IncludeHwo { get; set; } = false;

    /// <summary>
    /// Whether to post WPC Excessive Rainfall Outlook (ERO) alerts to this platform.
    /// Requires Ero.Enabled = true.
    /// </summary>
    public bool IncludeEro { get; set; } = true;
}

public class InstagramSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;
    public string PageAccessToken { get; set; } = "";
    public string InstagramAccountId { get; set; } = "";
    /// <summary>
    /// URL of a publicly accessible image to attach to each post.
    /// Instagram requires an image — text-only posts are not supported.
    /// </summary>
    public string ImageUrl { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class XSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string AccessTokenSecret { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;

    /// <summary>
    /// Quota guard: max posts per rolling 30-day window, matching X's free-tier limit (500
    /// posts/month; Basic tier is 3,000/month — raise this to match if you're on Basic). Once
    /// reached, further posts are skipped and logged until the window rolls over, rather than
    /// burning requests X would reject anyway. Persists across restarts (x_post_count.txt).
    /// Set to 0 to disable (no limit).
    /// </summary>
    public int MaxPostsPerMonth { get; set; } = 500;
}

public class BlueskySettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;
    public string Handle { get; set; } = "";
    public string AppPassword { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class MastodonSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;
    public string InstanceUrl { get; set; } = "";
    public string AccessToken { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class PushoverSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Your Pushover application API token. Create an app at https://pushover.net/apps</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Your Pushover user key (or group key). Found at https://pushover.net/</summary>
    public string UserKey { get; set; } = "";

    /// <summary>
    /// Default priority for most alerts.
    /// -2 = silent, -1 = quiet, 0 = normal, 1 = high (bypasses quiet hours), 2 = emergency (repeats until acknowledged)
    /// </summary>
    public int DefaultPriority { get; set; } = 1;

    /// <summary>
    /// Priority override for Extreme severity alerts (e.g. Tornado Warning, Flash Flood Emergency).
    /// Set to 2 for emergency priority that repeats until acknowledged and bypasses Do Not Disturb.
    /// </summary>
    public int ExtremePriority { get; set; } = 2;

    /// <summary>
    /// For emergency priority (2): how often in seconds to retry until acknowledged. Minimum 30.
    /// </summary>
    public int EmergencyRetrySeconds { get; set; } = 60;

    /// <summary>
    /// For emergency priority (2): how long in seconds to keep retrying before giving up. Maximum 10800 (3 hours).
    /// </summary>
    public int EmergencyExpireSeconds { get; set; } = 3600;

    /// <summary>
    /// Pushover sound to play. Leave empty for user default.
    /// Options: pushover, bike, bugle, cashregister, classical, cosmic, falling, gamelan, incoming,
    ///          intermission, magic, mechanical, pianobar, siren, spacealarm, tugboat, alien, climb,
    ///          persistent, echo, updown, vibrate, none
    /// </summary>
    public string Sound { get; set; } = "siren";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class TwilioSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Twilio Account SID from https://console.twilio.com/</summary>
    public string AccountSid { get; set; } = "";

    /// <summary>Twilio Auth Token from https://console.twilio.com/</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>Your Twilio phone number in E.164 format, e.g. "+15551234567"</summary>
    public string FromNumber { get; set; } = "";

    /// <summary>
    /// One or more recipient phone numbers in E.164 format.
    /// Multiple numbers supported — all will receive each alert.
    /// Example: ["+15559876543", "+15557654321"]
    /// </summary>
    public List<string> ToNumbers { get; set; } = new();

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;

    /// <summary>
    /// Cost/quota guard: max SMS sends per rolling 24-hour window, counted per individual message
    /// (i.e. one alert to 3 ToNumbers counts as 3). Once reached, further sends are skipped and
    /// logged until the window rolls over — protects against runaway Twilio charges during a busy
    /// severe weather outbreak. Persists across restarts (twilio_sms_count.txt). Set to 0 to
    /// disable (no limit).
    /// </summary>
    public int MaxSmsPerDay { get; set; } = 100;
}

public class DiscordDmSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Discord bot token from the Developer Portal (Bot → Token).
    /// The bot must share a server with each recipient, or the recipient must allow DMs
    /// from server members. Create a bot at https://discord.com/developers/applications
    /// </summary>
    public string BotToken { get; set; } = "";

    /// <summary>
    /// Discord user IDs to DM. Right-click a user in Discord (with Developer Mode on)
    /// and choose "Copy User ID". All listed users receive every alert.
    /// Example: ["123456789012345678", "987654321098765432"]
    /// </summary>
    public List<string> UserIds { get; set; } = new();

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class DiscordSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// One or more Discord Incoming Webhook URLs. Each URL posts to a different
    /// channel or server. Create one in your server:
    /// Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL
    /// Example: ["https://discord.com/api/webhooks/..."]
    /// </summary>
    public List<string> WebhookUrls { get; set; } = new();

    /// <summary>
    /// Optional: override the display name shown for messages posted by this webhook.
    /// Leave empty to use the webhook's configured name.
    /// </summary>
    public string Username { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class TelegramSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Bot token from @BotFather. Message @BotFather on Telegram, run /newbot, and
    /// copy the token it gives you (format: "123456789:ABC-DEF...").
    /// </summary>
    public string BotToken { get; set; } = "";

    /// <summary>
    /// Destination chat ID. For a private chat, message your bot once, then look up
    /// your numeric chat ID (e.g. via @userinfobot). For a channel, add the bot as an
    /// admin and use the channel's @username or its numeric -100... ID.
    /// </summary>
    public string ChatId { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class VoipMsSettings : IPlatformFilterSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// VoIP.ms API username. Enable API access and set this at:
    /// https://voip.ms/m/api.php (Main Menu → SOAP and REST/JSON API)
    /// </summary>
    public string ApiUsername { get; set; } = "";

    /// <summary>
    /// VoIP.ms API password. This is separate from your account password and is set on the
    /// same API settings page. You must also allow your server's IP address there.
    /// </summary>
    public string ApiPassword { get; set; } = "";

    /// <summary>
    /// Your VoIP.ms DID (phone number) to send from, digits only, e.g. "5551234567".
    /// </summary>
    public string Did { get; set; } = "";

    /// <summary>
    /// One or more recipient phone numbers, digits only, e.g. ["5559876543"].
    /// All numbers receive each alert.
    /// </summary>
    public List<string> ToNumbers { get; set; } = new();

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.IncludeSpcOutlooks"/>
    public bool IncludeSpcOutlooks { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeSpcMcd"/>
    public bool IncludeSpcMcd { get; set; } = true;

    /// <inheritdoc cref="FacebookSettings.IncludeHwo"/>
    public bool IncludeHwo { get; set; } = false;

    /// <inheritdoc cref="FacebookSettings.IncludeEro"/>
    public bool IncludeEro { get; set; } = true;
}

public class MapSettings
{
    /// <summary>Whether to generate Mapbox static map images for alert areas.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Mapbox public access token. Create a free account at https://account.mapbox.com/
    /// and copy the default public token (or create a new one under Tokens).
    /// Free tier: 50,000 static map images/month.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>
    /// Mapbox map style. Format: "{username}/{style_id}".
    /// Built-in options: "mapbox/outdoors-v12", "mapbox/streets-v12",
    /// "mapbox/light-v11", "mapbox/dark-v11", "mapbox/satellite-streets-v12"
    /// </summary>
    public string Style { get; set; } = "mapbox/outdoors-v12";

    /// <summary>Map image width in pixels. Maximum 1280.</summary>
    public int Width { get; set; } = 600;

    /// <summary>Map image height in pixels. Maximum 1280.</summary>
    public int Height { get; set; } = 400;
}

public class SpcSettings
{
    /// <summary>
    /// Whether to monitor SPC (Storm Prediction Center) Day 1/Day 2 Convective Outlooks
    /// for the locations derived from Location.Zones (or Location.Counties if Zones is empty).
    /// Alerts when a monitored location is in any non-"None" categorical risk
    /// (Thunderstorm or higher) on either day, bundled with that location's
    /// tornado/wind/hail probability.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum seconds between checks against the SPC outlook GeoJSON feeds.
    /// SPC re-issues the Day 1 outlook ~5x/day and Day 2 ~2x/day, so polling more
    /// often than every few minutes has no benefit. Default 1800 (30 min).
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 1800;

}

public class SpcMcdSettings
{
    /// <summary>
    /// Whether to monitor SPC Mesoscale Discussions (MCDs) for the monitored area.
    /// When enabled, each poll cycle checks for active MCDs whose polygon covers at
    /// least one configured zone/county centroid and posts them to enabled platforms.
    /// Per-platform delivery is controlled by IncludeSpcMcd on each platform's settings
    /// (independent of IncludeSpcOutlooks). Each new MCD also triggers expedited polling
    /// the same as severe/extreme NWS alerts.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum seconds between checks for active MCDs against the NWS products API.
    /// MCDs can be issued and expire within 1-3 hours, so frequent checks are fine.
    /// Default 300 (5 min) — matches the regular poll interval.
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 300;
}

public class HwoSettings
{
    /// <summary>
    /// Whether to monitor the Hazardous Weather Outlook (HWO) text product for the WFO(s)
    /// covering Location.Zones/Location.Counties. HWO is plain text only — no polygon, no map image —
    /// and is issued 1-2x/day per office. Intended for personal/informational use: delivery
    /// to each platform is controlled independently by IncludeHwo on that platform's settings,
    /// which defaults to false since the full product text is long and not well suited to
    /// short-form platforms like X/Bluesky. HWO alerts carry Severity "Unknown" — if a
    /// platform's MinSeverity filter excludes "Unknown", it will not receive HWO posts even
    /// with IncludeHwo enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum seconds between checks against the NWS text products API for a new HWO issuance.
    /// HWO is typically issued 1-2x/day per office, so polling more than every few minutes
    /// has no benefit. Default 300 (5 min) — matches the regular poll interval.
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 300;
}

public class EroSettings
{
    /// <summary>
    /// Whether to monitor the WPC (Weather Prediction Center) Excessive Rainfall Outlook (ERO)
    /// Day 1/2/3 categorical risk for the locations derived from Location.Zones (or
    /// Location.Counties if Zones is empty). Alerts when a monitored location is in any
    /// non-"None" categorical risk (Marginal or higher) on any of the three days. Note: despite
    /// the "Spc"-prefixed sibling settings (Spc, SpcMcd), ERO is issued by WPC, not SPC.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum seconds between checks against the WPC ERO GeoJSON feeds. WPC re-issues Day 1
    /// several times a day and Day 2/3 less often, so polling more than every few minutes has
    /// no benefit. Default 1800 (30 min) — matches Spc.CheckIntervalSeconds.
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 1800;
}

/// <summary>
/// Self-update checking against GitHub Releases. Aimed at people running a standalone release
/// binary (see scripts/update.ps1 and the release.yml pipeline that produces per-platform
/// archives) rather than this repo owner's own continuously-deployed server (deploy.yml pushes
/// straight from master on every commit and has no need for this).
/// </summary>
public class UpdateSettings
{
    /// <summary>
    /// Single on/off switch for the whole feature. False: no GitHub API calls are made at all —
    /// upgrade manually by running scripts/update.ps1 yourself whenever you want. True: checks
    /// GitHub Releases on the configured interval and, when a newer version is found,
    /// automatically launches scripts/update.ps1 (which downloads it, replaces the running
    /// executable, and restarts) and shuts this process down so the script can replace it.
    /// </summary>
    public bool AutoApply { get; set; } = false;

    /// <summary>
    /// How often to check for a new release. Checks are cheap, but releases are infrequent —
    /// once a day is plenty and avoids needless GitHub API calls.
    /// </summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// GitHub "owner/repo" to check for releases. Defaults to the upstream project; change this
    /// if you're running your own fork with its own release/tagging cadence.
    /// </summary>
    public string GitHubRepo { get; set; } = "MikeWills/NwsAlertBot";

    /// <summary>
    /// Passed to scripts/update.ps1 as -ServiceName so it restarts the right systemd unit/Windows
    /// Service after swapping the executable. Must match whatever -ServiceName you gave
    /// scripts/setup-service.ps1 for this instance — required if you're running more than one
    /// instance on the same machine (each needs a distinct name; see README "Running as a
    /// Service"). Defaults to setup-service.ps1's own default, so single-instance setups don't
    /// need to touch this.
    /// </summary>
    public string ServiceName { get; set; } = "nwsalertbot";
}

