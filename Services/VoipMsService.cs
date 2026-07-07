using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Sends SMS alerts via the VoIP.ms REST API (no SDK — GET request with query parameters).
/// API docs: https://voip.ms/m/apidocs.php (method: sendSMS)
/// Message body limited to 160 characters (single SMS segment).
///
/// VoIP.ms's documented sample code uses GET with query-string parameters, not POST with a
/// form body — confirmed by reproducing an identical generic SOAP "Bad Request" fault (HTTP 500,
/// content-type application/soap+xml) for every POST attempt regardless of credentials, message
/// content/encoding, or source IP allowlist status, while the equivalent GET request succeeds.
/// Do not change this back to POST without re-verifying against a live account.
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
    public bool IncludeSpcMcd     => _settings.IncludeSpcMcd;
    public bool IncludeHwo        => _settings.IncludeHwo;
    public bool IncludeEro        => _settings.IncludeEro;

    public Task<bool> SendConfirmationAsync(string message) =>
        SendToAllAsync(message, "confirmation");

    // Single SMS segment (GSM-7).
    private const int MaxSmsLength = 160;

    public async Task<bool> SendAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        return await SendToAllAsync(BuildSmsText(alert, MaxSmsLength), alert.Event);
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

        // Non-ASCII content (emoji, em-dashes, smart quotes) forces UCS-2 SMS encoding at a
        // ~70-char segment limit instead of GSM-7's 160 — and VoIP.ms's API has been observed
        // rejecting such content outright with a generic "Bad Request" SOAP fault rather than a
        // normal error response. Normalize to plain ASCII before the 160-char truncation below,
        // which only holds for GSM-7 content.
        message = SanitizeForSms(message);
        // Safety net only -- BuildSmsText already fits alert messages within MaxSmsLength field
        // by field. This plain truncation only applies to confirmation messages (plain strings).
        if (message.Length > MaxSmsLength) message = message[..(MaxSmsLength - 3)] + "...";

        var tasks = _settings.ToNumbers.Select(to => SendSmsAsync(to, message, label));
        var results = await Task.WhenAll(tasks);
        return results.All(r => r);
    }

    private async Task<bool> SendSmsAsync(string toNumber, string message, string label)
    {
        try
        {
            // GET with query parameters — see class remarks for why this isn't POST.
            var qs = string.Join("&", new[]
            {
                $"api_username={Uri.EscapeDataString(_settings.ApiUsername)}",
                $"api_password={Uri.EscapeDataString(_settings.ApiPassword)}",
                $"method=sendSMS",
                $"did={Uri.EscapeDataString(_settings.Did)}",
                $"dst={Uri.EscapeDataString(toNumber)}",
                $"message={Uri.EscapeDataString(message)}",
            });

            // The credentials are in the URL for this request (required by VoIP.ms's API), so
            // HttpClient's own request-URL logging is suppressed for this service in Program.cs
            // (System.Net.Http.HttpClient.VoipMsService -> Warning) to keep them out of logs.
            var response = await _http.GetAsync($"{ApiUrl}?{qs}");
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

    /// <summary>
    /// Normalizes smart-punctuation to ASCII equivalents and strips anything else non-ASCII
    /// (emoji, etc.). VoIP.ms's sendSMS API has been observed rejecting Unicode message content
    /// outright with a generic "Bad Request" SOAP fault instead of a normal error response.
    /// </summary>
    private static string SanitizeForSms(string text)
    {
        text = text
            .Replace('—', '-')                            // em dash —
            .Replace('–', '-')                             // en dash –
            .Replace('‘', '\'').Replace('’', '\'')   // smart single quotes
            .Replace('“', '"').Replace('”', '"');    // smart double quotes

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c < 128) sb.Append(c);

        return sb.ToString().Trim();
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
