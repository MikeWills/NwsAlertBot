using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Posts to Bluesky via the AT Protocol REST API.
/// API docs: https://docs.bsky.app/docs/get-started
/// Use an App Password, not your main password.
/// Character limit: 300
/// </summary>
public class BlueskyService
{
    private readonly HttpClient _http;
    private readonly BlueskySettings _settings;
    private readonly ILogger<BlueskyService> _logger;

    private const string PdsHost   = "https://bsky.social";
    private const int    CharLimit  = 300;

    private string _accessJwt = "";
    private string _did       = "";

    public BlueskyService(HttpClient http, BlueskySettings settings, ILogger<BlueskyService> logger)
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
        PostTextAsync(message.Length > CharLimit ? message[..(CharLimit - 3)] + "..." : message, "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        return await PostTextAsync(alert.FormatPost(maxLength: CharLimit), alert.Event, alert.MapImageBytes);
    }

    private async Task<bool> PostTextAsync(string text, string label, byte[]? imageBytes = null)
    {
        if (!_settings.Enabled) return false;

        try
        {
            if (string.IsNullOrEmpty(_accessJwt))
                await AuthenticateAsync();

            if (string.IsNullOrEmpty(_accessJwt)) return false;

            var (success, isAuthFailure) = await CreatePostAsync(text, label, imageBytes);

            // Retry once only on auth expiry (401), not on rate limits or content errors
            if (!success && isAuthFailure)
            {
                _accessJwt = "";
                await AuthenticateAsync();
                if (!string.IsNullOrEmpty(_accessJwt))
                    (success, _) = await CreatePostAsync(text, label, imageBytes);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bluesky: Exception posting {Label}.", label);
            return false;
        }
    }

    private async Task AuthenticateAsync()
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                identifier = _settings.Handle,
                password   = _settings.AppPassword
            });

            var content  = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{PdsHost}/xrpc/com.atproto.server.createSession", content);
            var body     = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Bluesky: Auth failed. Status={Status} Body={Body}", response.StatusCode, body);
                return;
            }

            var doc    = JsonDocument.Parse(body);
            _accessJwt = doc.RootElement.GetProperty("accessJwt").GetString() ?? "";
            _did       = doc.RootElement.GetProperty("did").GetString() ?? "";
            _logger.LogInformation("Bluesky: Authenticated as {Handle}.", _settings.Handle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bluesky: Exception during authentication.");
        }
    }

    private async Task<(bool Success, bool IsAuthFailure)> CreatePostAsync(string text, string label, byte[]? imageBytes)
    {
        var record = new Dictionary<string, object?>
        {
            ["$type"]     = "app.bsky.feed.post",
            ["text"]      = text,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("o"),
        };

        if (imageBytes != null)
        {
            var blob = await UploadBlobAsync(imageBytes, label);
            if (blob != null)
            {
                record["embed"] = new Dictionary<string, object?>
                {
                    ["$type"]  = "app.bsky.embed.images",
                    ["images"] = new object[] { new Dictionary<string, object?> { ["image"] = blob.Value, ["alt"] = "" } }
                };
            }
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["repo"]       = _did,
            ["collection"] = "app.bsky.feed.post",
            ["record"]     = record
        });

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{PdsHost}/xrpc/com.atproto.repo.createRecord");
        request.Headers.Add("Authorization", $"Bearer {_accessJwt}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var body     = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Bluesky: Posted {Label}.", label);
            return (true, false);
        }

        _logger.LogError("Bluesky: Post failed. Status={Status} Body={Body}", response.StatusCode, body);
        return (false, response.StatusCode == System.Net.HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Uploads image bytes to the PDS via uploadBlob and returns the resulting blob reference
    /// (to be embedded in the post record's "embed.images[].image" field), or null on failure.
    /// </summary>
    private async Task<JsonElement?> UploadBlobAsync(byte[] imageBytes, string label)
    {
        try
        {
            using var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{PdsHost}/xrpc/com.atproto.repo.uploadBlob");
            request.Headers.Add("Authorization", $"Bearer {_accessJwt}");
            request.Content = content;

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bluesky: Blob upload failed for {Label}. Status={Status} Body={Body}",
                    label, response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("blob", out var blob) ? blob.Clone() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bluesky: Exception uploading blob for {Label}.", label);
            return null;
        }
    }
}
