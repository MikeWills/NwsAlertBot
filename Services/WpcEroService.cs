using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Polls the WPC (Weather Prediction Center) Excessive Rainfall Outlook (ERO) Day 1/2/3 GeoJSON
/// feeds and checks each monitored location (derived from Location.Zones/Location.Counties)
/// against the categorical risk polygons (Marginal/Slight/Moderate/High). Produces one synthetic
/// NwsAlert per day that is in any non-"None" categorical risk, with a categorical outlook map
/// image (via Iowa State's IEM Mesonet plotting service, keyed off the location's WFO/state)
/// that flows through the same MapImageUrl pipeline as Mapbox alert maps.
/// Note: despite living alongside SpcOutlookService/SpcMcdService, ERO is a WPC product, not SPC.
/// Docs: https://www.wpc.ncep.noaa.gov/qpf/excessive_rainfall_outlook_ero.php
/// </summary>
public class WpcEroService
{
    private readonly HttpClient _http;
    private readonly EroSettings _settings;
    private readonly LocationSettings _location;
    private readonly NwsZoneService _zones;
    private readonly ILogger<WpcEroService> _logger;

    private const string BaseUrl = "https://www.wpc.ncep.noaa.gov/exper/eromap/geojson/";

    private List<(string Code, double Lat, double Lon, string? Wfo, string? State)>? _locations;
    private DateTimeOffset _lastCheckedUtc = DateTimeOffset.MinValue;
    private readonly TimeZoneInfo _timeZone;

    // WPC's own category names in dn order (1-4). Distinct from this bot's Severity scale —
    // see BuildAlert for the (deliberately shifted) mapping between the two.
    private static readonly string[] CategoryNames = { "Marginal", "Slight", "Moderate", "High" };

    public WpcEroService(HttpClient http, EroSettings settings, LocationSettings location, NwsZoneService zones, ILogger<WpcEroService> logger)
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
            catch { logger.LogWarning("Wpc: Unknown TimeZone \"{Id}\"; falling back to America/Chicago.", id); }
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch
        {
            logger.LogWarning("Wpc: Could not load America/Chicago as fallback timezone; using UTC. Set Location.TimeZone to a valid IANA ID such as \"America/Chicago\".");
            return TimeZoneInfo.Utc;
        }
    }

    public bool IsEnabled => _settings.Enabled;

    /// <summary>
    /// Returns synthetic alerts for every monitored location currently in a non-"None"
    /// categorical ERO risk on Day 1, 2, or 3. Returns an empty list (no HTTP calls) if
    /// called before CheckIntervalSeconds has elapsed since the last check.
    /// </summary>
    public async Task<List<NwsAlert>> GetEroAlertsAsync()
    {
        if (!_settings.Enabled) return new();

        if (DateTimeOffset.UtcNow - _lastCheckedUtc < TimeSpan.FromSeconds(_settings.CheckIntervalSeconds))
            return new();
        _lastCheckedUtc = DateTimeOffset.UtcNow;

        var locations = await EnsureLocationsResolvedAsync();
        if (locations.Count == 0)
        {
            _logger.LogWarning("Wpc: No monitored locations resolved from Location.Zones/Location.Counties. Skipping ERO check.");
            return new();
        }

        var results = new List<NwsAlert>();
        foreach (var day in new[] { 1, 2, 3 })
        {
            var alert = await CheckDayAsync(day, locations);
            if (alert != null) results.Add(alert);
        }

        return results;
    }

    private async Task<List<(string Code, double Lat, double Lon, string? Wfo, string? State)>> EnsureLocationsResolvedAsync()
    {
        if (_locations != null) return _locations;

        var codes = _location.Zones.Concat(_location.Counties).ToList();
        var resolved = new List<(string Code, double Lat, double Lon, string? Wfo, string? State)>();

        foreach (var code in codes)
        {
            var info = await _zones.GetZoneInfoAsync(code);
            if (info == null)
            {
                _logger.LogWarning("Wpc: Could not resolve geometry for {Code}; this location will not be monitored.", code);
                continue;
            }

            var centroid = PolygonGeometry.ComputeCentroid(info.Geometry);
            if (centroid == null)
            {
                _logger.LogWarning("Wpc: Could not compute a centroid for {Code}; this location will not be monitored.", code);
                continue;
            }

            resolved.Add((code, centroid.Value.Lat, centroid.Value.Lon, info.Cwa, info.State));
        }

        _locations = resolved;

        if (resolved.Count > 0)
            _logger.LogInformation("Wpc: Resolved {Count} monitored location(s) for ERO checks.", resolved.Count);
        else
            _logger.LogWarning("Wpc: No zone/county geometries resolved. Zones and Counties must be configured; State-only config is not supported for ERO polygon checking.");

        return resolved;
    }

    private async Task<NwsAlert?> CheckDayAsync(int day, List<(string Code, double Lat, double Lon, string? Wfo, string? State)> locations)
    {
        try
        {
            using var doc = await FetchLayerAsync($"Day{day}_Latest.geojson");
            if (doc == null) return null;

            var features = doc.RootElement.GetProperty("features");

            // One alert per day: find the highest risk across all monitored locations.
            int bestDn = -1;
            string? bestOutlook = null;
            DateTimeOffset? bestIssue = null, bestExpire = null;
            string? bestWfo = null, bestState = null;

            foreach (var loc in locations)
            {
                var (dn, outlook, issue, expire) = FindCategorical(features, loc.Lon, loc.Lat);
                if (outlook == null) continue;

                if (dn > bestDn)
                {
                    bestDn      = dn;
                    bestOutlook = outlook;
                    bestIssue   = issue;
                    bestExpire  = expire;
                    bestWfo     = loc.Wfo;
                    bestState   = loc.State;
                }
            }

            if (bestOutlook == null) return null;

            var alert = BuildAlert(day, bestWfo, bestState, bestDn, bestOutlook, bestIssue, bestExpire, _timeZone);
            if (alert == null)
                _logger.LogWarning("Wpc: Skipping Day {Day} ERO — both ISSUE_TIME and END_TIME are absent.", day);
            return alert;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wpc: Failed to check Day {Day} ERO.", day);
            return null;
        }
    }

    private async Task<JsonDocument?> FetchLayerAsync(string fileName)
    {
        try
        {
            var response = await _http.GetAsync(BaseUrl + fileName);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Wpc: {File} returned {Status}.", fileName, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wpc: Failed to fetch {File}.", fileName);
            return null;
        }
    }

    private static (int Dn, string? Outlook, DateTimeOffset? Issue, DateTimeOffset? Expire) FindCategorical(
        JsonElement features, double lon, double lat)
    {
        int bestDn = -1;
        string? outlook = null;
        DateTimeOffset? issue = null, expire = null;

        foreach (var feature in features.EnumerateArray())
        {
            if (!PolygonGeometry.PointInGeometry(feature.GetProperty("geometry"), lon, lat)) continue;

            var props = feature.GetProperty("properties");
            if (!props.TryGetProperty("dn", out var dnEl) || !dnEl.TryGetInt32(out int dn)) continue;
            if (dn <= bestDn) continue;

            bestDn  = dn;
            outlook = props.TryGetProperty("OUTLOOK", out var outlookEl) ? outlookEl.GetString() : null;
            issue   = ParseTimestamp(props, "ISSUE_TIME");
            expire  = ParseTimestamp(props, "END_TIME");
        }

        return (bestDn, outlook, issue, expire);
    }

    // WPC timestamps ("2026-07-07 08:03:00") carry no offset but are UTC.
    private static DateTimeOffset? ParseTimestamp(JsonElement props, string key)
    {
        if (!props.TryGetProperty(key, out var el)) return null;
        var str = el.GetString();
        return str != null && DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : null;
    }

    private static NwsAlert? BuildAlert(int day, string? wfo, string? state, int dn, string outlook,
        DateTimeOffset? issue, DateTimeOffset? expire, TimeZoneInfo timeZone)
    {
        // WPC's own 4-tier category names (Marginal/Slight/Moderate/High) don't line up 1:1 with
        // this bot's Severity scale in name, only in rank — e.g. WPC "Moderate" (dn 3) maps to
        // Severity "Severe" here, since Severity "Moderate" is already used for WPC "Slight" (dn 2).
        string category = dn >= 1 && dn <= CategoryNames.Length ? CategoryNames[dn - 1] : "Unknown";
        string severity = dn switch
        {
            4 => "Extreme",
            3 => "Severe",
            2 => "Moderate",
            _ => "Minor", // dn 1 (Marginal) or unrecognized
        };

        string detailsUrl = $"https://www.wpc.ncep.noaa.gov/qpf/ero.php?opt=curr&day={day}";
        string instruction = $"{outlook}\nFor more details: {detailsUrl}";

        // Prefer ISSUE_TIME for the dedup ID; fall back to END_TIME (stable for the outlook period).
        // Never use UtcNow — it changes every minute and would re-post the same outlook every poll cycle.
        var stamp = issue ?? expire;
        if (stamp == null) return null;
        string issueStamp = stamp.Value.ToString("yyyyMMddHHmm");

        return new NwsAlert
        {
            Id              = $"WPC-ERO-Day{day}-{issueStamp}",
            Event           = $"WPC Day {day} Excessive Rainfall Outlook",
            Headline        = $"{category} Risk — Day {day} Excessive Rainfall Outlook",
            AreaDesc        = "Monitored Area",
            Severity        = severity,
            SenderName      = "NOAA Weather Prediction Center",
            Instruction     = instruction,
            Sent            = issue ?? DateTimeOffset.UtcNow,
            Expires         = expire,
            MapImageUrl     = BuildEroImageUrl(day, wfo, state),
            DetailsUrl      = detailsUrl,
            IsEro           = true,
            DisplayTimeZone = timeZone,
        };
    }

    /// <summary>
    /// Builds an IEM Mesonet auto-plot URL showing the categorical ERO risk for the location's
    /// WFO/state, e.g. https://mesonet.agron.iastate.edu/plotting/auto/plot/220/which:1E::...
    /// Returns null if the WFO or state couldn't be resolved. No API key required.
    /// Plot docs: https://mesonet.agron.iastate.edu/plotting/auto/?q=220
    /// </summary>
    private static string? BuildEroImageUrl(int day, string? wfo, string? state)
    {
        if (string.IsNullOrEmpty(wfo) || string.IsNullOrEmpty(state)) return null;

        return "https://mesonet.agron.iastate.edu/plotting/auto/plot/220/" +
               $"which:{day}E::cat:categorical::t:cwa::network:WFO::wfo:{wfo}::csector:{state}::_r:t::dpi:100.png";
    }
}
