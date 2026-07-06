using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Polls the NWS text products API for the Hazardous Weather Outlook (HWO) issued by the
/// WFO(s) covering the monitored area (derived from Location.Zones/Location.Counties). HWO is a
/// plain-text product — no polygon, no map image — issued 1-2x/day per office. This service
/// fetches the latest issuance per WFO and returns it as a synthetic NwsAlert with the raw
/// teletype formatting (header codes, UGC zone list, mid-sentence line wraps) cleaned up.
///
/// Delivery is opt-in per platform via IncludeHwo, since the full product text is long and
/// intended primarily for personal use rather than short-form platforms like X/Bluesky.
/// Docs: https://www.weather.gov/media/directives/010_docs/pd01005017curr.pdf
/// API: https://api.weather.gov/products/types/HWO/locations/{wfo}
/// </summary>
public class HwoService
{
    private readonly HttpClient _http;
    private readonly HwoSettings _settings;
    private readonly LocationSettings _location;
    private readonly NwsZoneService _zones;
    private readonly ILogger<HwoService> _logger;
    private readonly TimeZoneInfo _timeZone;

    private List<string>? _wfos;
    private DateTimeOffset _lastCheckedUtc = DateTimeOffset.MinValue;

    // Raw teletype header codes: a bare line count ("000"), the WMO abbreviated heading
    // ("FLUS43 KMPX 011906"), or the AFOS PIL alone ("HWOMPX") — all short, all-caps, no prose.
    private static readonly Regex HeaderCodeLineRegex = new(
        @"^(\d{3}|[A-Z]{4}\d{2}\s+[A-Z]{4}\s+\d{6}|[A-Z]{3,6})$", RegexOptions.Compiled);

    // UGC zone/county code list header, e.g. "MNZ041>045-047>070-...".
    private static readonly Regex UgcLineRegex = new(
        @"^[A-Z]{2}[ZC]\d{3}", RegexOptions.Compiled);

    private static readonly Regex OfficeNameRegex = new(
        @"National Weather Service (.+)", RegexOptions.Compiled);

    public bool IsEnabled => _settings.Enabled;

    public HwoService(HttpClient http, HwoSettings settings, LocationSettings location, NwsZoneService zones, ILogger<HwoService> logger)
    {
        _http = http;
        _settings = settings;
        _location = location;
        _zones = zones;
        _logger = logger;
        _timeZone = ResolveTimeZone(location.TimeZone, logger);
    }

    private static TimeZoneInfo ResolveTimeZone(string id, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { logger.LogWarning("Hwo: Unknown TimeZone \"{Id}\"; falling back to America/Chicago.", id); }
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch
        {
            logger.LogWarning("Hwo: Could not load America/Chicago as fallback timezone; using UTC.");
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Returns one synthetic alert per monitored WFO holding its latest Hazardous Weather
    /// Outlook issuance. Returns an empty list (no HTTP calls) if called before
    /// CheckIntervalSeconds has elapsed since the last check. Dedup against repeat issuances
    /// is handled by AlertTrackerService via the product's NWS UUID (embedded in alert.Id).
    /// </summary>
    public async Task<List<NwsAlert>> GetHwoAlertsAsync()
    {
        if (!_settings.Enabled) return new();

        if (DateTimeOffset.UtcNow - _lastCheckedUtc < TimeSpan.FromSeconds(_settings.CheckIntervalSeconds))
            return new();
        _lastCheckedUtc = DateTimeOffset.UtcNow;

        var wfos = await EnsureWfosResolvedAsync();
        if (wfos.Count == 0)
        {
            _logger.LogWarning("Hwo: No WFOs resolved from Location.Zones/Location.Counties. Skipping HWO check.");
            return new();
        }

        var fetchTasks = wfos.Select(FetchLatestAsync).ToArray();
        var fetched = await Task.WhenAll(fetchTasks);
        return fetched.Where(a => a != null).Select(a => a!).ToList();
    }

    private async Task<List<string>> EnsureWfosResolvedAsync()
    {
        if (_wfos != null) return _wfos;

        var codes = _location.Zones.Concat(_location.Counties).ToList();
        var wfos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in codes)
        {
            var info = await _zones.GetZoneInfoAsync(code);
            if (info?.Cwa == null)
            {
                _logger.LogWarning("Hwo: Could not resolve WFO for {Code}; skipping.", code);
                continue;
            }
            wfos.Add(info.Cwa);
        }

        _wfos = wfos.ToList();

        if (_wfos.Count > 0)
            _logger.LogInformation("Hwo: Resolved {Count} WFO(s) for HWO checks: {Wfos}.", _wfos.Count, string.Join(", ", _wfos));
        else
            _logger.LogWarning("Hwo: No WFOs resolved. Zones and Counties must be configured; State-only config is not supported for HWO.");

        return _wfos;
    }

    private async Task<NwsAlert?> FetchLatestAsync(string wfo)
    {
        try
        {
            var response = await _http.GetAsync($"https://api.weather.gov/products/types/HWO/locations/{wfo}");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Hwo: Product listing for {Wfo} returned {Status}.", wfo, response.StatusCode);
                return null;
            }

            using var listDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!listDoc.RootElement.TryGetProperty("@graph", out var graph) || graph.GetArrayLength() == 0)
                return null;

            var latest = graph[0];
            if (!latest.TryGetProperty("id", out var idEl)) return null;
            var uuid = idEl.GetString();
            if (string.IsNullOrEmpty(uuid)) return null;

            var issuanceTime = DateTimeOffset.UtcNow;
            if (latest.TryGetProperty("issuanceTime", out var timeEl))
            {
                var timeStr = timeEl.GetString() ?? "";
                DateTimeOffset.TryParse(timeStr, null,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out issuanceTime);
            }

            var text = await FetchProductTextAsync(uuid);
            if (text == null) return null;

            var officeName = ParseOfficeName(text);

            return new NwsAlert
            {
                Id              = $"HWO-{wfo}-{uuid}",
                Event           = "Hazardous Weather Outlook",
                Headline        = $"Hazardous Weather Outlook — {officeName ?? wfo}",
                AreaDesc        = officeName ?? wfo,
                Severity        = "Unknown",
                SenderName      = officeName != null ? $"National Weather Service {officeName}" : "National Weather Service",
                Sent            = issuanceTime,
                HwoText         = CleanHwoText(text),
                DetailsUrl      = $"https://api.weather.gov/products/{uuid}",
                IsHwo           = true,
                DisplayTimeZone = _timeZone,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hwo: Failed to fetch latest HWO for {Wfo}.", wfo);
            return null;
        }
    }

    private async Task<string?> FetchProductTextAsync(string uuid)
    {
        try
        {
            var response = await _http.GetAsync($"https://api.weather.gov/products/{uuid}");
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("productText", out var el) ? el.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hwo: Failed to fetch product {Uuid}.", uuid);
            return null;
        }
    }

    private static string? ParseOfficeName(string text)
    {
        var m = OfficeNameRegex.Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Strips raw teletype formatting from an HWO product's text: the leading WMO/AFOS header
    /// codes, the UGC zone/county code list block, and the trailing "$$" terminator, then
    /// collapses mid-sentence line wraps within each remaining paragraph into single lines
    /// while preserving paragraph/section breaks.
    /// </summary>
    private static string CleanHwoText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var normalized = raw.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        var paragraphs = Regex.Split(normalized, @"\n\s*\n+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0);

        var kept = new List<string>();
        foreach (var para in paragraphs)
        {
            var firstLine = para.Split('\n')[0].Trim();

            if (para is "$$" or "&&") continue;                    // product terminator / segment marker
            if (HeaderCodeLineRegex.IsMatch(firstLine)) continue;   // raw WMO/AFOS header codes
            if (UgcLineRegex.IsMatch(firstLine)) continue;          // UGC zone/county code list

            bool isTitleBlock = firstLine.Equals("Hazardous Weather Outlook", StringComparison.OrdinalIgnoreCase);
            kept.Add(isTitleBlock ? para : CollapseLineWraps(para));
        }

        return string.Join("\n\n", kept);
    }

    private static string CollapseLineWraps(string paragraph) =>
        Regex.Replace(paragraph, @"[ \t]*\n[ \t]*", " ").Trim();
}
