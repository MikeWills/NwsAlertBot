using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Polls the WPC Winter Storm Severity Index (WSSI) ArcGIS MapServer and checks the monitored
/// locations (centroids of Location.Zones/Location.Counties) against the impact polygons for all
/// five components (Overall, Snow Amount, Snow Load, Ice Accumulation, Blowing Snow) on Days 1-3.
/// Produces at most one combined synthetic NwsAlert listing the highest impact per component.
/// Data source: https://mapservices.weather.noaa.gov/vector/rest/services/outlooks/wpc_wssi/MapServer
/// </summary>
public class WpcWssiService
{
    private readonly HttpClient _http;
    private readonly WssiSettings _settings;
    private readonly LocationSettings _location;
    private readonly NwsZoneService _zones;
    private readonly ILogger<WpcWssiService> _logger;
    private readonly TimeZoneInfo _timeZone;

    private const string MapServerBase = "https://mapservices.weather.noaa.gov/vector/rest/services/outlooks/wpc_wssi/MapServer";

    // Day 1/2/3 feature layer IDs per component (0/5/10/15/23 are group layers with no features).
    private static readonly (string DisplayName, int[] LayerIds)[] Components =
    {
        ("Overall",          new[] { 1, 2, 3 }),
        ("Snow Amount",      new[] { 6, 7, 8 }),
        ("Snow Load",        new[] { 11, 12, 13 }),
        ("Ice Accumulation", new[] { 16, 17, 18 }),
        ("Blowing Snow",     new[] { 24, 25, 26 }),
    };

    // Ascending severity; index = rank.
    internal static readonly string[] ImpactLevels = { "WINTER WEATHER AREA", "MINOR", "MODERATE", "MAJOR", "EXTREME" };

    internal record WssiHit(string Component, string Impact, string? IssueTime, string? StartTime, string? EndTime);

    private List<(double Lat, double Lon, string? Wfo)>? _locations;
    private DateTimeOffset _lastCheckedUtc = DateTimeOffset.MinValue;

    public WpcWssiService(HttpClient http, WssiSettings settings, LocationSettings location, NwsZoneService zones, ILogger<WpcWssiService> logger)
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
            catch { logger.LogWarning("Wssi: Unknown TimeZone \"{Id}\"; falling back to America/Chicago.", id); }
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch
        {
            logger.LogWarning("Wssi: Could not load America/Chicago as fallback timezone; using UTC.");
            return TimeZoneInfo.Utc;
        }
    }

    public bool IsEnabled => _settings.Enabled;

    /// <summary>
    /// Returns zero or one synthetic alert. No HTTP calls if CheckIntervalSeconds hasn't elapsed.
    /// </summary>
    public async Task<List<NwsAlert>> GetWssiAlertsAsync()
    {
        if (!_settings.Enabled) return new();

        if (DateTimeOffset.UtcNow - _lastCheckedUtc < TimeSpan.FromSeconds(_settings.CheckIntervalSeconds))
            return new();
        _lastCheckedUtc = DateTimeOffset.UtcNow;

        var locations = await EnsureLocationsResolvedAsync();
        if (locations.Count == 0)
        {
            _logger.LogWarning("Wssi: No monitored locations resolved from Location.Zones/Location.Counties. Skipping WSSI check.");
            return new();
        }

        int minRank = GetImpactRank(_settings.MinImpact);
        if (minRank < 0)
        {
            _logger.LogWarning("Wssi: Unknown MinImpact \"{MinImpact}\"; using \"Minor\".", _settings.MinImpact);
            minRank = GetImpactRank("Minor");
        }

        var geometry = BuildMultipointGeometry(locations.Select(l => (l.Lon, l.Lat)));
        var hits = new List<WssiHit>();

        foreach (var (component, layerIds) in Components)
        {
            foreach (int layerId in layerIds)
            {
                var json = await QueryLayerAsync(layerId, geometry);
                if (json != null)
                    hits.AddRange(ParseFeatures(json, component));
            }
        }

        string? wfo = locations.Select(l => l.Wfo).FirstOrDefault(w => !string.IsNullOrEmpty(w));
        var alert = BuildAlert(hits, minRank, wfo, _timeZone);
        if (alert == null && hits.Any(h => GetImpactRank(h.Impact) >= minRank))
            _logger.LogWarning("Wssi: Impact found but issue_time was unparseable; skipping.");

        return alert == null ? new() : new List<NwsAlert> { alert };
    }

    private async Task<string?> QueryLayerAsync(int layerId, string geometry)
    {
        var url = $"{MapServerBase}/{layerId}/query" +
                  $"?geometry={Uri.EscapeDataString(geometry)}" +
                  "&geometryType=esriGeometryMultipoint&inSR=4326" +
                  "&spatialRel=esriSpatialRelIntersects" +
                  "&outFields=impact,issue_time,start_time,end_time" +
                  "&returnGeometry=false&f=json";

        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Wssi: Layer {LayerId} query returned {Status}.", layerId, response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wssi: Failed to query layer {LayerId}.", layerId);
            return null;
        }
    }

    private async Task<List<(double Lat, double Lon, string? Wfo)>> EnsureLocationsResolvedAsync()
    {
        if (_locations != null) return _locations;

        var resolved = new List<(double Lat, double Lon, string? Wfo)>();
        foreach (var code in _location.Zones.Concat(_location.Counties))
        {
            var info = await _zones.GetZoneInfoAsync(code);
            var centroid = info == null ? null : PolygonGeometry.ComputeCentroid(info.Geometry);
            if (centroid == null)
            {
                _logger.LogWarning("Wssi: Could not resolve a centroid for {Code}; this location will not be monitored.", code);
                continue;
            }
            resolved.Add((centroid.Value.Lat, centroid.Value.Lon, info!.Cwa));
        }

        _locations = resolved;
        if (resolved.Count > 0)
            _logger.LogInformation("Wssi: Resolved {Count} monitored location(s) for WSSI checks.", resolved.Count);
        return resolved;
    }

    internal static string BuildMultipointGeometry(IEnumerable<(double Lon, double Lat)> points)
    {
        var coords = string.Join(",", points.Select(p =>
            $"[{p.Lon.ToString(CultureInfo.InvariantCulture)},{p.Lat.ToString(CultureInfo.InvariantCulture)}]"));
        return $"{{\"points\":[{coords}],\"spatialReference\":{{\"wkid\":4326}}}}";
    }

    /// <summary>Parses an ArcGIS query response. Returns no hits for an error body or empty feature list.</summary>
    internal static List<WssiHit> ParseFeatures(string json, string component)
    {
        var hits = new List<WssiHit>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("features", out var features)) return hits;

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("attributes", out var a)) continue;
            var impact = GetString(a, "impact");
            if (string.IsNullOrEmpty(impact)) continue;
            hits.Add(new WssiHit(component, impact, GetString(a, "issue_time"), GetString(a, "start_time"), GetString(a, "end_time")));
        }
        return hits;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Builds the combined alert from every hit at or above minRank, or null if none qualify.
    /// The ID is day + highest level, not issue_time: WSSI re-issues every 2 hours, so keying on
    /// issue_time would repost the same storm ~12x a day. This posts once per day and again only
    /// when the highest impact level changes.
    /// </summary>
    internal static NwsAlert? BuildAlert(IEnumerable<WssiHit> hits, int minRank, string? wfo, TimeZoneInfo timeZone)
    {
        var qualifying = hits.Where(h => GetImpactRank(h.Impact) >= minRank).ToList();
        if (qualifying.Count == 0) return null;

        var perComponent = qualifying
            .GroupBy(h => h.Component)
            .Select(g => g.OrderByDescending(h => GetImpactRank(h.Impact)).First())
            .OrderByDescending(h => GetImpactRank(h.Impact))
            .ToList();

        var top = perComponent[0];
        int topRank = GetImpactRank(top.Impact);

        var issued = qualifying.Select(h => ParseIssueTime(h.IssueTime)).FirstOrDefault(t => t != null);
        if (issued == null) return null;

        var latestEnd = qualifying.Select(h => ParseWssiTime(h.EndTime)).Where(t => t != null).Max();

        string severity = topRank switch
        {
            4 => "Extreme",
            3 => "Severe",
            2 => "Moderate",
            _ => "Minor",
        };

        string detailsUrl = string.IsNullOrEmpty(wfo)
            ? "https://www.wpc.ncep.noaa.gov/wwd/wssi/wssi.php"
            : $"https://www.wpc.ncep.noaa.gov/wwd/wssi/wssi.php?id={wfo}";

        string summary = string.Join("\n", perComponent.Select(h => $"{h.Component}: {FormatImpactLabel(h.Impact)}"));

        return new NwsAlert
        {
            Id              = $"WPC-WSSI-{issued.Value:yyyyMMdd}-{ImpactLevels[topRank].Replace(' ', '_')}",
            Event           = "WPC Winter Storm Severity Index",
            Headline        = $"{FormatImpactLabel(top.Impact)} — Winter Storm Severity Index",
            AreaDesc        = "Monitored Area",
            Severity        = severity,
            SenderName      = "NOAA Weather Prediction Center",
            Instruction     = $"{summary}\nFor more details: {detailsUrl}",
            Sent            = issued.Value,
            Expires         = latestEnd,
            MapImageUrl     = string.IsNullOrEmpty(wfo) ? null : $"https://www.wpc.ncep.noaa.gov/wwd/wssi/images/WSSI_OVERALL_{wfo}.png",
            DetailsUrl      = detailsUrl,
            IsWssi          = true,
            DisplayTimeZone = timeZone,
        };
    }

    internal static int GetImpactRank(string? impact)
    {
        if (string.IsNullOrWhiteSpace(impact)) return -1;
        return Array.FindIndex(ImpactLevels, l => l.Equals(impact.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal static string FormatImpactLabel(string impact) => impact.Trim().ToUpperInvariant() switch
    {
        "WINTER WEATHER AREA" => "Winter Weather Area",
        "MINOR"               => "Minor Impacts",
        "MODERATE"            => "Moderate Impacts",
        "MAJOR"               => "Major Impacts",
        "EXTREME"             => "Extreme Impacts",
        _                     => impact,
    };

    // issue_time, e.g. "2026-11-22 0114Z" (UTC).
    internal static DateTimeOffset? ParseIssueTime(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParseExact(s.Trim(), "yyyy-MM-dd HHmm'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

    // start_time/end_time, e.g. "00Z 11/22/26" (UTC).
    internal static DateTimeOffset? ParseWssiTime(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParseExact(s.Trim(), "HH'Z' MM/dd/yy", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
}
