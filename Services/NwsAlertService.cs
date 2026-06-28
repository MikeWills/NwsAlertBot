using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Polls the NWS REST API for active alerts.
/// API docs: https://www.weather.gov/documentation/services-web-api
///
/// Filtering is pushed to the API where possible (zones, counties, severity,
/// urgency, certainty, event type) to minimize data transfer and processing.
/// When AdditionalEventTypes is set, a second API call is made with only the
/// geographic filter so those event types bypass the Severity filter.
/// </summary>
public class NwsAlertService
{
    private readonly HttpClient _http;
    private readonly NwsSettings _settings;
    private readonly ILogger<NwsAlertService> _logger;
    private readonly TimeZoneInfo _timeZone;

    public NwsAlertService(HttpClient http, NwsSettings settings, ILogger<NwsAlertService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
        _timeZone = ResolveTimeZone(settings.TimeZone, logger);
    }

    private static TimeZoneInfo ResolveTimeZone(string id, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { logger.LogWarning("Nws: Unknown TimeZone \"{Id}\"; falling back to America/Chicago.", id); }
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch
        {
            logger.LogWarning("Nws: Could not load America/Chicago as fallback timezone; using UTC. Set Nws.TimeZone to a valid IANA ID such as \"America/Chicago\".");
            return TimeZoneInfo.Utc;
        }
    }

    public async Task<List<NwsAlert>> GetActiveAlertsAsync()
    {
        var alerts = new List<NwsAlert>();

        try
        {
            alerts = await FetchAlertsAsync(BuildUrl());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch NWS alerts.");
        }

        if (!string.IsNullOrWhiteSpace(_settings.AdditionalEventTypes))
        {
            try
            {
                var additional = await FetchAlertsAsync(BuildAdditionalUrl());
                var seen = new HashSet<string>(alerts.Select(a => a.Id), StringComparer.Ordinal);
                foreach (var a in additional)
                    if (seen.Add(a.Id)) alerts.Add(a);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NWS: Failed to fetch AdditionalEventTypes alerts.");
            }
        }

        _logger.LogInformation("NWS returned {Count} qualifying active alerts.", alerts.Count);
        return alerts;
    }

    private async Task<List<NwsAlert>> FetchAlertsAsync(string url)
    {
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("NWS request failed. Status={Status} URL={Url}", response.StatusCode, url);
            response.EnsureSuccessStatusCode();
        }
        var json = await response.Content.ReadAsStringAsync();
        return ParseAlerts(JsonDocument.Parse(json));
    }

    private List<NwsAlert> ParseAlerts(JsonDocument doc)
    {
        var alerts = new List<NwsAlert>();

        if (!doc.RootElement.TryGetProperty("features", out var features))
            return alerts;

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var props))
                continue;

            var alert = new NwsAlert
            {
                Id              = GetString(props, "id"),
                Event           = GetString(props, "event"),
                Headline        = GetString(props, "headline"),
                Description     = GetString(props, "description"),
                Instruction     = GetString(props, "instruction"),
                AreaDesc        = GetString(props, "areaDesc"),
                Severity        = GetString(props, "severity"),
                Urgency         = GetString(props, "urgency"),
                Certainty       = GetString(props, "certainty"),
                SenderName      = GetString(props, "senderName"),
                MessageType     = GetString(props, "messageType"),
                Sent            = GetDateTimeOffset(props, "sent"),
                Expires         = GetNullableDateTimeOffset(props, "expires"),
                Ends            = GetNullableDateTimeOffset(props, "ends"),
                DisplayTimeZone = _timeZone,
            };

            if (feature.TryGetProperty("geometry", out var geo) && geo.ValueKind != JsonValueKind.Null)
                alert.GeometryJson = geo.GetRawText();

            if (props.TryGetProperty("geocode", out var geocode) &&
                geocode.TryGetProperty("UGC", out var ugc) &&
                ugc.ValueKind == JsonValueKind.Array)
            {
                foreach (var code in ugc.EnumerateArray())
                {
                    var s = code.GetString();
                    if (!string.IsNullOrEmpty(s)) alert.GeocodeUgc.Add(s);
                }
            }

            alerts.Add(alert);
        }

        return alerts;
    }

    /// <summary>
    /// Appends the geographic filter to the query string.
    /// Zones and counties are combined — both use the NWS "zone" parameter and the API
    /// accepts a mixed list of UGC zone codes (MNZxxx) and county codes (MNCxxx).
    /// </summary>
    private void AddGeoFilter(List<string> qs)
    {
        var ugcCodes = _settings.Zones.Concat(_settings.Counties).ToList();

        if (ugcCodes.Count > 0)
            qs.Add($"zone={string.Join(",", ugcCodes)}");
        else if (!string.IsNullOrWhiteSpace(_settings.State))
            qs.Add($"area={_settings.State.ToUpper()}");
        else
            _logger.LogWarning("No geographic filter configured. Fetching nationwide alerts (high volume).");
    }

    /// <summary>
    /// Main query: geographic + severity/urgency/certainty/event filters.
    /// </summary>
    private string BuildUrl()
    {
        var qs = new List<string> { "status=actual", "message_type=alert,update,cancel" };

        AddGeoFilter(qs);

        if (!string.IsNullOrWhiteSpace(_settings.Severity))
            qs.Add($"severity={Uri.EscapeDataString(_settings.Severity)}");

        if (!string.IsNullOrWhiteSpace(_settings.Urgency))
            qs.Add($"urgency={Uri.EscapeDataString(_settings.Urgency)}");

        if (!string.IsNullOrWhiteSpace(_settings.Certainty))
            qs.Add($"certainty={Uri.EscapeDataString(_settings.Certainty)}");

        if (!string.IsNullOrWhiteSpace(_settings.EventTypes))
            qs.Add($"event={Uri.EscapeDataString(_settings.EventTypes)}");

        return "https://api.weather.gov/alerts/active?" + string.Join("&", qs);
    }

    /// <summary>
    /// Secondary query for AdditionalEventTypes: geographic filter + specific event types only,
    /// no severity filter, so Minor events are included regardless of the main Severity setting.
    /// </summary>
    private string BuildAdditionalUrl()
    {
        var qs = new List<string> { "status=actual", "message_type=alert,update,cancel" };

        AddGeoFilter(qs);

        qs.Add($"event={Uri.EscapeDataString(_settings.AdditionalEventTypes)}");

        return "https://api.weather.gov/alerts/active?" + string.Join("&", qs);
    }

    private static string GetString(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        return "";
    }

    private static DateTimeOffset GetDateTimeOffset(JsonElement el, string key)
    {
        var s = GetString(el, key);
        return DateTimeOffset.TryParse(s, out var dt) ? dt : DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonElement el, string key)
    {
        var s = GetString(el, key);
        return DateTimeOffset.TryParse(s, out var dt) ? dt : null;
    }
}
