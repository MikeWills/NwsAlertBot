using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Polls the SPC (Storm Prediction Center) Day 1/Day 2 Convective Outlook GeoJSON feeds
/// and checks each monitored location (derived from Nws.Zones/Nws.Counties) against the
/// categorical risk polygons (TSTM/MRGL/SLGT/ENH/MDT/HIGH) and the tornado/wind/hail
/// probability polygons. Produces one synthetic NwsAlert per (location, day) that is in
/// any non-"None" categorical risk, bundling the tornado/wind/hail breakdown and a
/// categorical outlook map image (via Iowa State's IEM Mesonet plotting service, keyed
/// off the location's WFO/state) that flows through the same MapImageUrl pipeline as
/// Mapbox alert maps.
/// Docs: https://www.spc.noaa.gov/misc/about.html
/// </summary>
public class SpcOutlookService
{
    private readonly HttpClient _http;
    private readonly SpcSettings _settings;
    private readonly LocationSettings _location;
    private readonly NwsZoneService _zones;
    private readonly ILogger<SpcOutlookService> _logger;

    private const string BaseUrl = "https://www.spc.noaa.gov/products/outlook/";

    private List<(string Code, string Name, double Lat, double Lon, string? Wfo, string? State)>? _locations;
    private DateTimeOffset _lastCheckedUtc = DateTimeOffset.MinValue;
    private readonly TimeZoneInfo _timeZone;

    public SpcOutlookService(HttpClient http, SpcSettings settings, LocationSettings location, NwsZoneService zones, ILogger<SpcOutlookService> logger)
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
            catch { logger.LogWarning("Spc: Unknown TimeZone \"{Id}\"; falling back to America/Chicago.", id); }
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch
        {
            logger.LogWarning("Spc: Could not load America/Chicago as fallback timezone; using UTC. Set Location.TimeZone to a valid IANA ID such as \"America/Chicago\".");
            return TimeZoneInfo.Utc;
        }
    }

    public bool IsEnabled => _settings.Enabled;

    /// <summary>
    /// Returns synthetic alerts for every monitored location currently in a non-"None"
    /// categorical outlook on Day 1 or Day 2. Returns an empty list (no HTTP calls) if
    /// called before CheckIntervalSeconds has elapsed since the last check.
    /// </summary>
    public async Task<List<NwsAlert>> GetOutlookAlertsAsync()
    {
        if (!_settings.Enabled) return new();

        if (DateTimeOffset.UtcNow - _lastCheckedUtc < TimeSpan.FromSeconds(_settings.CheckIntervalSeconds))
            return new();
        _lastCheckedUtc = DateTimeOffset.UtcNow;

        var locations = await EnsureLocationsResolvedAsync();
        if (locations.Count == 0)
        {
            _logger.LogWarning("Spc: No monitored locations resolved from Nws.Zones/Nws.Counties. Skipping outlook check.");
            return new();
        }

        var results = new List<NwsAlert>();
        foreach (var day in new[] { 1, 2 })
        {
            var alert = await CheckDayAsync(day, locations);
            if (alert != null) results.Add(alert);
        }

        return results;
    }

    private async Task<List<(string Code, string Name, double Lat, double Lon, string? Wfo, string? State)>> EnsureLocationsResolvedAsync()
    {
        if (_locations != null) return _locations;

        var codes = _location.Zones.Concat(_location.Counties).ToList();
        var resolved = new List<(string Code, string Name, double Lat, double Lon, string? Wfo, string? State)>();

        foreach (var code in codes)
        {
            var info = await _zones.GetZoneInfoAsync(code);
            if (info == null)
            {
                _logger.LogWarning("Spc: Could not resolve geometry for {Code}; this location will not be monitored.", code);
                continue;
            }

            var centroid = PolygonGeometry.ComputeCentroid(info.Geometry);
            if (centroid == null)
            {
                _logger.LogWarning("Spc: Could not compute a centroid for {Code}; this location will not be monitored.", code);
                continue;
            }

            resolved.Add((code, code, centroid.Value.Lat, centroid.Value.Lon, info.Cwa, info.State));
        }

        _locations = resolved;

        if (resolved.Count > 0)
            _logger.LogInformation("Spc: Resolved {Count} monitored location(s) for outlook checks.", resolved.Count);
        else
            _logger.LogWarning("Spc: No zone/county geometries resolved. Zones and Counties must be configured; State-only config is not supported for SPC outlook polygon checking.");

        return resolved;
    }

    private async Task<NwsAlert?> CheckDayAsync(int day, List<(string Code, string Name, double Lat, double Lon, string? Wfo, string? State)> locations)
    {
        try
        {
            using var catDoc  = await FetchLayerAsync($"day{day}otlk_cat.lyr.geojson");
            using var tornDoc = await FetchLayerAsync($"day{day}otlk_torn.lyr.geojson");
            using var windDoc = await FetchLayerAsync($"day{day}otlk_wind.lyr.geojson");
            using var hailDoc = await FetchLayerAsync($"day{day}otlk_hail.lyr.geojson");

            if (catDoc == null) return null;

            var catFeatures  = catDoc.RootElement.GetProperty("features");
            var tornFeatures = tornDoc?.RootElement.GetProperty("features");
            var windFeatures = windDoc?.RootElement.GetProperty("features");
            var hailFeatures = hailDoc?.RootElement.GetProperty("features");

            // One alert per day: find the highest risk across all monitored locations.
            int bestDn = -1;
            string? bestLabel = null, bestLabel2 = null;
            DateTimeOffset? bestIssue = null, bestExpire = null;
            string? bestWfo = null, bestState = null;
            double? maxTorn = null, maxWind = null, maxHail = null;
            string? sigTorn = null, sigWind = null, sigHail = null;

            foreach (var loc in locations)
            {
                var (dn, label, label2, issue, expire) = FindCategorical(catFeatures, loc.Lon, loc.Lat);
                if (label == null) continue;

                maxTorn = MaxNullable(maxTorn, FindMaxProbability(tornFeatures, loc.Lon, loc.Lat));
                maxWind = MaxNullable(maxWind, FindMaxProbability(windFeatures, loc.Lon, loc.Lat));
                maxHail = MaxNullable(maxHail, FindMaxProbability(hailFeatures, loc.Lon, loc.Lat));

                if (tornFeatures.HasValue) sigTorn = MaxSig(sigTorn, FindSig(tornFeatures.Value, loc.Lon, loc.Lat));
                if (windFeatures.HasValue) sigWind = MaxSig(sigWind, FindSig(windFeatures.Value, loc.Lon, loc.Lat));
                if (hailFeatures.HasValue) sigHail = MaxSig(sigHail, FindSig(hailFeatures.Value, loc.Lon, loc.Lat));

                if (dn > bestDn)
                {
                    bestDn     = dn;
                    bestLabel  = label;
                    bestLabel2 = label2;
                    bestIssue  = issue;
                    bestExpire = expire;
                    bestWfo    = loc.Wfo;
                    bestState  = loc.State;
                }
            }

            if (bestLabel == null) return null;

            var alert = BuildAlert(day, bestWfo, bestState, bestLabel, bestLabel2!, bestIssue, bestExpire, maxTorn, maxWind, maxHail, sigTorn, sigWind, sigHail, _timeZone);
            if (alert == null)
                _logger.LogWarning("Spc: Skipping Day {Day} outlook — both ISSUE_ISO and EXPIRE_ISO are absent.", day);
            return alert;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spc: Failed to check Day {Day} outlook.", day);
            return null;
        }
    }

    private static double? MaxNullable(double? a, double? b) =>
        a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);

    private static string? MaxSig(string? a, string? b) =>
        a == null ? b : b == null ? a : string.CompareOrdinal(b, a) > 0 ? b : a;

    private async Task<JsonDocument?> FetchLayerAsync(string fileName)
    {
        try
        {
            var response = await _http.GetAsync(BaseUrl + fileName);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Spc: {File} returned {Status}.", fileName, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spc: Failed to fetch {File}.", fileName);
            return null;
        }
    }

    private static (int Dn, string? Label, string? Label2, DateTimeOffset? Issue, DateTimeOffset? Expire) FindCategorical(
        JsonElement features, double lon, double lat)
    {
        int bestDn = -1;
        string? label = null, label2 = null;
        DateTimeOffset? issue = null, expire = null;

        foreach (var feature in features.EnumerateArray())
        {
            if (!PointInGeometry(feature.GetProperty("geometry"), lon, lat)) continue;

            var props = feature.GetProperty("properties");
            if (!props.TryGetProperty("DN", out var dnEl) || !dnEl.TryGetInt32(out int dn)) continue;
            if (dn <= bestDn) continue;

            bestDn = dn;
            label  = props.GetProperty("LABEL").GetString();
            label2 = props.GetProperty("LABEL2").GetString();
            issue  = ParseIso(props, "ISSUE_ISO");
            expire = ParseIso(props, "EXPIRE_ISO");
        }

        return (bestDn, label, label2, issue, expire);
    }

    private static double? FindMaxProbability(JsonElement? features, double lon, double lat)
    {
        if (features == null) return null;

        double? best = null;
        foreach (var feature in features.Value.EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            var labelStr = props.GetProperty("LABEL").GetString();
            // SIGN is the significant-severe hatch polygon — not a numeric probability; handled separately.
            if (labelStr == null || !double.TryParse(labelStr, CultureInfo.InvariantCulture, out var pct))
                continue;

            if (!PointInGeometry(feature.GetProperty("geometry"), lon, lat)) continue;
            if (best == null || pct > best) best = pct;
        }

        return best;
    }

    // SPC significant-severe hatching uses labels "CIG1", "CIG2", etc. — not numeric probabilities.
    // Returns the highest significant label covering the point, or null if none.
    private static string? FindSig(JsonElement features, double lon, double lat)
    {
        string? best = null;
        foreach (var feature in features.EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            var label = props.GetProperty("LABEL").GetString();
            if (label == null || !label.StartsWith("CIG", StringComparison.OrdinalIgnoreCase)) continue;
            if (!PointInGeometry(feature.GetProperty("geometry"), lon, lat)) continue;
            if (best == null || string.CompareOrdinal(label, best) > 0) best = label;
        }
        return best;
    }

    // SPC timestamps are UTC. AssumeUniversal treats offset-free strings as UTC;
    // explicit offsets (+00:00, Z) are still honoured.
    private static DateTimeOffset? ParseIso(JsonElement props, string key)
    {
        if (!props.TryGetProperty(key, out var el)) return null;
        var str = el.GetString();
        return str != null && DateTimeOffset.TryParse(str, null,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : null;
    }

    private static NwsAlert? BuildAlert(int day, string? wfo, string? state, string label, string label2,
        DateTimeOffset? issue, DateTimeOffset? expire,
        double? tornPct, double? windPct, double? hailPct,
        string? sigTorn, string? sigWind, string? sigHail,
        TimeZoneInfo timeZone)
    {
        string severity = label switch
        {
            "HIGH" => "Extreme",
            "MDT"  => "Severe",
            "ENH"  => "Severe",
            "SLGT" => "Moderate",
            _      => "Minor", // MRGL, TSTM
        };

        // SPC minimum thresholds: tornado polygons start at 2%, wind/hail at 5%.
        // No polygon for a point means the probability is below that threshold, not zero.
        // "Significant" (SIGN hatch) = 10%+ chance of EF2+ tornado, 2"+ hail, or 65+ kt wind.
        static string Sig(string? s) => s != null ? $" — {s}" : "";
        static string FormatTornPct(double? p, string? sig) =>
            (p.HasValue ? $"{p.Value * 100:0}%" : "< 2%") + Sig(sig);
        static string FormatWindHailPct(double? p, string? sig) =>
            (p.HasValue ? $"{p.Value * 100:0}%" : "< 5%") + Sig(sig);

        string instruction = $"Tornado: {FormatTornPct(tornPct, sigTorn)}\nWind: {FormatWindHailPct(windPct, sigWind)}\nHail: {FormatWindHailPct(hailPct, sigHail)}" +
                             $"\nFor more details: https://www.spc.noaa.gov/products/outlook/day{day}otlk.html";
        // Prefer ISSUE_ISO for the dedup ID; fall back to EXPIRE_ISO (stable for the outlook period).
        // Never use UtcNow — it changes every minute and would re-post the same outlook every poll cycle.
        var stamp = issue ?? expire;
        if (stamp == null) return null;
        string issueStamp = stamp.Value.ToString("yyyyMMddHHmm");

        return new NwsAlert
        {
            Id           = $"SPC-Day{day}-{issueStamp}",
            Event        = $"SPC Day {day} Convective Outlook",
            Headline     = $"{label2} ({label}) — Day {day} Convective Outlook",
            AreaDesc     = "Monitored Area",
            Severity     = severity,
            SenderName   = "NOAA Storm Prediction Center",
            Instruction  = instruction,
            Sent         = issue ?? DateTimeOffset.UtcNow,
            Expires      = expire,
            MapImageUrl      = BuildOutlookImageUrl(day, wfo, state),
            IsSpcOutlook     = true,
            DisplayTimeZone  = timeZone,
        };
    }

    /// <summary>
    /// Builds an IEM Mesonet auto-plot URL showing the categorical outlook for the location's
    /// WFO/state, e.g. https://mesonet.agron.iastate.edu/plotting/auto/plot/220/which:1C::...
    /// Returns null if the WFO or state couldn't be resolved (e.g. some Alaska/Pacific/Caribbean
    /// offices use ICAO-prefixed codes on IEM's side that don't match the NWS API's plain CWA id).
    /// No API key required. Plot docs: https://mesonet.agron.iastate.edu/plotting/auto/?q=220
    /// </summary>
    private static string? BuildOutlookImageUrl(int day, string? wfo, string? state)
    {
        if (string.IsNullOrEmpty(wfo) || string.IsNullOrEmpty(state)) return null;

        return "https://mesonet.agron.iastate.edu/plotting/auto/plot/220/" +
               $"which:{day}C::cat:categorical::t:cwa::network:WFO::wfo:{wfo}::csector:{state}::_r:t::dpi:100.png";
    }

    // --- Point-in-polygon (ray casting) ---

    private static bool PointInGeometry(JsonElement geometry, double lon, double lat)
    {
        var type = geometry.GetProperty("type").GetString();
        var coords = geometry.GetProperty("coordinates");

        return type switch
        {
            "Polygon" => PointInPolygonRings(coords, lon, lat),
            "MultiPolygon" => coords.EnumerateArray().Any(poly => PointInPolygonRings(poly, lon, lat)),
            _ => false,
        };
    }

    private static bool PointInPolygonRings(JsonElement rings, double lon, double lat)
    {
        if (!PointInRing(rings[0], lon, lat)) return false;

        for (int i = 1; i < rings.GetArrayLength(); i++)
            if (PointInRing(rings[i], lon, lat)) return false; // inside a hole

        return true;
    }

    private static bool PointInRing(JsonElement ring, double lon, double lat)
    {
        bool inside = false;
        int n = ring.GetArrayLength();

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var pi = ring[i];
            var pj = ring[j];
            double xi = pi[0].GetDouble(), yi = pi[1].GetDouble();
            double xj = pj[0].GetDouble(), yj = pj[1].GetDouble();

            if ((yi > lat) != (yj > lat) &&
                lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
                inside = !inside;
        }

        return inside;
    }

}
