using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Posts alerts to a Discord channel via an Incoming Webhook.
/// API docs: https://discord.com/developers/docs/resources/webhook#execute-webhook
/// </summary>
public class DiscordService
{
    private const int EmbedDescriptionLimit = 4096;

    private readonly HttpClient _http;
    private readonly DiscordSettings _settings;
    private readonly ILogger<DiscordService> _logger;

    public DiscordService(HttpClient http, DiscordSettings settings, ILogger<DiscordService> logger)
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
        SendAsync(content: message, embed: null, label: "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;

        var embed = new Dictionary<string, object?>
        {
            ["title"]       = Truncate($"{alert.Event} — {alert.AreaDesc}", 256),
            ["description"] = alert.FormatPost(EmbedDescriptionLimit),
            ["color"]       = GetColor(alert.Severity),
        };
        if (!string.IsNullOrWhiteSpace(alert.SenderName))
            embed["footer"] = new { text = alert.SenderName };

        return await SendAsync(content: null, embed: embed, label: alert.Event, imageBytes: alert.MapImageBytes);
    }

    private async Task<bool> SendAsync(string? content, Dictionary<string, object?>? embed, string label, byte[]? imageBytes = null)
    {
        if (!_settings.Enabled) return false;

        var urls = _settings.WebhookUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0)
        {
            _logger.LogWarning("Discord: No WebhookUrls configured. Skipping {Label}.", label);
            return false;
        }

        var tasks = urls.Select(url => PostToWebhookAsync(url, content, embed, label, imageBytes));
        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    private async Task<bool> PostToWebhookAsync(string webhookUrl, string? content, Dictionary<string, object?>? embed, string label, byte[]? imageBytes)
    {
        try
        {
            HttpResponseMessage response;

            if (imageBytes != null)
            {
                var embedWithImage = new Dictionary<string, object?>(embed ?? new())
                {
                    ["image"] = new { url = "attachment://map.png" }
                };

                var payloadObj = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(content)) payloadObj["content"] = content;
                payloadObj["embeds"] = new[] { embedWithImage };
                if (!string.IsNullOrWhiteSpace(_settings.Username)) payloadObj["username"] = _settings.Username;

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(JsonSerializer.Serialize(payloadObj), Encoding.UTF8, "application/json"), "payload_json");
                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(imageContent, "files[0]", "map.png");

                response = await _http.PostAsync(webhookUrl, form);
            }
            else
            {
                var payload = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(content)) payload["content"] = content;
                if (embed is not null) payload["embeds"] = new[] { embed };
                if (!string.IsNullOrWhiteSpace(_settings.Username)) payload["username"] = _settings.Username;

                var json = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                response = await _http.SendAsync(request);
            }

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord: Posted {Label}.", label);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Discord: Post failed. Status={Status} Body={Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord: Exception posting {Label}.", label);
            return false;
        }
    }

    private static int GetColor(string? severity) => severity?.ToLower() switch
    {
        "extreme"  => 0xE53935, // red
        "severe"   => 0xFB8C00, // orange
        "moderate" => 0xFDD835, // yellow
        "minor"    => 0x43A047, // green
        _          => 0x757575  // grey
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
