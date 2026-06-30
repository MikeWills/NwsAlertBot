using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Polls the NWS products API for SPC Mesoscale Discussions (MCDs) and checks
/// each active MCD polygon against the monitored area's zone centroids. Produces
/// a synthetic NwsAlert for each MCD that is currently active and covers at least
/// one monitored location.
///
/// MCDs are issued from KWNS as product type "SWO" on the NWS API. They contain
/// a LAT...LON polygon in 8-digit DDHHMM-encoded pairs (first 4 digits = lat*100 N,
/// last 4 = lon*100 W) and a "Valid DDHHMM Z - DDHHMM Z" line for the active window.
/// Image URL: https://www.spc.noaa.gov/products/md/{year}/mcd{NNNN}.png
/// </summary>
public class SpcMcdService
{
    private readonly HttpClient _http;
    private readonly SpcMcdSettings _settings;
    private readonly NwsSettings _nwsSettings;
    private readonly NwsZoneService _zones;
    private readonly ILogger<SpcMcdService> _logger;
    private readonly TimeZoneInfo _timeZone;

    private List<(string Code, double Lat, double Lon)>? _locations;
    private DateTimeOffset _lastCheckedUtc = DateTimeOffset.MinValue;
    // Avoid re-fetching products we've already processed (MCD or non-MCD)
    private readonly HashSet<string> _checkedUuids = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex McdNumberRegex = new(
        @"Mesoscale Discussion\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ValidTimeRegex = new(
        @"Valid\s+(\d{6})Z\s*-\s*(\d{6})Z", RegexOptions.Compiled);
    private static readonly Regex LatLonRegex = new(
        @"LAT\.\.\.LON\s+([\d\s]+?)(?=\n\n|\n[A-Z]|\z)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex AreasAffectedRegex = new(
        @"Areas affected\.\.\.(.*?)(?=\n\nConcerning|\nConcerning)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ConcerningRegex = new(
        @"Concerning\.\.\.(.*?)(?=\n\nValid|\nValid)", RegexOptions.Compiled | RegexOptions.Singleline);

    public bool IsEnabled => _settings.Enabled;

    public SpcMcdService(
        HttpClient http,
        SpcMcdSettings settings,
        NwsSettings nwsSettings,
        NwsZoneService zones,
        ILogger<SpcMcdService> logger)
    {
        _http = http;
        _settings = settings;
        _nwsSettings = nwsSettings;
        _zones = zones;
        _logger = logger;
        _timeZone = ResolveTimeZone(nwsSettings.TimeZone, logger);
    }

    private static TimeZoneInfo ResolveTimeZone(string id, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { logger.LogWarning("SpcMcd: Unknown TimeZone \"{Id}\"; falling back to America/Chicago.", id); }
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch
        {
            logger.LogWarning("SpcMcd: Could not load America/Chicago; using UTC.");
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Returns synthetic alerts for any active SPC Mesoscale Discussion whose polygon
    /// covers at least one monitored zone/county centroid. Returns an empty list when
    /// called before CheckIntervalSeconds has elapsed since the last check.
    /// </summary>
    public async Task<List<NwsAlert>> GetMcdAlertsAsync()
    {
        if (!_settings.Enabled) return new();

        if (DateTimeOffset.UtcNow - _lastCheckedUtc < TimeSpan.FromSeconds(_settings.CheckIntervalSeconds))
            return new();
        _lastCheckedUtc = DateTimeOffset.UtcNow;

        var locations = await EnsureLocationsAsync();
        if (locations.Count == 0)
        {
            _logger.LogWarning("SpcMcd: No monitored locations resolved; skipping MCD check.");
            return new();
        }

        var results = new List<NwsAlert>();
        try
        {
            var listing = await FetchProductListingAsync();
            var newUuids = listing
                .Where(p => !_checkedUuids.Contains(p.Uuid))
                .Select(p => p.Uuid)
                .ToList();

            if (newUuids.Count > 0)
            {
                var fetchTasks = newUuids
                    .Select(uuid => FetchAndProcessAsync(uuid, locations))
                    .ToArray();
                var processed = await Task.WhenAll(fetchTasks);
                foreach (var alert in processed.Where(a => a != null))
                    results.Add(alert!);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpcMcd: Failed to check for active MCDs.");
        }

        return results;
    }

    private async Task<NwsAlert?> FetchAndProcessAsync(
        string uuid,
        List<(string Code, double Lat, double Lon)> locations)
    {
        try
        {
            var text = await FetchProductTextAsync(uuid);
            _checkedUuids.Add(uuid);
            if (text == null) return null;

            if (!text.Contains("SWOMCD", StringComparison.OrdinalIgnoreCase)) return null;

            var mcdNum = ParseMcdNumber(text);
            if (mcdNum == null) return null;

            var (issueUtc, expireUtc) = ParseValidWindow(text);
            if (issueUtc == null || expireUtc == null) return null;

            var now = DateTimeOffset.UtcNow;
            if (now < issueUtc.Value || now > expireUtc.Value) return null;

            var polygon = ParseLatLon(text);
            if (polygon == null || polygon.Count < 3) return null;

            if (!locations.Any(loc => PointInPolygon(polygon, loc.Lon, loc.Lat))) return null;

            _logger.LogInformation("SpcMcd: MCD #{Num} covers monitored area (expires {Expire:HH:mm}Z).",
                mcdNum.Value, expireUtc.Value);

            return BuildAlert(text, mcdNum.Value, issueUtc.Value, expireUtc.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpcMcd: Error processing product {Uuid}.", uuid);
            return null;
        }
    }

    private async Task<List<(string Uuid, DateTimeOffset IssuanceTime)>> FetchProductListingAsync()
    {
        var response = await _http.GetAsync("https://api.weather.gov/products?type=SWO&limit=50");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SpcMcd: Products API returned {Status}.", response.StatusCode);
            return new();
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("@graph", out var graph)) return new();

        var now = DateTimeOffset.UtcNow;
        var result = new List<(string, DateTimeOffset)>();

        foreach (var item in graph.EnumerateArray())
        {
            if (!item.TryGetProperty("@id", out var idEl)) continue;
            var url = idEl.GetString();
            if (string.IsNullOrEmpty(url)) continue;
            var uuid = url.Split('/').Last();

            // MCDs come only from KWNS (SPC Norman OK)
            if (item.TryGetProperty("issuingOffice", out var officeEl) &&
                officeEl.GetString() != "KWNS") continue;

            DateTimeOffset issuanceTime = now;
            if (item.TryGetProperty("issuanceTime", out var timeEl))
            {
                var timeStr = timeEl.GetString() ?? "";
                if (!DateTimeOffset.TryParse(timeStr, null,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out issuanceTime))
                    issuanceTime = now;
            }

            // MCDs last at most 3 hours — look back 6 to be safe
            if (now - issuanceTime > TimeSpan.FromHours(6)) continue;

            result.Add((uuid, issuanceTime));
        }

        return result;
    }

    private async Task<string?> FetchProductTextAsync(string uuid)
    {
        try
        {
            var response = await _http.GetAsync($"https://api.weather.gov/products/{uuid}");
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("productText", out var el) ? el.GetString() : null;
        }
        catch { return null; }
    }

    private async Task<List<(string Code, double Lat, double Lon)>> EnsureLocationsAsync()
    {
        if (_locations != null) return _locations;

        var codes = _nwsSettings.Zones.Count > 0 ? _nwsSettings.Zones : _nwsSettings.Counties;
        var resolved = new List<(string, double, double)>();

        foreach (var code in codes)
        {
            var info = await _zones.GetZoneInfoAsync(code);
            if (info == null)
            {
                _logger.LogWarning("SpcMcd: Could not resolve geometry for {Code}; skipping.", code);
                continue;
            }
            var c = ComputeCentroid(info.Geometry);
            if (c == null)
            {
                _logger.LogWarning("SpcMcd: Could not compute centroid for {Code}; skipping.", code);
                continue;
            }
            resolved.Add((code, c.Value.Lat, c.Value.Lon));
        }

        if (resolved.Count > 0)
        {
            _locations = resolved;
            _logger.LogInformation("SpcMcd: Resolved {Count} monitored location(s).", resolved.Count);
        }

        return resolved;
    }

    private static int? ParseMcdNumber(string text)
    {
        var m = McdNumberRegex.Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    private static (DateTimeOffset? Issue, DateTimeOffset? Expire) ParseValidWindow(string text)
    {
        var m = ValidTimeRegex.Match(text);
        if (!m.Success) return (null, null);

        var issue  = ParseDdhhmm(m.Groups[1].Value);
        var expire = ParseDdhhmm(m.Groups[2].Value);

        // Handle midnight crossover (expire before issue after wrapping)
        if (issue != null && expire != null && expire.Value < issue.Value)
            expire = expire.Value.AddDays(1);

        return (issue, expire);
    }

    /// <summary>
    /// Parses a 6-digit DDHHMM UTC string from SPC product valid lines.
    /// Uses current UTC year/month as context, selecting the candidate closest to now
    /// to handle month-boundary edge cases.
    /// </summary>
    private static DateTimeOffset? ParseDdhhmm(string ddhhmm)
    {
        if (ddhhmm.Length != 6) return null;
        if (!int.TryParse(ddhhmm[..2], out int day))   return null;
        if (!int.TryParse(ddhhmm[2..4], out int hour)) return null;
        if (!int.TryParse(ddhhmm[4..6], out int min))  return null;

        var now = DateTimeOffset.UtcNow;
        var candidates = new List<DateTimeOffset>();

        for (int delta = -1; delta <= 1; delta++)
        {
            try
            {
                var d = now.AddMonths(delta);
                candidates.Add(new DateTimeOffset(d.Year, d.Month, day, hour, min, 0, TimeSpan.Zero));
            }
            catch { }
        }

        if (candidates.Count == 0) return null;
        return candidates.MinBy(c => Math.Abs((c - now).TotalHours));
    }

    /// <summary>
    /// Parses the LAT...LON block from MCD product text.
    /// Format: 8-digit groups where first 4 digits = lat*100 (N), last 4 = lon*100 (W, negated).
    /// </summary>
    private static List<(double Lat, double Lon)>? ParseLatLon(string text)
    {
        var m = LatLonRegex.Match(text);
        if (!m.Success) return null;

        var tokens = m.Groups[1].Value
            .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        var polygon = new List<(double, double)>();
        foreach (var token in tokens)
        {
            if (token.Length != 8) continue;
            if (!double.TryParse(token[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out double lat100)) continue;
            if (!double.TryParse(token[4..8], NumberStyles.Integer, CultureInfo.InvariantCulture, out double lon100)) continue;
            polygon.Add((lat100 / 100.0, -lon100 / 100.0));
        }

        return polygon.Count >= 3 ? polygon : null;
    }

    private static bool PointInPolygon(List<(double Lat, double Lon)> polygon, double lon, double lat)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double yi = polygon[i].Lat, xi = polygon[i].Lon;
            double yj = polygon[j].Lat, xj = polygon[j].Lon;
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private NwsAlert BuildAlert(string text, int mcdNum, DateTimeOffset issueUtc, DateTimeOffset expireUtc)
    {
        var areasMatch     = AreasAffectedRegex.Match(text);
        var concerningMatch = ConcerningRegex.Match(text);

        string areas = areasMatch.Success
            ? Collapse(areasMatch.Groups[1].Value)
            : "Monitored Area";
        string concerning = concerningMatch.Success
            ? Collapse(concerningMatch.Groups[1].Value)
            : "";

        string headline = concerning.Length > 0
            ? $"SPC MCD #{mcdNum} — {concerning}"
            : $"SPC Mesoscale Discussion #{mcdNum}";

        string instruction = $"Areas affected: {areas}";
        if (concerning.Length > 0)
            instruction += $"\n{concerning}";
        instruction += $"\nhttps://www.spc.noaa.gov/products/md/{issueUtc.Year}/md{mcdNum:D4}.html";

        return new NwsAlert
        {
            Id              = $"SPC-MCD-{issueUtc.Year}-{mcdNum}",
            Event           = "SPC Mesoscale Discussion",
            Headline        = headline,
            AreaDesc        = areas,
            Severity        = "Severe",
            Urgency         = "Future",
            Certainty       = "Possible",
            SenderName      = "NOAA Storm Prediction Center",
            Instruction     = instruction,
            Sent            = issueUtc,
            Expires         = expireUtc,
            MapImageUrl     = $"https://www.spc.noaa.gov/products/md/{issueUtc.Year}/mcd{mcdNum:D4}.png",
            IsSpcMcd        = true,
            DisplayTimeZone = _timeZone,
        };
    }

    private static string Collapse(string s) =>
        Regex.Replace(s.Trim(), @"\s+", " ");

    // --- Polygon centroid (mirrors SpcOutlookService) ---

    private static (double Lat, double Lon)? ComputeCentroid(JsonElement geometry)
    {
        var type   = geometry.GetProperty("type").GetString();
        var coords = geometry.GetProperty("coordinates");

        JsonElement exterior;
        switch (type)
        {
            case "Polygon":
                exterior = coords[0];
                break;
            case "MultiPolygon":
                JsonElement? largest = null;
                double bestArea = -1;
                foreach (var poly in coords.EnumerateArray())
                {
                    var ring = poly[0];
                    double area = Math.Abs(RingSignedArea(ring));
                    if (area > bestArea) { bestArea = area; largest = ring; }
                }
                if (largest == null) return null;
                exterior = largest.Value;
                break;
            default:
                return null;
        }
        return RingCentroid(exterior);
    }

    private static double RingSignedArea(JsonElement ring)
    {
        double area = 0;
        int n = ring.GetArrayLength();
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[i][0].GetDouble(), yi = ring[i][1].GetDouble();
            double xj = ring[j][0].GetDouble(), yj = ring[j][1].GetDouble();
            area += xj * yi - xi * yj;
        }
        return area / 2.0;
    }

    private static (double Lat, double Lon)? RingCentroid(JsonElement ring)
    {
        double area = RingSignedArea(ring);
        int n = ring.GetArrayLength();
        if (Math.Abs(area) < 1e-12)
        {
            double sLon = 0, sLat = 0;
            for (int i = 0; i < n; i++) { sLon += ring[i][0].GetDouble(); sLat += ring[i][1].GetDouble(); }
            return n == 0 ? null : (sLat / n, sLon / n);
        }
        double cx = 0, cy = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[i][0].GetDouble(), yi = ring[i][1].GetDouble();
            double xj = ring[j][0].GetDouble(), yj = ring[j][1].GetDouble();
            double cross = xj * yi - xi * yj;
            cx += (xj + xi) * cross;
            cy += (yj + yi) * cross;
        }
        double factor = 1.0 / (6.0 * area);
        return (cy * factor, cx * factor);
    }
}
