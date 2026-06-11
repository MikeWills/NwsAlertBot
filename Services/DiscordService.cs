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

    public Task<bool> SendConfirmationAsync(string message) =>
        SendAsync(message, embed: null, label: "confirmation");

    public Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return Task.FromResult(false);

        var embed = new
        {
            title       = Truncate($"{alert.Event} — {alert.AreaDesc}", 256),
            description = alert.FormatPost(EmbedDescriptionLimit),
            color       = GetColor(alert.Severity),
            footer      = !string.IsNullOrWhiteSpace(alert.SenderName)
                ? new { text = alert.SenderName }
                : null
        };

        return SendAsync(content: null, embed: embed, label: alert.Event);
    }

    private async Task<bool> SendAsync(string? content, object? embed, string label)
    {
        if (!_settings.Enabled) return false;

        if (string.IsNullOrWhiteSpace(_settings.WebhookUrl))
        {
            _logger.LogWarning("Discord: WebhookUrl is not configured. Skipping {Label}.", label);
            return false;
        }

        try
        {
            var payload = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(content)) payload["content"] = content;
            if (embed is not null) payload["embeds"] = new[] { embed };
            if (!string.IsNullOrWhiteSpace(_settings.Username)) payload["username"] = _settings.Username;

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, _settings.WebhookUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await _http.SendAsync(request);

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
