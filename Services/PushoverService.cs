using System.Text;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Sends push notifications via Pushover.
/// API docs: https://pushover.net/api
/// Priority 2 (emergency) repeats until acknowledged and bypasses Do Not Disturb.
/// </summary>
public class PushoverService
{
    private readonly HttpClient _http;
    private readonly PushoverSettings _settings;
    private readonly ILogger<PushoverService> _logger;

    private const string ApiUrl = "https://api.pushover.net/1/messages.json";

    public PushoverService(HttpClient http, PushoverSettings settings, ILogger<PushoverService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public bool IsEnabled => _settings.Enabled;
    public string MinSeverity => _settings.MinSeverity;
    public string EventTypes => _settings.EventTypes;
    public bool IncludeSpcOutlooks => _settings.IncludeSpcOutlooks;
    public bool IncludeSpcMcd     => _settings.IncludeSpcMcd;

    public Task<bool> SendConfirmationAsync(string message) =>
        SendAsync("✅ NWS Alert Bot", message, priority: 0, label: "confirmation");

    public async Task<bool> SendAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;

        int priority = alert.Severity.Equals("Extreme", StringComparison.OrdinalIgnoreCase)
            ? _settings.ExtremePriority
            : _settings.DefaultPriority;

        string title = $"⚠️ {alert.Event}";
        if (title.Length > 250) title = title[..250];

        return await SendAsync(title, BuildBody(alert), priority, alert.Event);
    }

    private async Task<bool> SendAsync(string title, string message, int priority, string label)
    {
        if (!_settings.Enabled) return false;

        try
        {
            // Pushover 1024-char body limit
            if (message.Length > 1024) message = message[..1021] + "...";

            var formData = new List<KeyValuePair<string, string>>
            {
                new("token",    _settings.ApiToken),
                new("user",     _settings.UserKey),
                new("title",    title),
                new("message",  message),
                new("priority", priority.ToString()),
            };

            if (!string.IsNullOrWhiteSpace(_settings.Sound))
                formData.Add(new("sound", _settings.Sound));

            if (priority == 2)
            {
                formData.Add(new("retry",  _settings.EmergencyRetrySeconds.ToString()));
                formData.Add(new("expire", _settings.EmergencyExpireSeconds.ToString()));
            }

            var response = await _http.PostAsync(ApiUrl, new FormUrlEncodedContent(formData));
            var body     = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Pushover: Sent {Label} [priority {Priority}].", label, priority);
                return true;
            }

            _logger.LogError("Pushover: Send failed. Status={Status} Body={Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pushover: Exception sending {Label}.", label);
            return false;
        }
    }

    private static string BuildBody(NwsAlert alert)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(alert.AreaDesc)) sb.AppendLine(alert.AreaDesc);
        var expiresAt = alert.Ends ?? alert.Expires;
        if (expiresAt.HasValue) sb.AppendLine($"Until: {expiresAt.Value.ToLocalTime():ddd h:mm tt zzz}");
        if (!string.IsNullOrWhiteSpace(alert.Instruction)) { sb.AppendLine(); sb.Append(alert.Instruction); }
        return sb.ToString().Trim();
    }
}
