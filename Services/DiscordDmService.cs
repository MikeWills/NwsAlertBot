using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Sends alerts as Discord DMs to one or more users via the Discord Bot API.
/// API docs: https://discord.com/developers/docs/resources/channel#create-message
/// </summary>
public class DiscordDmService
{
    private const string ApiBase = "https://discord.com/api/v10";
    private const int EmbedDescriptionLimit = 4096;

    private readonly HttpClient _http;
    private readonly DiscordDmSettings _settings;
    private readonly ILogger<DiscordDmService> _logger;

    // Cache userId → DM channelId so we only call CreateDM once per user per session.
    private readonly ConcurrentDictionary<string, string> _channelCache = new();

    public DiscordDmService(HttpClient http, DiscordDmSettings settings, ILogger<DiscordDmService> logger)
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
        SendToAllUsersAsync(content: message, embed: null, label: "confirmation");

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

        return await SendToAllUsersAsync(content: null, embed: embed, label: alert.Event, imageBytes: alert.MapImageBytes);
    }

    private async Task<bool> SendToAllUsersAsync(string? content, Dictionary<string, object?>? embed, string label, byte[]? imageBytes = null)
    {
        if (!_settings.Enabled) return false;

        if (string.IsNullOrWhiteSpace(_settings.BotToken))
        {
            _logger.LogWarning("DiscordDm: BotToken is not configured. Skipping {Label}.", label);
            return false;
        }

        if (_settings.UserIds.Count == 0)
        {
            _logger.LogWarning("DiscordDm: No UserIds configured. Skipping {Label}.", label);
            return false;
        }

        var tasks = _settings.UserIds.Select(userId => SendToUserAsync(userId, content, embed, label, imageBytes));
        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    private async Task<bool> SendToUserAsync(string userId, string? content, Dictionary<string, object?>? embed, string label, byte[]? imageBytes)
    {
        try
        {
            var channelId = await GetOrCreateDmChannelAsync(userId);
            if (channelId == null) return false;

            string messageUrl = $"{ApiBase}/channels/{channelId}/messages";
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

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(JsonSerializer.Serialize(payloadObj), Encoding.UTF8, "application/json"), "payload_json");
                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(imageContent, "files[0]", "map.png");

                using var request = new HttpRequestMessage(HttpMethod.Post, messageUrl) { Content = form };
                request.Headers.Add("Authorization", $"Bot {_settings.BotToken}");
                response = await _http.SendAsync(request);
            }
            else
            {
                var payload = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(content)) payload["content"] = content;
                if (embed is not null) payload["embeds"] = new[] { embed };

                var json = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, messageUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Authorization", $"Bot {_settings.BotToken}");
                response = await _http.SendAsync(request);
            }

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("DiscordDm: Sent {Label} to user {UserId}.", label, userId);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("DiscordDm: Message to user {UserId} failed. Status={Status} Body={Body}",
                userId, response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DiscordDm: Exception sending {Label} to user {UserId}.", label, userId);
            return false;
        }
    }

    private async Task<string?> GetOrCreateDmChannelAsync(string userId)
    {
        if (_channelCache.TryGetValue(userId, out var cached)) return cached;

        try
        {
            var body = JsonSerializer.Serialize(new { recipient_id = userId });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/users/@me/channels")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Bot {_settings.BotToken}");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("DiscordDm: Could not open DM channel for user {UserId}. Status={Status} Body={Body}",
                    userId, response.StatusCode, err);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var channelId = doc.RootElement.GetProperty("id").GetString();
            if (!string.IsNullOrEmpty(channelId))
                _channelCache[userId] = channelId;

            return channelId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DiscordDm: Exception opening DM channel for user {UserId}.", userId);
            return null;
        }
    }

    private static int GetColor(string? severity) => severity?.ToLower() switch
    {
        "extreme"  => 0xE53935,
        "severe"   => 0xFB8C00,
        "moderate" => 0xFDD835,
        "minor"    => 0x43A047,
        _          => 0x757575
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
