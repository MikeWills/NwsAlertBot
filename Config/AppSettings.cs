namespace NwsAlertBot.Config;

public class AppSettings
{
    public NwsSettings Nws { get; set; } = new();
    public FacebookSettings Facebook { get; set; } = new();
    public InstagramSettings Instagram { get; set; } = new();
    public XSettings X { get; set; } = new();
    public BlueskySettings Bluesky { get; set; } = new();
    public MastodonSettings Mastodon { get; set; } = new();
    public PushoverSettings Pushover { get; set; } = new();
    public TwilioSettings Twilio { get; set; } = new();
    public VoipMsSettings VoipMs { get; set; } = new();
    public NtfySettings Ntfy { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
}

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

    /// <summary>Poll interval in seconds. 60 is recommended.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

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
}

public class DiscordSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Discord Incoming Webhook URL. Create one in your server:
    /// Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL
    /// </summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>
    /// Optional: override the display name shown for messages posted by this webhook.
    /// Leave empty to use the webhook's configured name.
    /// </summary>
    public string Username { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";
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
}

public class NtfySettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base server URL. Use "https://ntfy.sh" for the hosted service,
    /// or your self-hosted instance URL, e.g. "https://ntfy.example.com"
    /// </summary>
    public string ServerUrl { get; set; } = "https://ntfy.sh";

    /// <summary>
    /// Topic name to publish to. Treat this like a password — make it hard to guess
    /// unless you are using access control on a self-hosted instance.
    /// Example: "nws-alerts-a7f3k9"
    /// </summary>
    public string Topic { get; set; } = "";

    /// <summary>
    /// Optional: username for authentication (self-hosted instances with access control,
    /// or ntfy.sh accounts with reserved topics).
    /// Leave empty for anonymous access to public topics.
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>Optional: password for authentication. Leave empty for anonymous access.</summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// Default priority for most alerts.
    /// 1 = min, 2 = low, 3 = default, 4 = high, 5 = urgent (max, bypasses DND)
    /// </summary>
    public int DefaultPriority { get; set; } = 4;

    /// <summary>
    /// Priority override for Extreme severity alerts.
    /// Set to 5 (urgent) to bypass Do Not Disturb for life-threatening events.
    /// </summary>
    public int ExtremePriority { get; set; } = 5;

    /// <inheritdoc cref="FacebookSettings.MinSeverity"/>
    public string MinSeverity { get; set; } = "";

    /// <inheritdoc cref="FacebookSettings.EventTypes"/>
    public string EventTypes { get; set; } = "";
}
