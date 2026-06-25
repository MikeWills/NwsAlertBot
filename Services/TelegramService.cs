using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Sends alerts to a Telegram chat or channel via the Bot API.
/// API docs: https://core.telegram.org/bots/api
/// </summary>
public class TelegramService
{
    private const int MessageLimit = 4096;
    private const int CaptionLimit = 1024;

    private readonly HttpClient _http;
    private readonly TelegramSettings _settings;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(HttpClient http, TelegramSettings settings, ILogger<TelegramService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public bool IsEnabled => _settings.Enabled;
    public string MinSeverity => _settings.MinSeverity;
    public string EventTypes => _settings.EventTypes;

    public Task<bool> SendConfirmationAsync(string message) =>
        SendAsync(message, photoUrl: null, label: "confirmation");

    public Task<bool> SendAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return Task.FromResult(false);

        bool hasPhoto = !string.IsNullOrEmpty(alert.MapImageUrl);
        string text = alert.FormatPost(hasPhoto ? CaptionLimit : MessageLimit);
        return SendAsync(text, alert.MapImageUrl, label: alert.Event);
    }

    private async Task<bool> SendAsync(string text, string? photoUrl, string label)
    {
        if (!_settings.Enabled) return false;

        if (string.IsNullOrWhiteSpace(_settings.BotToken) || string.IsNullOrWhiteSpace(_settings.ChatId))
        {
            _logger.LogWarning("Telegram: BotToken or ChatId is not configured. Skipping {Label}.", label);
            return false;
        }

        try
        {
            bool hasPhoto = !string.IsNullOrWhiteSpace(photoUrl);
            string method = hasPhoto ? "sendPhoto" : "sendMessage";
            string url = $"https://api.telegram.org/bot{_settings.BotToken}/{method}";

            var payload = new Dictionary<string, object?> { ["chat_id"] = _settings.ChatId };
            if (hasPhoto)
            {
                payload["photo"] = photoUrl;
                payload["caption"] = text;
            }
            else
            {
                payload["text"] = text;
                payload["disable_web_page_preview"] = true;
            }

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await _http.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Telegram: Sent {Label}.", label);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Telegram: Send failed. Status={Status} Body={Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram: Exception sending {Label}.", label);
            return false;
        }
    }
}
