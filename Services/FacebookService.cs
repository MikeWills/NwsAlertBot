using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Posts to a Facebook Page via the Meta Graph API.
/// API docs: https://developers.facebook.com/docs/pages/publishing
/// Requires: pages_manage_posts permission + long-lived Page Access Token
/// Graph API version: v25.0 (Feb 2026)
/// </summary>
public class FacebookService
{
    private readonly HttpClient _http;
    private readonly FacebookSettings _settings;
    private readonly ILogger<FacebookService> _logger;

    private const string GraphApiVersion = "v25.0";

    public FacebookService(HttpClient http, FacebookSettings settings, ILogger<FacebookService> logger)
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

    public Task<bool> SendConfirmationAsync(string message) =>
        PostMessageAsync(message, "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;

        return await PostMessageAsync(alert.FormatPost(maxLength: 63206), alert.Event, alert.MapImageBytes);
    }

    private async Task<bool> PostMessageAsync(string message, string label, byte[]? imageBytes = null)
    {
        if (!_settings.Enabled) return false;

        try
        {
            HttpResponseMessage response;

            if (imageBytes != null)
            {
                // Post to /photos with the image as multipart source so the map renders inline.
                var url = $"https://graph.facebook.com/{GraphApiVersion}/{_settings.PageId}/photos";

                using var form = new MultipartFormDataContent();
                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(imageContent, "source", "map.png");
                form.Add(new StringContent(message), "caption");
                form.Add(new StringContent(_settings.PageAccessToken), "access_token");

                response = await _http.PostAsync(url, form);
            }
            else
            {
                var url = $"https://graph.facebook.com/{GraphApiVersion}/{_settings.PageId}/feed";
                var payload = new { message, access_token = _settings.PageAccessToken };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                response = await _http.PostAsync(url, content);
            }

            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Facebook: Posted {Label}.", label);
                return true;
            }

            _logger.LogError("Facebook: Post failed. Status={Status} Body={Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Facebook: Exception posting {Label}.", label);
            return false;
        }
    }
}
