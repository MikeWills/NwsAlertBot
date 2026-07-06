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
    public bool IncludeSpcMcd     => _settings.IncludeSpcMcd;
    public bool IncludeHwo        => _settings.IncludeHwo;

    public Task<bool> SendConfirmationAsync(string message) =>
        SendToAllAsync(message, "confirmation");

    // Keep SMS to 2 segments to control cost.
    private const int MaxSmsLength = 320;

    public async Task<bool> SendAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        // Cache-bust the map URL so Twilio's servers don't serve a stale cached MMS image.
        return await SendToAllAsync(BuildSmsText(alert, MaxSmsLength), alert.Event, CacheBust(alert.MapImageUrl, alert.Id));
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

        // Safety net only -- BuildSmsText already fits alert messages within MaxSmsLength field
        // by field. This plain truncation only applies to confirmation messages (plain strings).
        if (message.Length > MaxSmsLength) message = message[..(MaxSmsLength - 3)] + "...";

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

    /// <summary>
    /// Builds the SMS body, fitting it within maxLength one field at a time: the header always
    /// appears, and each of area/until/instruction is included only if it fits whole -- never
    /// truncated mid-field (which would otherwise show a useless fragment like "Until: ...").
    /// The details link is reserved for last and always kept if it fits at all.
    /// </summary>
    private static string BuildSmsText(NwsAlert alert, int maxLength)
    {
        string header = $"NWS ALERT: {alert.Event}";

        // SPC MCD/Outlook embed the details link at the end of Instruction so platforms that
        // only render Instruction (no separate DetailsUrl line) still show it. SMS appends the
        // link separately below, so strip a trailing duplicate here rather than showing it twice.
        string instruction = alert.Instruction;
        if (!string.IsNullOrWhiteSpace(alert.DetailsUrl) && !string.IsNullOrWhiteSpace(instruction))
        {
            string suffix = "\n" + alert.DetailsUrl;
            if (instruction.EndsWith(suffix, StringComparison.Ordinal))
                instruction = instruction[..^suffix.Length];
        }

        var expiresAt = alert.Ends ?? alert.Expires;
        string detailsLine = !string.IsNullOrWhiteSpace(alert.DetailsUrl) ? $"\nDetails: {alert.DetailsUrl}" : "";

        string[] optionalLines =
        {
            !string.IsNullOrWhiteSpace(alert.AreaDesc) ? $"\n{alert.AreaDesc}" : "",
            expiresAt.HasValue ? $"\nUntil: {expiresAt.Value.ToLocalTime():ddd h:mm tt zzz}" : "",
            !string.IsNullOrWhiteSpace(instruction) ? $"\n{instruction}" : "",
        };

        string body = header;
        int budget = maxLength - detailsLine.Length;
        foreach (var line in optionalLines)
        {
            if (line.Length > 0 && body.Length + line.Length <= budget)
                body += line;
        }

        string result = body + detailsLine;
        return result.Length <= maxLength ? result : result[..(maxLength - 3)] + "...";
    }
}
