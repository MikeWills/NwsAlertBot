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

    public Task<bool> SendConfirmationAsync(string message) =>
        PostTextAsync(message.Length > CharLimit ? message[..(CharLimit - 3)] + "..." : message, "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        return await PostTextAsync(alert.FormatPost(maxLength: CharLimit), alert.Event);
    }

    private async Task<bool> PostTextAsync(string text, string label)
    {
        if (!_settings.Enabled) return false;

        try
        {
            if (string.IsNullOrEmpty(_accessJwt))
                await AuthenticateAsync();

            if (string.IsNullOrEmpty(_accessJwt)) return false;

            bool success = await CreatePostAsync(text, label);

            // Retry once on auth failure
            if (!success)
            {
                _accessJwt = "";
                await AuthenticateAsync();
                success = await CreatePostAsync(text, label);
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

    private async Task<bool> CreatePostAsync(string text, string label)
    {
        var payload = JsonSerializer.Serialize(new
        {
            repo       = _did,
            collection = "app.bsky.feed.post",
            record     = new
            {
                @type     = "app.bsky.feed.post",
                text,
                createdAt = DateTimeOffset.UtcNow.ToString("o")
            }
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
            return true;
        }

        _logger.LogError("Bluesky: Post failed. Status={Status} Body={Body}", response.StatusCode, body);
        return false;
    }
}
