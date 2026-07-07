using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Posts to Mastodon via the Mastodon REST API.
/// Endpoint: POST /api/v1/statuses
/// API docs: https://docs.joinmastodon.org/methods/statuses/
/// Character limit: 500 (default; varies by instance)
/// </summary>
public class MastodonService
{
    private readonly HttpClient _http;
    private readonly MastodonSettings _settings;
    private readonly ILogger<MastodonService> _logger;

    private const int CharLimit = 500;

    public MastodonService(HttpClient http, MastodonSettings settings, ILogger<MastodonService> logger)
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
        PostStatusAsync(message.Length > CharLimit ? message[..(CharLimit - 3)] + "..." : message, "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        return await PostStatusAsync(alert.FormatPost(maxLength: CharLimit), alert.Event, alert.MapImageBytes);
    }

    private async Task<bool> PostStatusAsync(string status, string label, byte[]? imageBytes = null)
    {
        if (!_settings.Enabled) return false;

        try
        {
            string instanceUrl = _settings.InstanceUrl.TrimEnd('/');

            string? mediaId = imageBytes != null
                ? await UploadMediaAsync(instanceUrl, imageBytes, label)
                : null;

            var formFields = new List<KeyValuePair<string, string>>
            {
                new("status",     status),
                new("visibility", "public")
            };
            if (mediaId != null)
                formFields.Add(new("media_ids[]", mediaId));

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{instanceUrl}/api/v1/statuses");
            request.Headers.Add("Authorization", $"Bearer {_settings.AccessToken}");
            request.Content = new FormUrlEncodedContent(formFields);

            var response = await _http.SendAsync(request);
            var body     = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Mastodon: Posted {Label}.", label);
                return true;
            }

            _logger.LogError("Mastodon: Post failed. Status={Status} Body={Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mastodon: Exception posting {Label}.", label);
            return false;
        }
    }

    /// <summary>
    /// Uploads an image via the media endpoint and returns its id (to attach via media_ids[]
    /// on the status), or null on failure (the status is still posted as text-only).
    /// </summary>
    private async Task<string?> UploadMediaAsync(string instanceUrl, byte[] imageBytes, string label)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "file", "map.png");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{instanceUrl}/api/v2/media");
            request.Headers.Add("Authorization", $"Bearer {_settings.AccessToken}");
            request.Content = content;

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mastodon: Media upload failed for {Label}. Status={Status} Body={Body}",
                    label, response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mastodon: Exception uploading media for {Label}.", label);
            return null;
        }
    }
}
