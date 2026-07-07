using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Posts to X (Twitter) via API v2 using OAuth 1.0a with HMAC-SHA1.
/// API docs: https://developer.twitter.com/en/docs/twitter-api/tweets/manage-tweets/api-reference/post-tweets
/// Rate limits: Free tier = 500 posts/month. Basic tier ($100/mo) = 3,000/month.
/// Character limit: 280
/// </summary>
public class XService
{
    private readonly HttpClient _http;
    private readonly XSettings _settings;
    private readonly ILogger<XService> _logger;

    private const string PostTweetUrl = "https://api.twitter.com/2/tweets";
    private const string MediaUploadUrl = "https://upload.twitter.com/1.1/media/upload.json";

    public XService(HttpClient http, XSettings settings, ILogger<XService> logger)
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
        PostTextAsync(message.Length > 280 ? message[..277] + "..." : message, "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        return await PostTextAsync(alert.FormatPost(maxLength: 280), alert.Event, alert.MapImageBytes);
    }

    private async Task<bool> PostTextAsync(string text, string label, byte[]? imageBytes = null)
    {
        if (!_settings.Enabled) return false;

        try
        {
            string? mediaId = imageBytes != null ? await UploadMediaAsync(imageBytes, label) : null;

            object tweetBody = mediaId != null
                ? new { text, media = new { media_ids = new[] { mediaId } } }
                : new { text };

            var authHeader = BuildOAuth1Header("POST", PostTweetUrl);
            var payload = JsonSerializer.Serialize(tweetBody);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, PostTweetUrl);
            request.Headers.Add("Authorization", authHeader);
            request.Content = content;

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("X: Posted {Label}.", label);
                return true;
            }

            _logger.LogError("X: Post failed. Status={Status} Body={Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "X: Exception posting {Label}.", label);
            return false;
        }
    }

    /// <summary>
    /// Uploads an image to the v1.1 media endpoint and returns its media_id_string, or null on
    /// failure (the tweet is still posted as text-only in that case). Posting media still goes
    /// through this v1.1 endpoint even for v2 tweets — X has not migrated media upload to v2.
    /// Multipart fields are not part of the OAuth1.0a signature base for this endpoint, so the
    /// same header-only signing used for the tweet itself applies here too.
    /// </summary>
    private async Task<string?> UploadMediaAsync(byte[] imageBytes, string label)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageBytes), "media", "map.png");

            var authHeader = BuildOAuth1Header("POST", MediaUploadUrl);
            using var request = new HttpRequestMessage(HttpMethod.Post, MediaUploadUrl);
            request.Headers.Add("Authorization", authHeader);
            request.Content = content;

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("X: Media upload failed for {Label}. Status={Status} Body={Body}",
                    label, response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("media_id_string", out var id) ? id.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "X: Exception uploading media for {Label}.", label);
            return null;
        }
    }

    private string BuildOAuth1Header(string method, string url)
    {
        string nonce = Convert.ToBase64String(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var oauthParams = new SortedDictionary<string, string>
        {
            ["oauth_consumer_key"]     = _settings.ApiKey,
            ["oauth_nonce"]            = nonce,
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"]        = timestamp,
            ["oauth_token"]            = _settings.AccessToken,
            ["oauth_version"]          = "1.0"
        };

        string paramString = string.Join("&",
            oauthParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        string signatureBase = $"{method}&{Uri.EscapeDataString(url)}&{Uri.EscapeDataString(paramString)}";
        string signingKey = $"{Uri.EscapeDataString(_settings.ApiSecret)}&{Uri.EscapeDataString(_settings.AccessTokenSecret)}";

        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(signingKey));
        string signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(signatureBase)));
        oauthParams["oauth_signature"] = signature;

        return "OAuth " + string.Join(", ",
            oauthParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}=\"{Uri.EscapeDataString(kv.Value)}\""));
    }
}
