using System.Text;
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

    public Task<bool> SendConfirmationAsync(string message) =>
        PostStatusAsync(message.Length > CharLimit ? message[..(CharLimit - 3)] + "..." : message, "confirmation");

    public async Task<bool> PostAlertAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return false;
        return await PostStatusAsync(alert.FormatPost(maxLength: CharLimit), alert.Event);
    }

    private async Task<bool> PostStatusAsync(string status, string label)
    {
        if (!_settings.Enabled) return false;

        try
        {
            var formData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("status",     status),
                new KeyValuePair<string, string>("visibility", "public")
            });

            string instanceUrl = _settings.InstanceUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{instanceUrl}/api/v1/statuses");
            request.Headers.Add("Authorization", $"Bearer {_settings.AccessToken}");
            request.Content = formData;

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
}
