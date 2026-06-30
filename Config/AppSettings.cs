namespace NwsAlertBot.Config;

public class NwsSettings
{
    /// <summary>
    /// Two-letter state code fallback, e.g. "MO".
    /// Used only if Zones and Counties are both empty.
    /// Leave empty for nationwide (not recommended — very noisy).
    /// </summary>
    public string State { get; set; } = "";

    /// <summary>
    /// NWS forecast zone codes to monitor. Format: {ST}Z{###}
    /// Example: ["MOZ066", "MOZ067", "MOZ068"]
    /// Find yours at: https://alerts.weather.gov/ or https://www.weather.gov/gis/
    /// If specified, takes priority over State.
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

    /// <summary>
    /// Filter by minimum severity — passed directly to the NWS API.
    /// Valid values: Extreme, Severe, Moderate, Minor, Unknown
    /// Can be a comma-separated list, e.g. "Extreme,Severe"
    /// Leave empty to receive all severities.
    /// </summary>
    public string Severity { get; set; } = "Severe,Extreme";

    /// <summary>
    /// Filter by urgency — passed directly to the NWS API.
    /// Valid values: Immediate, Expected, Future, Past, Unknown
    /// Leave empty for all urgency levels.
    /// </summary>
    public string Urgency { get; set; } = "";

    /// <summary>
    /// Filter by certainty — passed directly to the NWS API.
    /// Valid values: Observed, Likely, Possible, Unlikely, Unknown
    /// Leave empty for all certainty levels.
    /// </summary>
    public string Certainty { get; set; } = "";

    /// <summary>
    /// Filter by specific event type(s) — passed directly to the NWS API.
    /// Examples: "Tornado Warning", "Flash Flood Warning", "Severe Thunderstorm Warning"
    /// Can be a comma-separated list.
    /// Leave empty for all event types.
    /// NOTE: If you set Severity above, you likely don't need this too.
    /// </summary>
    public string EventTypes { get; set; } = "";

    /// <summary>
    /// Additional event types to always fetch regardless of the Severity filter.
    /// Use this to include specific lower-severity events (e.g. "Special Weather Statement,
    /// Winter Weather Advisory") alongside a Severity filter like "Moderate,Severe,Extreme".
    /// These are fetched in a separate API call (no severity filter) and merged with the main results.
    /// Leave empty if not needed.
    /// </summary>
    public string AdditionalEventTypes { get; set; } = "";

    /// <summary>
    /// IANA time zone ID used to format Issued/Valid/Expires times on all alert posts
    /// (both NWS and SPC). Works on Windows and Linux. Examples: "America/Chicago",
    /// "America/New_York", "America/Denver", "America/Los_Angeles".
    /// See README for a full US reference table.
    /// </summary>
    public string TimeZone { get; set; } = "America/Chicago";
}

public class FacebookSettings
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
}

public class InstagramSettings
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
}

public class XSettings
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
}

public class BlueskySettings
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
}

public class MastodonSettings
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
}

public class PushoverSettings
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
}

public class TwilioSettings
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
}

public class DiscordDmSettings
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
}

public class DiscordSettings
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
}

public class TelegramSettings
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
}

public class VoipMsSettings
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
    /// for the locations derived from Nws.Zones (or Nws.Counties if Zones is empty).
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
    /// MCDs respect the per-platform IncludeSpcOutlooks flag and also trigger
    /// expedited polling the same as severe/extreme NWS alerts.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum seconds between checks for active MCDs against the NWS products API.
    /// MCDs can be issued and expire within 1-3 hours, so frequent checks are fine.
    /// Default 300 (5 min) — matches the regular poll interval.
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 300;
}

