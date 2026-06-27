using System.Text;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Sends SMS alerts via the Twilio REST API (no SDK — Basic auth over HTTPS).
/// API docs: https://www.twilio.com/docs/messaging/api/message-resource
/// Cost: ~$1.15/month for a number + ~$0.0079/SMS (US).
/// </summary>
public class TwilioService
{
    private readonly HttpClient _http;
    private readonly TwilioSettings _settings;
    private readonly ILogger<TwilioService> _logger;

    public TwilioService(HttpClient http, TwilioSettings settings, ILogger<TwilioService> logger)
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
        // Cache-bust the map URL so Twilio's servers don't serve a stale cached MMS image.
        return await SendToAllAsync(BuildSmsText(alert), alert.Event, CacheBust(alert.MapImageUrl, alert.Id));
    }

    private static string? CacheBust(string? url, string alertId)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string sep = url.Contains('?') ? "&" : "?";
        return url + $"{sep}_cb={Uri.EscapeDataString(alertId)}";
    }

    private async Task<bool> SendToAllAsync(string message, string label, string? mediaUrl = null)
    {
        if (!_settings.Enabled) return false;

        if (_settings.ToNumbers.Count == 0)
        {
            _logger.LogWarning("Twilio: No recipient numbers configured in ToNumbers.");
            return false;
        }

        // Keep SMS to 2 segments (320 chars) to control cost
        if (message.Length > 320) message = message[..317] + "...";

        var tasks = _settings.ToNumbers.Select(to => SendSmsAsync(to, message, label, mediaUrl));
        var results = await Task.WhenAll(tasks);
        return results.All(r => r);
    }

    private async Task<bool> SendSmsAsync(string toNumber, string message, string label, string? mediaUrl = null)
    {
        try
        {
            string url         = $"https://api.twilio.com/2010-04-01/Accounts/{_settings.AccountSid}/Messages.json";
            string credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_settings.AccountSid}:{_settings.AuthToken}"));

            var formFields = new List<KeyValuePair<string, string>>
            {
                new("From", _settings.FromNumber),
                new("To",   toNumber),
                new("Body", message),
            };
            // MediaUrl turns the message into MMS — Twilio fetches the URL itself, no upload needed.
            if (!string.IsNullOrEmpty(mediaUrl))
                formFields.Add(new("MediaUrl", mediaUrl));

            var formData = new FormUrlEncodedContent(formFields);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Basic {credentials}");
            request.Content = formData;

            var response = await _http.SendAsync(request);
            var body     = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Twilio: SMS sent to {Number} for {Label}.", toNumber, label);
                return true;
            }

            _logger.LogError("Twilio: SMS to {Number} failed. Status={Status} Body={Body}",
                toNumber, response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twilio: Exception sending SMS to {Number}.", toNumber);
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
