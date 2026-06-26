using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Sends SMS alerts via the VoIP.ms REST API (no SDK — API key in query string over HTTPS).
/// API docs: https://voip.ms/m/apidocs.php (method: sendSMS)
/// Message body limited to 160 characters (single SMS segment).
/// </summary>
public class VoipMsService
{
    private const string ApiUrl = "https://voip.ms/api/v1/rest.php";

    private readonly HttpClient _http;
    private readonly VoipMsSettings _settings;
    private readonly ILogger<VoipMsService> _logger;

    public VoipMsService(HttpClient http, VoipMsSettings settings, ILogger<VoipMsService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public bool IsEnabled => _settings.Enabled;
    public string MinSeverity => _settings.MinSeverity;
    public string EventTypes => _settings.EventTypes;
    public bool IncludeSpcOutlooks => _settings.IncludeSpcOutlooks;

    public Task<bool> SendConfirmationAsync(string message) =>
        SendToAllAsync(message, "confirmation");

    public async Task<bool> SendAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        return await SendToAllAsync(BuildSmsText(alert), alert.Event);
    }

    private async Task<bool> SendToAllAsync(string message, string label)
    {
        if (!_settings.Enabled) return false;

        if (string.IsNullOrWhiteSpace(_settings.Did))
        {
            _logger.LogWarning("VoipMs: Did is not configured.");
            return false;
        }

        if (_settings.ToNumbers.Count == 0)
        {
            _logger.LogWarning("VoipMs: No recipient numbers configured in ToNumbers.");
            return false;
        }

        // Keep SMS to a single segment (160 chars)
        if (message.Length > 160) message = message[..157] + "...";

        var tasks = _settings.ToNumbers.Select(to => SendSmsAsync(to, message, label));
        var results = await Task.WhenAll(tasks);
        return results.All(r => r);
    }

    private async Task<bool> SendSmsAsync(string toNumber, string message, string label)
    {
        try
        {
            // POST credentials in the body so they don't appear in URLs logged by HttpClient infrastructure
            var formFields = new List<KeyValuePair<string, string>>
            {
                new("api_username", _settings.ApiUsername),
                new("api_password", _settings.ApiPassword),
                new("method",       "sendSMS"),
                new("did",          _settings.Did),
                new("dst",          toNumber),
                new("message",      message),
            };

            var response = await _http.PostAsync(ApiUrl, new FormUrlEncodedContent(formFields));
            var body     = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                string? status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

                if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("VoipMs: SMS sent to {Number} for {Label}.", toNumber, label);
                    return true;
                }

                _logger.LogError("VoipMs: SMS to {Number} failed. Status={Status} Body={Body}",
                    toNumber, status, body);
                return false;
            }

            _logger.LogError("VoipMs: SMS to {Number} failed. HttpStatus={Status} Body={Body}",
                toNumber, response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VoipMs: Exception sending SMS to {Number}.", toNumber);
            return false;
        }
    }

    private static string BuildSmsText(NwsAlert alert)
    {
        var sb = new StringBuilder();
        sb.Append($"NWS ALERT: {alert.Event}");
        if (!string.IsNullOrWhiteSpace(alert.AreaDesc)) sb.Append($"\n{alert.AreaDesc}");
        var expiresAt = alert.Ends ?? alert.Expires;
        if (expiresAt.HasValue) sb.Append($"\nUntil: {expiresAt.Value.ToLocalTime():ddd h:mm tt zzz}");
        if (!string.IsNullOrWhiteSpace(alert.Instruction)) sb.Append($"\n{alert.Instruction}");
        return sb.ToString();
    }
}
