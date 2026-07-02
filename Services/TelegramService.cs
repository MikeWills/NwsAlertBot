using System.Net.Http.Headers;
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
    public bool IncludeSpcOutlooks => _settings.IncludeSpcOutlooks;
    public bool IncludeSpcMcd     => _settings.IncludeSpcMcd;
    public bool IncludeHwo        => _settings.IncludeHwo;

    public Task<bool> SendConfirmationAsync(string message) =>
        SendMessageAsync(message, "confirmation");

    public Task<bool> SendAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return Task.FromResult(false);
        return PostAlertInternalAsync(alert);
    }

    private Task<bool> PostAlertInternalAsync(NwsAlert alert)
    {
        if (alert.MapImageBytes != null)
            return SendPhotoAsync(alert.MapImageBytes, alert.FormatPost(CaptionLimit), alert.Event);

        return SendMessageAsync(alert.FormatPost(MessageLimit), alert.Event);
    }

    private async Task<bool> SendPhotoAsync(byte[] imageBytes, string caption, string label)
    {
        if (!ValidateConfig(label)) return false;

        try
        {
            string url = $"https://api.telegram.org/bot{_settings.BotToken}/sendPhoto";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(_settings.ChatId), "chat_id");
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(imageContent, "photo", "map.png");
            form.Add(new StringContent(caption), "caption");

            var response = await _http.PostAsync(url, form);

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

    private async Task<bool> SendMessageAsync(string text, string label)
    {
        if (!ValidateConfig(label)) return false;

        try
        {
            string url = $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage";

            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = _settings.ChatId,
                ["text"] = text,
                ["disable_web_page_preview"] = true
            };

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

    private bool ValidateConfig(string label)
    {
        if (string.IsNullOrWhiteSpace(_settings.BotToken) || string.IsNullOrWhiteSpace(_settings.ChatId))
        {
            _logger.LogWarning("Telegram: BotToken or ChatId is not configured. Skipping {Label}.", label);
            return false;
        }
        return true;
    }
}
