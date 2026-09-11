using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Polls the WPC (Weather Prediction Center) Probabilistic Winter Precipitation Forecast (PWPF)
/// contour KMZ files and checks each monitored location (derived from
/// Location.Zones/Location.Counties) against the 24-hour probability-of-exceedance contours for
/// snow (≥1/2/4/6/8/12/18") and freezing rain (≥0.01/0.10/0.25/0.50") on Day 1 and Day 2.
/// Produces at most one synthetic NwsAlert per day per precipitation type, and only when the
/// forecast has gone *up* since the last post for that day (a higher accumulation threshold now
/// clears Pwpf.MinProbabilityPercent, or the same threshold's probability band rose) — a
/// downgraded or unchanged forecast is never re-posted.
/// Data: https://www.wpc.ncep.noaa.gov/pwpf/latest_kml/ (one KMZ per threshold/forecast hour)
/// Docs: https://www.wpc.ncep.noaa.gov/pwpf/about_pwpf_products.shtml
/// </summary>
public class WpcPwpfService
{
    private readonly HttpClient _http;
    private readonly PwpfSettings _settings;
    private readonly LocationSettings _location;
    private readonly NwsZoneService _zones;
    private readonly AlertTrackerService _tracker;
    private readonly ILogger<WpcPwpfService> _logger;

    private const string BaseUrl = "https://www.wpc.ncep.noaa.gov/pwpf/latest_kml/";
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    /// <summary>The probability contour levels WPC draws, ascending. A location's "band" is the highest of these whose contour encloses it.</summary>
    internal static readonly int[] ContourLevels = { 1, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 95 };

    /// <summary>Snow thresholds (inches) WPC publishes a 24-hr contour file for.</summary>
    internal static readonly double[] AvailableSnowThresholds = { 1, 2, 4, 6, 8, 12, 18 };

    /// <summary>Freezing rain thresholds (inches) WPC publishes a 24-hr contour file for.</summary>
    internal static readonly double[] AvailableIceThresholds = { 0.01, 0.10, 0.25, 0.50 };

    private List<(string Code, double Lat, double Lon)>? _locations;
    private DateTimeOffset _lastCheckedUtc = DateTimeOffset.MinValue;
    private readonly TimeZoneInfo _timeZone;

    public WpcPwpfService(HttpClient http, PwpfSettings settings, LocationSettings location, NwsZoneService zones,
        AlertTrackerService tracker, ILogger<WpcPwpfService> logger)
    {
        _http = http;
        _settings = settings;
        _location = location;
        _zones = zones;
        _tracker = tracker;
        _logger = logger;
        _timeZone = ResolveTimeZone(location.TimeZone, logger);
    }

    private static TimeZoneInfo ResolveTimeZone(string id, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { logger.LogWarning("Pwpf: Unknown TimeZone \"{Id}\"; falling back to America/Chicago.", id); }
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch
        {
            logger.LogWarning("Pwpf: Could not load America/Chicago as fallback timezone; using UTC. Set Location.TimeZone to a valid IANA ID such as \"America/Chicago\".");
            return TimeZoneInfo.Utc;
        }
    }

    public bool IsEnabled => _settings.Enabled;

    /// <summary>
    /// Returns synthetic alerts for each Day 1/2 snow or freezing-rain forecast whose highest
    /// qualifying threshold/probability band has risen since the last post for that day.
    /// Returns an empty list (no HTTP calls) if called before CheckIntervalSeconds has elapsed
    /// since the last check.
    /// </summary>
    public async Task<List<NwsAlert>> GetPwpfAlertsAsync()
    {
        if (!_settings.Enabled) return new();

        if (DateTimeOffset.UtcNow - _lastCheckedUtc < TimeSpan.FromSeconds(_settings.CheckIntervalSeconds))
            return new();
        _lastCheckedUtc = DateTimeOffset.UtcNow;

        var locations = await EnsureLocationsResolvedAsync();
        if (locations.Count == 0)
        {
            _logger.LogWarning("Pwpf: No monitored locations resolved from Location.Zones/Location.Counties. Skipping PWPF check.");
            return new();
        }

        var snowThresholds = ParseThresholds(_settings.SnowThresholds, AvailableSnowThresholds, "SnowThresholds", _logger);
        var iceThresholds  = ParseThresholds(_settings.IceThresholds,  AvailableIceThresholds,  "IceThresholds", _logger);
        if (snowThresholds.Count == 0 && iceThresholds.Count == 0)
        {
            _logger.LogWarning("Pwpf: Both SnowThresholds and IceThresholds are empty; nothing to check.");
            return new();
        }

        var results = new List<NwsAlert>();
        foreach (var day in new[] { 1, 2 })
        {
            if (snowThresholds.Count > 0)
            {
                var alert = await CheckDayAsync(day, PrecipType.Snow, snowThresholds, locations);
                if (alert != null) results.Add(alert);
            }
            if (iceThresholds.Count > 0)
            {
                var alert = await CheckDayAsync(day, PrecipType.Ice, iceThresholds, locations);
                if (alert != null) results.Add(alert);
            }
        }

        return results;
    }

    private async Task<List<(string Code, double Lat, double Lon)>> EnsureLocationsResolvedAsync()
    {
        if (_locations != null) return _locations;

        var codes = _location.Zones.Concat(_location.Counties).ToList();
        var resolved = new List<(string Code, double Lat, double Lon)>();

        foreach (var code in codes)
        {
            var info = await _zones.GetZoneInfoAsync(code);
            if (info == null)
            {
                _logger.LogWarning("Pwpf: Could not resolve geometry for {Code}; this location will not be monitored.", code);
                continue;
            }

            var centroid = PolygonGeometry.ComputeCentroid(info.Geometry);
            if (centroid == null)
            {
                _logger.LogWarning("Pwpf: Could not compute a centroid for {Code}; this location will not be monitored.", code);
                continue;
            }

            resolved.Add((code, centroid.Value.Lat, centroid.Value.Lon));
        }

        _locations = resolved;

        if (resolved.Count > 0)
            _logger.LogInformation("Pwpf: Resolved {Count} monitored location(s) for PWPF checks.", resolved.Count);
        else
            _logger.LogWarning("Pwpf: No zone/county geometries resolved. Zones and Counties must be configured; State-only config is not supported for PWPF contour checking.");

        return resolved;
    }

    /// <summary>
    /// Parses a comma-separated threshold list from config, keeping only values WPC actually
    /// publishes a contour file for (anything else would just 404) and returning them ascending.
    /// </summary>
    internal static List<double> ParseThresholds(string configured, double[] available, string settingName, ILogger logger)
    {
        var result = new List<double>();
        foreach (var raw in (configured ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                logger.LogWarning("Pwpf: Ignoring unparseable {Setting} entry \"{Raw}\".", settingName, raw);
                continue;
            }

            var match = available.FirstOrDefault(a => Math.Abs(a - value) < 0.001, double.NaN);
            if (double.IsNaN(match))
            {
                logger.LogWarning("Pwpf: Ignoring {Setting} entry {Value} — WPC only publishes contours for {Available}.",
                    settingName, raw, string.Join(", ", available.Select(a => a.ToString(CultureInfo.InvariantCulture))));
                continue;
            }

            if (!result.Contains(match)) result.Add(match);
        }

        result.Sort();
        return result;
    }

    private async Task<NwsAlert?> CheckDayAsync(int day, PrecipType ptype, List<double> thresholds,
        List<(string Code, double Lat, double Lon)> locations)
    {
        try
        {
            // Fetch every configured threshold for this day so the post can list all of them,
            // not just the one that triggered it.
            var bands = new List<(double Threshold, int Band)>();
            DateTimeOffset? validStart = null, validEnd = null, issued = null;

            foreach (var threshold in thresholds)
            {
                var file = BuildFileName(ptype, threshold, day);
                var contour = await FetchContourAsync(file);
                if (contour == null) return null; // logged in FetchContourAsync; try again next interval

                int band = 0;
                foreach (var loc in locations)
                    band = Math.Max(band, FindBand(contour.Polygons, loc.Lon, loc.Lat));
                bands.Add((threshold, band));

                validStart ??= contour.ValidStart;
                validEnd   ??= contour.ValidEnd;
                issued     ??= contour.LastModified;
            }

            var alert = BuildAlert(day, ptype, bands, validStart, validEnd, issued, _settings.MinProbabilityPercent, _timeZone);
            if (alert == null) return null;

            if (HasPostedAtOrAbove(day, ptype, thresholds, alert.PwpfThreshold, alert.PwpfBand, validStart!.Value, _settings.MinProbabilityPercent))
            {
                _logger.LogInformation("Pwpf: Day {Day} {Type} forecast has not risen since last post (≥{Threshold}\" at {Band}%); not re-posting.",
                    day, ptype, FormatInches(alert.PwpfThreshold), alert.PwpfBand);
                return null;
            }

            return alert;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pwpf: Failed to check Day {Day} {Type} PWPF.", day, ptype);
            return null;
        }
    }

    /// <summary>
    /// "Only post when the forecast goes up": true if posted_alerts.txt already holds an ID for
    /// this day/type/valid-date at a higher threshold (any qualifying band) or the same threshold
    /// at the same-or-higher band. Uses the tracker's persisted log so a restart doesn't re-post.
    /// </summary>
    private bool HasPostedAtOrAbove(int day, PrecipType ptype, List<double> thresholds, double threshold, int band,
        DateTimeOffset validStart, int minProbability)
    {
        foreach (var t in thresholds)
        {
            if (t < threshold) continue;
            foreach (var b in ContourLevels)
            {
                if (b < minProbability) continue;
                if (t == threshold && b < band) continue;
                if (_tracker.HasBeenPosted(BuildId(day, ptype, validStart, t, b))) return true;
            }
        }
        return false;
    }

    internal static string BuildId(int day, PrecipType ptype, DateTimeOffset validStart, double threshold, int band)
        => $"WPC-PWPF-Day{day}-{ptype}-{validStart.UtcDateTime:yyyyMMdd}-ge{FormatInches(threshold)}-p{band}";

    /// <summary>
    /// WPC file names: <c>prb_24hsnow_ge04_f024_cntr_latest.kmz</c> (snow, zero-padded whole
    /// inches) and <c>prb_24hicez_ge.25_f024_cntr_latest.kmz</c> (ice, leading-dot fraction).
    /// Day 1 is the 24-hr period ending 24 h after the latest cycle (f024); Day 2 is f048.
    /// </summary>
    internal static string BuildFileName(PrecipType ptype, double threshold, int day)
    {
        string amount = ptype == PrecipType.Snow
            ? ((int)threshold).ToString("00", CultureInfo.InvariantCulture)
            : threshold.ToString("0.00", CultureInfo.InvariantCulture).TrimStart('0');
        string product = ptype == PrecipType.Snow ? "snow" : "icez";
        return $"prb_24h{product}_ge{amount}_f{day * 24:000}_cntr_latest.kmz";
    }

    internal static string FormatInches(double inches)
        => inches.ToString(inches < 1 ? "0.00" : "0", CultureInfo.InvariantCulture);

    private async Task<PwpfContour?> FetchContourAsync(string fileName)
    {
        try
        {
            using var response = await _http.GetAsync(BaseUrl + fileName);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Pwpf: {File} returned {Status}.", fileName, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".kml", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                _logger.LogWarning("Pwpf: {File} contained no .kml entry.", fileName);
                return null;
            }

            string kml;
            using (var reader = new StreamReader(entry.Open()))
                kml = await reader.ReadToEndAsync();

            var contour = ParseKml(kml);
            contour.LastModified = response.Content.Headers.LastModified;
            return contour;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pwpf: Failed to fetch {File}.", fileName);
            return null;
        }
    }

    /// <summary>
    /// Pulls the valid window from the document <c>&lt;snippet&gt;</c>
    /// ("Valid 00Z 09/12/2026 - 00Z 09/13/2026") and every polygon Placemark, whose
    /// <c>&lt;name&gt;</c> is the contour's probability level (1, 5, 10, 20 … 95).
    /// Placemarks without a Polygon are the level labels — skipped.
    /// </summary>
    internal static PwpfContour ParseKml(string kml)
    {
        var doc = XDocument.Parse(kml);
        var contour = new PwpfContour();

        var snippet = doc.Descendants(Kml + "snippet").FirstOrDefault()?.Value;
        if (snippet != null)
        {
            var (start, end) = ParseValidWindow(snippet);
            contour.ValidStart = start;
            contour.ValidEnd = end;
        }

        var factory = new GeometryFactory();
        foreach (var placemark in doc.Descendants(Kml + "Placemark"))
        {
            var polygonEl = placemark.Element(Kml + "Polygon");
            if (polygonEl == null) continue;

            var name = placemark.Element(Kml + "name")?.Value.Trim();
            if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) continue;

            var shell = ParseRing(polygonEl.Element(Kml + "outerBoundaryIs")?.Descendants(Kml + "coordinates").FirstOrDefault()?.Value, factory);
            if (shell == null) continue;

            var holes = polygonEl.Elements(Kml + "innerBoundaryIs")
                .Select(h => ParseRing(h.Descendants(Kml + "coordinates").FirstOrDefault()?.Value, factory))
                .Where(h => h != null)
                .Cast<LinearRing>()
                .ToArray();

            contour.Polygons.Add((level, factory.CreatePolygon(shell, holes)));
        }

        return contour;
    }

    // KML coordinates: whitespace-separated "lon,lat[,alt]" tuples.
    private static LinearRing? ParseRing(string? coordinates, GeometryFactory factory)
    {
        if (string.IsNullOrWhiteSpace(coordinates)) return null;

        var coords = new List<Coordinate>();
        foreach (var tuple in coordinates.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = tuple.Split(',');
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            coords.Add(new Coordinate(lon, lat));
        }

        if (coords.Count < 3) return null;
        if (!coords[0].Equals2D(coords[^1])) coords.Add(coords[0].Copy());
        if (coords.Count < 4) return null;

        try { return factory.CreateLinearRing(coords.ToArray()); }
        catch { return null; }
    }

    /// <summary>Parses "Valid 00Z 09/12/2026 - 00Z 09/13/2026" into UTC start/end.</summary>
    internal static (DateTimeOffset? Start, DateTimeOffset? End) ParseValidWindow(string snippet)
    {
        var m = System.Text.RegularExpressions.Regex.Match(snippet,
            @"(\d{2})Z\s+(\d{2})/(\d{2})/(\d{4})\s*-\s*(\d{2})Z\s+(\d{2})/(\d{2})/(\d{4})");
        if (!m.Success) return (null, null);

        static DateTimeOffset? Build(string hh, string mm, string dd, string yyyy)
        {
            return DateTimeOffset.TryParseExact($"{yyyy}-{mm}-{dd} {hh}", "yyyy-MM-dd HH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
        }

        return (Build(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value),
                Build(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value));
    }

    /// <summary>
    /// The contour level (probability band) at a point: the highest level whose polygon covers
    /// it, or 0 if it lies outside every contour (probability below 1%). Contours are isolines,
    /// so a point inside the 40% ring is also inside the 10% and 20% rings — hence "highest".
    /// </summary>
    internal static int FindBand(List<(int Level, Polygon Polygon)> polygons, double lon, double lat)
    {
        int best = 0;
        foreach (var (level, polygon) in polygons)
        {
            if (level <= best) continue;
            try
            {
                if (polygon.Covers(polygon.Factory.CreatePoint(new Coordinate(lon, lat))))
                    best = level;
            }
            catch
            {
                // Malformed contour ring (self-intersection etc.) — skip rather than fail the whole check.
            }
        }
        return best;
    }

    /// <summary>Human label for a contour band: "40–50%", "≥95%", or "&lt;1%" for none.</summary>
    internal static string FormatBand(int band)
    {
        if (band <= 0) return "<1%";
        int idx = Array.IndexOf(ContourLevels, band);
        if (idx < 0) return $"≥{band}%";
        if (idx == ContourLevels.Length - 1) return $"≥{band}%";
        return $"{band}–{ContourLevels[idx + 1]}%";
    }

    /// <summary>
    /// Builds the alert for one day/type, or null if no threshold's band meets
    /// <paramref name="minProbability"/> at any monitored location. The triggering threshold is
    /// the highest one that qualifies; every checked threshold is listed in the body.
    /// </summary>
    internal static NwsAlert? BuildAlert(int day, PrecipType ptype, List<(double Threshold, int Band)> bands,
        DateTimeOffset? validStart, DateTimeOffset? validEnd, DateTimeOffset? issued, int minProbability, TimeZoneInfo timeZone)
    {
        if (validStart == null) return null; // needed for the dedup ID; never fall back to UtcNow

        var qualifying = bands.Where(b => b.Band >= minProbability).OrderByDescending(b => b.Threshold).FirstOrDefault();
        if (qualifying == default) return null;

        string what = ptype == PrecipType.Snow ? "snow" : "freezing rain";
        string severity = MapSeverity(ptype, qualifying.Threshold);

        string detailsUrl = "https://www.wpc.ncep.noaa.gov/pwpf/wwd_accum_probs.php" +
                            $"?fpd=24&ptype={(ptype == PrecipType.Snow ? "snow" : "icez")}&amt={FormatInches(qualifying.Threshold)}&day={day}";

        var lines = new List<string>
        {
            $"WPC probability of 24-hour {what} accumulation, valid " +
            $"{TimeZoneInfo.ConvertTime(validStart.Value, timeZone):ddd MMM d h:mm tt}" +
            (validEnd != null ? $" – {TimeZoneInfo.ConvertTime(validEnd.Value, timeZone):ddd MMM d h:mm tt}" : "") + ":"
        };
        foreach (var (threshold, band) in bands.OrderBy(b => b.Threshold))
            lines.Add($"• ≥{FormatInches(threshold)}\": {FormatBand(band)}");
        lines.Add("");
        lines.Add($"For more details: {detailsUrl}");

        return new NwsAlert
        {
            Id              = BuildId(day, ptype, validStart.Value, qualifying.Threshold, qualifying.Band),
            Event           = $"WPC Day {day} {(ptype == PrecipType.Snow ? "Snowfall" : "Freezing Rain")} Outlook",
            Headline        = $"{FormatBand(qualifying.Band)} chance of ≥{FormatInches(qualifying.Threshold)}\" {what} — Day {day} Winter Weather Outlook",
            AreaDesc        = "Monitored Area",
            Severity        = severity,
            SenderName      = "NOAA Weather Prediction Center",
            Instruction     = string.Join("\n", lines),
            Sent            = issued ?? validStart.Value,
            Expires         = validEnd,
            MapImageUrl     = BuildImageUrl(day, ptype, qualifying.Threshold),
            DetailsUrl      = detailsUrl,
            IsPwpf          = true,
            PwpfThreshold   = qualifying.Threshold,
            PwpfBand        = qualifying.Band,
            DisplayTimeZone = timeZone,
        };
    }

    /// <summary>
    /// Severity keyed to the highest qualifying accumulation threshold. Snow: 1–2" Minor,
    /// 4–6" Moderate, 8" Severe, 12"+ Extreme. Ice: 0.01" Minor, 0.10" Moderate, 0.25" Severe,
    /// 0.50" Extreme.
    /// </summary>
    internal static string MapSeverity(PrecipType ptype, double threshold) => ptype switch
    {
        PrecipType.Snow => threshold switch
        {
            >= 12 => "Extreme",
            >= 8  => "Severe",
            >= 4  => "Moderate",
            _     => "Minor",
        },
        _ => threshold switch
        {
            >= 0.50 => "Extreme",
            >= 0.25 => "Severe",
            >= 0.10 => "Moderate",
            _       => "Minor",
        },
    };

    /// <summary>
    /// WPC's static CONUS-wide Winter Weather Desk images. Only ≥4/8/12" snow and ≥0.25" ice
    /// have a dedicated image; other thresholds fall back to the day's composite. National
    /// scale, not cropped to the location — WPC offers no per-region render of this product.
    /// </summary>
    internal static string BuildImageUrl(int day, PrecipType ptype, double threshold)
    {
        string file = ptype switch
        {
            PrecipType.Snow when threshold is 4 or 8 or 12 => $"day{day}_psnow_gt_{(int)threshold:00}_conus.gif",
            PrecipType.Ice when Math.Abs(threshold - 0.25) < 0.001 => $"day{day}_pice_gt_25_conus.gif",
            _ => $"day{day}_composite_conus.gif",
        };
        return "https://www.wpc.ncep.noaa.gov/wwd/" + file;
    }
}

public enum PrecipType { Snow, Ice }

/// <summary>One parsed PWPF contour KMZ: its valid window plus every (level, polygon) contour ring.</summary>
public class PwpfContour
{
    public DateTimeOffset? ValidStart { get; set; }
    public DateTimeOffset? ValidEnd { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public List<(int Level, Polygon Polygon)> Polygons { get; } = new();
}
