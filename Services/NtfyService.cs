using System.Text;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Sends push notifications via ntfy (https://ntfy.sh).
/// API docs: https://docs.ntfy.sh/publish/
/// Priority 5 (urgent) bypasses Do Not Disturb on Android.
/// </summary>
public class NtfyService
{
    private readonly HttpClient _http;
    private readonly NtfySettings _settings;
    private readonly ILogger<NtfyService> _logger;

    public NtfyService(HttpClient http, NtfySettings settings, ILogger<NtfyService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public bool IsEnabled => _settings.Enabled;
    public string MinSeverity => _settings.MinSeverity;
    public string EventTypes => _settings.EventTypes;

    public Task<bool> SendConfirmationAsync(string message) =>
        SendAsync("✅ NWS Alert Bot — Connected", message, priority: 3, tags: "white_check_mark", label: "confirmation");

    public async Task<bool> SendAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;

        int priority = alert.Severity.Equals("Extreme", StringComparison.OrdinalIgnoreCase)
            ? _settings.ExtremePriority
            : _settings.DefaultPriority;

        return await SendAsync(
            title:    EscapeHeader($"⚠️ {alert.Event}"),
            message:  BuildBody(alert),
            priority: priority,
            tags:     GetTags(alert),
            label:    alert.Event);
    }

    private async Task<bool> SendAsync(string title, string message, int priority, string tags, string label)
    {
        if (!_settings.Enabled) return false;

        if (string.IsNullOrWhiteSpace(_settings.Topic))
        {
            _logger.LogWarning("Ntfy: Topic is not configured. Skipping {Label}.", label);
            return false;
        }

        try
        {
            string serverUrl = _settings.ServerUrl.TrimEnd('/');
            string url       = $"{serverUrl}/{_settings.Topic}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            if (!string.IsNullOrWhiteSpace(_settings.Username))
            {
                string creds = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));
                request.Headers.Add("Authorization", $"Basic {creds}");
            }

            request.Headers.Add("Title",    EscapeHeader(title));
            request.Headers.Add("Priority", priority.ToString());
            request.Headers.Add("Tags",     tags);
            request.Content = new StringContent(message, Encoding.UTF8, "text/plain");

            var response = await _http.SendAsync(request);
            var body     = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Ntfy: Sent {Label} [priority {Priority}].", label, priority);
                return true;
            }

            _logger.LogError("Ntfy: Send failed. Status={Status} Body={Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ntfy: Exception sending {Label}.", label);
            return false;
        }
    }

    private static string BuildBody(NwsAlert alert)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(alert.AreaDesc)) sb.AppendLine(alert.AreaDesc);
        var expiresAt = alert.Ends ?? alert.Expires;
        if (expiresAt.HasValue) sb.AppendLine($"Until: {expiresAt.Value.ToLocalTime():ddd h:mm tt zzz}");
        if (!string.IsNullOrWhiteSpace(alert.Instruction)) { sb.AppendLine(); sb.AppendLine(alert.Instruction); }
        if (!string.IsNullOrWhiteSpace(alert.SenderName)) sb.AppendLine($"— {alert.SenderName}");
        return sb.ToString().Trim();
    }

    private static string GetTags(NwsAlert alert)
    {
        var tags = new List<string>();
        tags.Add(alert.Severity?.ToLower() switch
        {
            "extreme"  => "rotating_light",
            "severe"   => "warning",
            "moderate" => "yellow_circle",
            _          => "information_source"
        });
        string ev = alert.Event?.ToLower() ?? "";
        if      (ev.Contains("tornado"))                                     tags.Add("tornado");
        else if (ev.Contains("flood"))                                       tags.Add("droplet");
        else if (ev.Contains("thunderstorm"))                                tags.Add("zap");
        else if (ev.Contains("winter") || ev.Contains("snow") || ev.Contains("blizzard")) tags.Add("snowflake");
        else if (ev.Contains("wind"))                                        tags.Add("wind_face");
        else if (ev.Contains("heat"))                                        tags.Add("thermometer");
        else if (ev.Contains("fire"))                                        tags.Add("fire");
        else if (ev.Contains("fog"))                                         tags.Add("fog");
        else if (ev.Contains("freeze") || ev.Contains("frost") || ev.Contains("ice")) tags.Add("ice_cube");
        return string.Join(",", tags);
    }

    private static string EscapeHeader(string value) =>
        new(value.Where(c => c < 128).ToArray());
}
