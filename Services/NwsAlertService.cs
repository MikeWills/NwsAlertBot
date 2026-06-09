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
/// </summary>
public class NwsAlertService
{
    private readonly HttpClient _http;
    private readonly NwsSettings _settings;
    private readonly ILogger<NwsAlertService> _logger;

    public NwsAlertService(HttpClient http, NwsSettings settings, ILogger<NwsAlertService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<List<NwsAlert>> GetActiveAlertsAsync()
    {
        try
        {
            string url = BuildUrl();
            _logger.LogDebug("NWS request URL: {Url}", url);

            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            var alerts = new List<NwsAlert>();

            if (!doc.RootElement.TryGetProperty("features", out var features))
                return alerts;

            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var props))
                    continue;

                var alert = new NwsAlert
                {
                    Id          = GetString(props, "id"),
                    Event       = GetString(props, "event"),
                    Headline    = GetString(props, "headline"),
                    Description = GetString(props, "description"),
                    Instruction = GetString(props, "instruction"),
                    AreaDesc    = GetString(props, "areaDesc"),
                    Severity    = GetString(props, "severity"),
                    Urgency     = GetString(props, "urgency"),
                    Certainty   = GetString(props, "certainty"),
                    SenderName  = GetString(props, "senderName"),
                    MessageType = GetString(props, "messageType"),
                    Sent        = GetDateTimeOffset(props, "sent"),
                    Expires     = GetNullableDateTimeOffset(props, "expires"),
                    Ends        = GetNullableDateTimeOffset(props, "ends"),
                };

                alerts.Add(alert);
            }

            _logger.LogInformation("NWS returned {Count} qualifying active alerts.", alerts.Count);
            return alerts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch NWS alerts.");
            return new List<NwsAlert>();
        }
    }

    /// <summary>
    /// Builds the NWS API URL, pushing as many filters as possible to the server.
    /// Priority: Zones > Counties > State > Nationwide (avoid nationwide — very noisy)
    /// </summary>
    private string BuildUrl()
    {
        var qs = new List<string>
        {
            "status=actual",
            "message_type=alert"
        };

        // Geographic filter — zones take priority over counties over state
        if (_settings.Zones.Count > 0)
        {
            qs.Add($"zone={string.Join(",", _settings.Zones)}");
        }
        else if (_settings.Counties.Count > 0)
        {
            qs.Add($"zone={string.Join(",", _settings.Counties)}");
        }
        else if (!string.IsNullOrWhiteSpace(_settings.State))
        {
            qs.Add($"area={_settings.State.ToUpper()}");
        }
        else
        {
            _logger.LogWarning("No geographic filter configured. Fetching nationwide alerts (high volume).");
        }

        // Severity filter — e.g. "Severe,Extreme"
        if (!string.IsNullOrWhiteSpace(_settings.Severity))
            qs.Add($"severity={Uri.EscapeDataString(_settings.Severity)}");

        // Urgency filter — e.g. "Immediate,Expected"
        if (!string.IsNullOrWhiteSpace(_settings.Urgency))
            qs.Add($"urgency={Uri.EscapeDataString(_settings.Urgency)}");

        // Certainty filter — e.g. "Observed,Likely"
        if (!string.IsNullOrWhiteSpace(_settings.Certainty))
            qs.Add($"certainty={Uri.EscapeDataString(_settings.Certainty)}");

        // Event type filter — e.g. "Tornado Warning,Flash Flood Warning"
        if (!string.IsNullOrWhiteSpace(_settings.EventTypes))
            qs.Add($"event={Uri.EscapeDataString(_settings.EventTypes)}");

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
