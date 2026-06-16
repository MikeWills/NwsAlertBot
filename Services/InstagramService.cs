using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Posts to Instagram via the Instagram Graph API.
/// Two-step process: create media container, then publish it.
/// Requires an image — text-only posts are not supported by Instagram.
/// API docs: https://developers.facebook.com/docs/instagram-platform/content-publishing
/// </summary>
public class InstagramService
{
    private readonly HttpClient _http;
    private readonly InstagramSettings _settings;
    private readonly ILogger<InstagramService> _logger;

    private const string GraphApiVersion = "v25.0";

    public InstagramService(HttpClient http, InstagramSettings settings, ILogger<InstagramService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public bool IsEnabled => _settings.Enabled;
    public string MinSeverity => _settings.MinSeverity;
    public string EventTypes => _settings.EventTypes;

    public Task<bool> SendConfirmationAsync(string message) =>
        PostCaptionAsync(message, "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        var imageUrl = alert.MapImageUrl ?? _settings.ImageUrl;
        return await PostCaptionAsync(alert.FormatPost(maxLength: 2200), alert.Event, imageUrl);
    }

    private async Task<bool> PostCaptionAsync(string caption, string label, string? imageUrl = null)
    {
        if (!_settings.Enabled) return false;

        imageUrl ??= _settings.ImageUrl;

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            _logger.LogWarning("Instagram: No image URL available. Instagram requires an image. Skipping {Label}.", label);
            return false;
        }

        try
        {
            string containerId = await CreateMediaContainerAsync(caption, imageUrl);
            if (string.IsNullOrEmpty(containerId)) return false;
            return await PublishContainerAsync(containerId, label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Instagram: Exception posting {Label}.", label);
            return false;
        }
    }

    private async Task<string> CreateMediaContainerAsync(string caption, string imageUrl)
    {
        var url = $"https://graph.facebook.com/{GraphApiVersion}/{_settings.InstagramAccountId}/media";

        var payload = new
        {
            image_url = imageUrl,
            caption,
            access_token = _settings.PageAccessToken
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Instagram: Container creation failed. Status={Status} Body={Body}",
                response.StatusCode, body);
            return "";
        }

        var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
    }

    private async Task<bool> PublishContainerAsync(string containerId, string label)
    {
        var url = $"https://graph.facebook.com/{GraphApiVersion}/{_settings.InstagramAccountId}/media_publish";

        var payload = new
        {
            creation_id = containerId,
            access_token = _settings.PageAccessToken
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Instagram: Posted {Label}.", label);
            return true;
        }

        _logger.LogError("Instagram: Publish failed. Status={Status} Body={Body}",
            response.StatusCode, body);
        return false;
    }
}
