using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;

namespace NwsAlertBot.Services;

/// <summary>
/// Checks GitHub Releases for a newer version of this app. Entirely gated by
/// <see cref="UpdateSettings.AutoApply"/> — when false, this makes no GitHub API calls at all.
/// When true, checks on the configured interval and, if a newer version is found, launches
/// <c>scripts/update.ps1</c> (expected alongside the executable) to download, install, and
/// restart it, then shuts this process down so the script can replace it.
/// </summary>
public class UpdateCheckService
{
    private readonly HttpClient _http;
    private readonly UpdateSettings _settings;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<UpdateCheckService> _logger;

    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;

    public UpdateCheckService(
        HttpClient http,
        UpdateSettings settings,
        IHostApplicationLifetime appLifetime,
        ILogger<UpdateCheckService> logger)
    {
        _http = http;
        _settings = settings;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    /// <summary>
    /// Checks for a newer release if AutoApply is on and the configured interval has elapsed
    /// since the last check; a no-op otherwise. Safe to call every poll cycle — self-throttles
    /// internally.
    /// </summary>
    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (!_settings.AutoApply) return;
        if (!IsCheckDue(_lastChecked, DateTimeOffset.UtcNow, _settings.CheckIntervalHours)) return;
        _lastChecked = DateTimeOffset.UtcNow;

        try
        {
            var currentVersion = GetCurrentVersion();
            if (currentVersion == null)
            {
                _logger.LogWarning("Update check: could not determine the running version; skipping.");
                return;
            }

            var latestTag = await GetLatestReleaseTagAsync(ct);
            if (latestTag == null) return; // already logged by GetLatestReleaseTagAsync

            var latestVersion = ParseVersion(latestTag);
            if (latestVersion == null)
            {
                _logger.LogWarning("Update check: could not parse release tag {Tag}; skipping.", latestTag);
                return;
            }

            if (latestVersion <= currentVersion)
            {
                _logger.LogInformation("Update check: running {Current}, latest release is {Latest} — up to date.",
                    currentVersion, latestVersion);
                return;
            }

            _logger.LogWarning("Update check: newer version available ({Latest}; currently running {Current}). Installing...",
                latestVersion, currentVersion);

            if (!LaunchUpdater(latestTag))
                return; // already logged by LaunchUpdater

            _logger.LogWarning("Update: launched updater for {Tag}; shutting down so it can replace this process.", latestTag);
            _appLifetime.StopApplication();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed: {Error}", ex.Message);
        }
    }

    /// <summary>True if at least <paramref name="intervalHours"/> have passed since <paramref name="lastChecked"/>.</summary>
    internal static bool IsCheckDue(DateTimeOffset lastChecked, DateTimeOffset now, int intervalHours) =>
        now - lastChecked >= TimeSpan.FromHours(intervalHours);

    /// <summary>Parses a GitHub release tag (e.g. "v1.2.3") into a comparable Version, or null if malformed.</summary>
    internal static Version? ParseVersion(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var version) ? version : null;

    internal static Version? GetCurrentVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    private async Task<string?> GetLatestReleaseTagAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{_settings.GitHubRepo}/releases/latest");
        request.Headers.Add("User-Agent", "NwsAlertBot-UpdateChecker");
        request.Headers.Add("Accept", "application/vnd.github+json");

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Update check: GitHub API returned {Status} for {Repo}.",
                response.StatusCode, _settings.GitHubRepo);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
    }

    /// <summary>
    /// Launches scripts/update.ps1 (expected next to the executable) as an independent process —
    /// it outlives this one once <see cref="IHostApplicationLifetime.StopApplication"/> is called
    /// below, since child processes aren't tied to their parent's lifetime on Windows or Linux.
    /// Returns false (and logs) if the script can't be found or fails to start.
    /// </summary>
    private bool LaunchUpdater(string tag)
    {
        string installDir = AppContext.BaseDirectory;
        string scriptPath = Path.Combine(installDir, "update.ps1");

        if (!File.Exists(scriptPath))
        {
            _logger.LogError("Update: scripts/update.ps1 not found next to the executable ({Path}); cannot auto-apply. " +
                "Download it from the release you're running and place it alongside the executable.", scriptPath);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-Repo");
            psi.ArgumentList.Add(_settings.GitHubRepo);
            psi.ArgumentList.Add("-Tag");
            psi.ArgumentList.Add(tag);
            psi.ArgumentList.Add("-WaitForPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("-InstallDir");
            psi.ArgumentList.Add(installDir);
            psi.ArgumentList.Add("-ServiceName");
            psi.ArgumentList.Add(_settings.ServiceName);

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update: failed to launch scripts/update.ps1 ({Error}). Is PowerShell (pwsh) installed?", ex.Message);
            return false;
        }
    }
}
