using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Precision;
using NetTopologySuite.Simplify;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Generates map image URLs showing the affected area of each alert.
///
/// Primary path (VTEC events): IEM autoplot #208 — server-side PNG with the exact NWS
/// warning polygon, county boundaries, and NEXRAD radar overlay. IEM's VTEC JSON API is
/// checked first to confirm the event exists in their database; if not found, falls through
/// to Mapbox. IEM silently serves a default demo image (HTTP 200) for unknown events, so
/// the pre-flight JSON check is required to avoid posting wrong maps.
///
/// Mapbox fallback: Overlay geometry priority:
///   1. Alert's own GeoJSON polygon (when NWS provides one).
///   2. Union of the alert's UGC zone/county geometries (NetTopologySuite), which dissolves
///      shared borders between adjacent counties so only the outer perimeter is drawn — falls
///      back to a convex hull if still too large for the 8000-char URL limit.
///
/// Bounding box falls back to configured zones/counties if no geometry is available.
/// </summary>
public class MapService
{
    private readonly MapSettings _settings;
    private readonly LocationSettings _location;
    private readonly NwsZoneService _zones;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MapService> _logger;

    private double[]? _fallbackBbox;
    private readonly SemaphoreSlim _fallbackLock = new(1, 1);

    // Overlay colors — blue palette chosen for color-blind accessibility
    private const string FillColor   = "#0066CC";
    private const string StrokeColor = "#003D99";

    public MapService(
        MapSettings settings,
        LocationSettings location,
        NwsZoneService zones,
        IHttpClientFactory httpFactory,
        ILogger<MapService> logger)
    {
        _settings    = settings;
        _location    = location;
        _zones       = zones;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    /// <summary>
    /// Returns a map image URL for the alert, or null if map generation is disabled,
    /// unconfigured, or no bounding box could be determined.
    /// </summary>
    public async Task<string?> GetMapUrlAsync(NwsAlert alert)
    {
        if (!_settings.Enabled) return null;

        // IEM autoplot #217: SPS-specific map image (non-VTEC). Takes the IEM product ID
        // constructed from AWIPSidentifier (AFOS PIL) and WMOidentifier parsed from the alert.
        // Pre-flight GeoJSON check required: IEM returns HTTP 200 with a demo image for unknown
        // products (same behavior as #208), so we verify the SPS is indexed before using the URL.
        if (alert.AfosId?.StartsWith("SPS", StringComparison.Ordinal) == true)
        {
            var spsUrl = BuildIemSpsUrl(alert);
            if (spsUrl != null)
            {
                if (await VerifyIemSpsAsync(alert))
                {
                    _logger.LogInformation("Map: Using IEM autoplot 217 for SPS {AfosId}.", alert.AfosId);
                    return spsUrl;
                }
                _logger.LogWarning("Map: IEM has not yet indexed SPS {AfosId}; falling back to Mapbox.", alert.AfosId);
            }
            else
            {
                _logger.LogWarning("Map: Could not build IEM SPS URL for {AfosId} (missing or malformed WMO identifier); falling back to Mapbox.", alert.AfosId);
            }
        }

        // IEM autoplot #208: generates a PNG with the exact warning polygon, county lines,
        // and NEXRAD radar. Available for any alert with a VTEC code (the vast majority of
        // NWS alerts). Skip for CAN/EXP actions — those events display incorrectly on IEM.
        // Pre-flight JSON check required: IEM returns HTTP 200 with a default demo image
        // for unknown events instead of a 404, so we verify the event exists first.
        if (alert.VtecWfo != null && alert.VtecPhenomena != null &&
            alert.VtecSignificance != null && alert.VtecEtn != null &&
            alert.VtecAction is not ("CAN" or "EXP"))
        {
            var iemPhenom = await ResolveIemPhenomenaAsync(alert);
            if (iemPhenom != null)
            {
                _logger.LogInformation(
                    "Map: Using IEM autoplot for {Action}.{Wfo}.{Phenom}.{Sig} #{Etn} (IEM code: {IemPhenom}).",
                    alert.VtecAction, alert.VtecWfo, alert.VtecPhenomena, alert.VtecSignificance, alert.VtecEtn, iemPhenom);
                return BuildIemUrl(alert, iemPhenom);
            }

            _logger.LogWarning(
                "Map: IEM does not have {Phenom}.{Sig} #{Etn} ({Wfo}) — falling back to Mapbox.",
                alert.VtecPhenomena, alert.VtecSignificance, alert.VtecEtn, alert.VtecWfo);
        }

        // Fallback: Mapbox static image (used for non-VTEC events, CAN/EXP, or when IEM
        // doesn't have the event in its VTEC database)
        return await GetMapboxUrlAsync(alert);
    }

    // IEM sometimes uses a different phenomena code than NWS does in the VTEC string.
    // For example, NWS issues "HT.W" (Heat Warning) but IEM stores it as "XH" (Extreme Heat).
    // This table maps (NwsCode, Significance) → IEM alternative to try when the NWS code isn't found.
    private static readonly IReadOnlyDictionary<(string, string), string> IemPhenomenaAliases =
        new Dictionary<(string, string), string>
        {
            { ("HT", "W"), "XH" }, // Heat Warning → Extreme Heat Warning in IEM
            { ("XH", "W"), "HT" }, // reverse: if XH.W not found, try HT.W
            { ("EH", "W"), "XH" }, // Excessive Heat Warning → try XH as well
        };

    /// <summary>
    /// Queries IEM's VTEC JSON API to find the phenomena code IEM uses for this event.
    /// Returns the IEM phenomena code if found, null if the event is not in IEM's database.
    /// IEM returns HTTP 200 with a demo image for unknown events, so this pre-flight check
    /// prevents posting the wrong map. IEM may use a different code than the NWS VTEC string
    /// (e.g. NWS "HT.W" is stored as "XH.W" in IEM), so known aliases are tried as fallbacks.
    /// </summary>
    private async Task<string?> ResolveIemPhenomenaAsync(NwsAlert alert)
    {
        var http = _httpFactory.CreateClient("WeatherImagery");
        http.Timeout = TimeSpan.FromSeconds(10);

        // IEM's VTEC API uses the 4-letter WFO format (e.g. KMPX, not MPX)
        var wfo4 = alert.VtecWfo!.Length == 3 ? "K" + alert.VtecWfo : alert.VtecWfo;

        // Try the NWS code first, then any known IEM aliases
        var candidates = new List<string> { alert.VtecPhenomena! };
        if (IemPhenomenaAliases.TryGetValue((alert.VtecPhenomena!, alert.VtecSignificance!), out var alias))
            candidates.Add(alias);

        foreach (var phenom in candidates)
        {
            try
            {
                var url = "https://mesonet.agron.iastate.edu/json/vtec_event.py" +
                          $"?year={alert.Sent.Year}&wfo={wfo4}" +
                          $"&phenomena={phenom}&significance={alert.VtecSignificance}" +
                          $"&etn={alert.VtecEtn}";

                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                // IEM returns event_exists:false (no error key) when not found —
                // must check event_exists:true, not just absence of "error"
                if (doc.RootElement.TryGetProperty("event_exists", out var exists) &&
                    exists.ValueKind == JsonValueKind.True)
                    return phenom; // confirmed in IEM's database
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Map: IEM VTEC check failed for {Phenom} ({Error}).", phenom, ex.Message);
            }
        }

        return null; // not found under any known code
    }

    /// <summary>
    /// Returns a Mapbox static map URL for the alert, bypassing the IEM check.
    /// Called when the IEM image download fails after all retries.
    /// Returns null if Mapbox is not configured or no bounding box is available.
    /// </summary>
    public Task<string?> GetMapboxFallbackUrlAsync(NwsAlert alert)
    {
        if (!_settings.Enabled || string.IsNullOrEmpty(_settings.AccessToken)) return Task.FromResult<string?>(null);
        return GetMapboxUrlAsync(alert);
    }

    private async Task<string?> GetMapboxUrlAsync(NwsAlert alert)
    {
        if (string.IsNullOrEmpty(_settings.AccessToken)) return null;

        double[]? bbox    = null;
        string?   overlay = null;
        string?   hull    = null;

        // Priority 1: alert's own GeoJSON geometry
        if (!string.IsNullOrEmpty(alert.GeometryJson))
        {
            bbox    = ExtractBbox(alert.GeometryJson);
            overlay = alert.GeometryJson;
            if (bbox != null)
                _logger.LogInformation("Map: Using alert geometry polygon for {Id}.", alert.Id);
        }

        // Priority 2: zone/county geometries.
        // Try the full alert UGC codes first (entire warning area). Falls back to configured
        // monitoring codes if the full set returns no geometry.
        if (bbox == null && alert.GeocodeUgc.Count > 0)
        {
            _logger.LogInformation("Map: No alert geometry; fetching geometry for all {Total} UGC code(s).",
                alert.GeocodeUgc.Count);

            (bbox, overlay, hull) = await GetBboxAndOverlayAsync(alert.GeocodeUgc);

            if (bbox != null)
            {
                _logger.LogInformation("Map: Built overlay geometry from all {Count} UGC code(s) for {Id}.",
                    alert.GeocodeUgc.Count, alert.Id);
            }
            else
            {
                var configured = new HashSet<string>(
                    _location.Zones.Concat(_location.Counties), StringComparer.OrdinalIgnoreCase);
                var fallbackCodes = alert.GeocodeUgc.Where(c => configured.Contains(c)).ToList();
                if (fallbackCodes.Count == 0)
                    fallbackCodes = _location.Zones.Concat(_location.Counties).ToList();

                _logger.LogWarning("Map: Full UGC geometry fetch returned nothing; retrying with {Count} configured code(s).",
                    fallbackCodes.Count);

                (bbox, overlay, hull) = await GetBboxAndOverlayAsync(fallbackCodes);

                if (bbox != null)
                    _logger.LogInformation("Map: Built fallback overlay from {Count} configured code(s) for {Id}.",
                        fallbackCodes.Count, alert.Id);
                else
                    _logger.LogWarning("Map: UGC code geometry fetch returned nothing for {Id}.", alert.Id);
            }
        }

        // Priority 3: configured zones/counties bbox only — no overlay drawn
        if (bbox == null)
        {
            bbox = await GetFallbackBboxAsync();
            if (bbox != null)
                _logger.LogInformation("Map: No alert geometry; using configured zone/county bbox for {Id}.", alert.Id);
        }

        if (bbox == null)
        {
            _logger.LogWarning("Map: No bounding box available for alert {Id}.", alert.Id);
            return null;
        }

        return BuildMapboxUrl(bbox, overlay, hull);
    }

    // -------------------------------------------------------------------------
    // Bbox + overlay resolution
    // -------------------------------------------------------------------------

    private async Task<(double[]? Bbox, string? Overlay, string? Hull)> GetBboxAndOverlayAsync(IList<string> codes)
    {
        var geos = await Task.WhenAll(codes.Select(c => _zones.GetGeometryAsync(c)));

        var geometries = new List<Geometry>();
        foreach (var geo in geos)
        {
            if (geo == null) continue;
            var geom = PolygonGeometry.Parse(geo.Value.GetRawText());
            if (geom != null) geometries.Add(geom);
        }

        if (geometries.Count == 0) return (null, null, null);

        try
        {
            // Union dissolves shared borders between adjacent zones/counties automatically
            // (falling back to a simple MultiPolygon for disjoint pieces) — no manual edge-matching needed.
            var union = geometries[0].Factory.BuildGeometry(geometries).Union();
            _logger.LogInformation("Map: Unioned {Count} zone/county geometries.", geometries.Count);

            var env = union.EnvelopeInternal;
            double[] bbox = { env.MinX, env.MinY, env.MaxX, env.MaxY };
            string overlay = PolygonGeometry.ToGeoJson(union);
            string hull    = PolygonGeometry.ToGeoJson(union.ConvexHull());

            return (bbox, overlay, hull);
        }
        catch (Exception ex)
        {
            // Fall back to a bbox from each geometry's own envelope (cheap arithmetic, can't
            // fail the way Union()/ConvexHull() can on malformed input) — still lets the alert
            // get a bbox-only Mapbox image instead of losing the map entirely.
            _logger.LogWarning(ex, "Map: Union of {Count} zone/county geometries failed; using bbox only, no overlay.", geometries.Count);
            return (EnvelopeUnionBbox(geometries), null, null);
        }
    }

    private static double[] EnvelopeUnionBbox(IReadOnlyList<Geometry> geometries)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var g in geometries)
        {
            var env = g.EnvelopeInternal;
            if (env.MinX < minX) minX = env.MinX;
            if (env.MinY < minY) minY = env.MinY;
            if (env.MaxX > maxX) maxX = env.MaxX;
            if (env.MaxY > maxY) maxY = env.MaxY;
        }
        return new[] { minX, minY, maxX, maxY };
    }

    private async Task<double[]?> GetFallbackBboxAsync()
    {
        if (_fallbackBbox != null) return _fallbackBbox;

        await _fallbackLock.WaitAsync();
        try
        {
            if (_fallbackBbox != null) return _fallbackBbox;

            var codes = _location.Zones.Concat(_location.Counties).ToList();
            if (codes.Count == 0) return null;

            var (bbox, _, _) = await GetBboxAndOverlayAsync(codes);
            if (bbox == null) return null;

            _fallbackBbox = bbox;
            _logger.LogInformation("Map: Cached fallback bbox for {Count} configured zone(s)/county(s).", codes.Count);
            return _fallbackBbox;
        }
        finally
        {
            _fallbackLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // URL building
    // -------------------------------------------------------------------------

    private string BuildMapboxUrl(double[] bbox, string? geometryJson = null, string? hullJson = null)
    {
        double lonPad = Math.Max((bbox[2] - bbox[0]) * 0.10, 0.05);
        double latPad = Math.Max((bbox[3] - bbox[1]) * 0.10, 0.05);
        double west   = bbox[0] - lonPad;
        double south  = bbox[1] - latPad;
        double east   = bbox[2] + lonPad;
        double north  = bbox[3] + latPad;

        string bboxStr    = $"[{west:F4},{south:F4},{east:F4},{north:F4}]";
        string dimensions = $"{_settings.Width}x{_settings.Height}";
        string baseUrl    = $"https://api.mapbox.com/styles/v1/{_settings.Style}/static/";
        string suffix     = $"?access_token={_settings.AccessToken}";

        // Try primary overlay geometry (unioned zone/county shapes) at reducing precision
        if (!string.IsNullOrEmpty(geometryJson))
        {
            foreach (int precision in new[] { 2, 1 })
            {
                string? simplified = SimplifyGeometry(geometryJson, precision);
                if (simplified == null) break;

                string? feature = BuildFeatureJson(simplified);
                if (feature == null) break;
                string encoded   = Uri.EscapeDataString(feature);
                string candidate = $"{baseUrl}geojson({encoded})/{bboxStr}/{dimensions}{suffix}";

                if (candidate.Length <= 8000)
                {
                    _logger.LogInformation("Map: Overlay URL built at precision={Precision}, length={Length}.", precision, candidate.Length);
                    return candidate;
                }

                _logger.LogInformation("Map: Overlay at precision={Precision} is {Length} chars; retrying.", precision, candidate.Length);
            }

            _logger.LogWarning("Map: Overlay geometry too large even at precision=1; trying convex hull.");
        }

        // Convex hull fallback — always compact, but loses concave county shapes
        if (!string.IsNullOrEmpty(hullJson))
        {
            foreach (int precision in new[] { 2, 1 })
            {
                string? simplified = SimplifyGeometry(hullJson, precision);
                if (simplified == null) break;

                string? feature = BuildFeatureJson(simplified);
                if (feature == null) break;
                string encoded   = Uri.EscapeDataString(feature);
                string candidate = $"{baseUrl}geojson({encoded})/{bboxStr}/{dimensions}{suffix}";

                if (candidate.Length <= 8000)
                {
                    _logger.LogInformation("Map: Convex hull overlay URL built at precision={Precision}, length={Length}.", precision, candidate.Length);
                    return candidate;
                }
            }

            _logger.LogWarning("Map: Convex hull too large even at precision=1; posting without polygon.");
        }

        return $"{baseUrl}{bboxStr}/{dimensions}{suffix}";
    }

    private static string BuildIemUrl(NwsAlert alert, string iemPhenomena) =>
        $"https://mesonet.agron.iastate.edu/plotting/auto/plot/208/" +
        $"network:WFO::wfo:{alert.VtecWfo}::year:{alert.Sent.Year}::" +
        $"phenomenav:{iemPhenomena}::significancev:{alert.VtecSignificance}::" +
        $"etn:{alert.VtecEtn}::opt:single::n:auto::_r:t::dpi:100.png";

    // AFOS SPS PIL: "SPS" + 3-char WFO, letters only (e.g. "SPSMPX"). WMO6: 2-letter/2-digit/2-digit
    // bulletin header, uppercase alphanumeric (e.g. "WWUS83"). Unlike the VTEC fields used by
    // BuildIemUrl/ResolveIemPhenomenaAsync (already constrained by NwsAlertService.VtecPattern's
    // character classes before they ever reach MapService), AfosId/WmoIdentifier previously had
    // only a length check here — validate their character classes too before embedding them in an
    // IEM autoplot URL segment.
    private static readonly Regex AfosSpsPattern = new(@"^SPS[A-Z]{3}$", RegexOptions.Compiled);
    private static readonly Regex Wmo6Pattern     = new(@"^[A-Z0-9]{6}$", RegexOptions.Compiled);

    /// <summary>
    /// Builds an IEM autoplot #217 URL for a Special Weather Statement.
    /// PID format: YYYYMMDDHHmm-K{WFO}-{WMO6}-SPS{WFO}
    /// WFO is the last 3 chars of the 6-char AFOS PIL (e.g. "SPSMPX" → "MPX").
    /// WMO6 is the first 6 chars of WmoIdentifier (e.g. "WWUS83 KMPX 011045" → "WWUS83").
    /// The timestamp is parsed from the DDHHMM token in WmoIdentifier rather than alert.Sent,
    /// because NWS API processing can add 1-2 minutes of latency after WMO transmission.
    /// </summary>
    internal static string? BuildIemSpsUrl(NwsAlert alert)
    {
        if (alert.AfosId == null || !AfosSpsPattern.IsMatch(alert.AfosId)) return null;
        if (string.IsNullOrEmpty(alert.WmoIdentifier) || alert.WmoIdentifier.Length < 6) return null;

        string wfo  = alert.AfosId[3..];        // "MPX" from "SPSMPX"
        string wmo6 = alert.WmoIdentifier[..6]; // "WWUS83" from "WWUS83 KMPX 011045"
        if (!Wmo6Pattern.IsMatch(wmo6)) return null;

        // Parse DDHHMM from WMO header (e.g. "011045" from "WWUS83 KMPX 011045") for the PID
        // timestamp, combining with year/month from alert.Sent. Fall back to Sent if parsing fails.
        string ts;
        var wmoParts = alert.WmoIdentifier.Split(' ');
        if (wmoParts.Length >= 3 && wmoParts[2].Length == 6 &&
            int.TryParse(wmoParts[2][..2], out int dd) &&
            int.TryParse(wmoParts[2][2..4], out int hh) &&
            int.TryParse(wmoParts[2][4..], out int mm))
        {
            ts = $"{alert.Sent.UtcDateTime.Year}{alert.Sent.UtcDateTime.Month:D2}{dd:D2}{hh:D2}{mm:D2}";
        }
        else
        {
            ts = $"{alert.Sent.UtcDateTime:yyyyMMddHHmm}";
        }

        string pid = $"{ts}-K{wfo}-{wmo6}-{alert.AfosId}";
        return $"https://mesonet.agron.iastate.edu/plotting/auto/plot/217/" +
               $"pid:{pid}::segnum:0::n:auto::_r:t::dpi:100.png";
    }

    /// <summary>
    /// Queries IEM's active SPS GeoJSON feed to confirm the SPS has been indexed.
    /// IEM returns HTTP 200 with a demo image for unknown PIDs, so this pre-flight check
    /// prevents posting the wrong map. Matches by WFO and issue time (within 5 minutes).
    /// </summary>
    private async Task<bool> VerifyIemSpsAsync(NwsAlert alert)
    {
        if (alert.AfosId == null || !AfosSpsPattern.IsMatch(alert.AfosId)) return false;
        var wfo = alert.AfosId[3..]; // "MPX" from "SPSMPX"

        try
        {
            var http = _httpFactory.CreateClient("WeatherImagery");
            http.Timeout = TimeSpan.FromSeconds(10);
            var json = await http.GetStringAsync(
                $"https://mesonet.agron.iastate.edu/geojson/sps.geojson?wfo={Uri.EscapeDataString(wfo)}");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features)) return false;

            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var props)) continue;
                if (!props.TryGetProperty("issue", out var issueEl)) continue;
                var issueStr = issueEl.GetString();
                if (string.IsNullOrEmpty(issueStr)) continue;
                if (!DateTimeOffset.TryParse(issueStr, null,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var issue)) continue;
                if (Math.Abs((issue - alert.Sent).TotalMinutes) <= 5)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Map: IEM SPS pre-flight check failed for {AfosId} ({Error}).", alert.AfosId, ex.Message);
            return false;
        }
    }

    private static string? BuildFeatureJson(string geometryJson)
    {
        var geom = PolygonGeometry.Parse(geometryJson);
        if (geom == null) return null;

        try
        {
            var feature = new Feature(geom, new AttributesTable(new Dictionary<string, object>
            {
                ["fill"] = FillColor,
                ["fill-opacity"] = 0.3,
                ["stroke"] = StrokeColor,
                ["stroke-width"] = 2,
                ["stroke-opacity"] = 0.9,
            }));
            return JsonSerializer.Serialize(feature, PolygonGeometry.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Geometry helpers
    // -------------------------------------------------------------------------

    private static double[]? ExtractBbox(string geometryJson)
    {
        var geom = PolygonGeometry.Parse(geometryJson);
        if (geom == null) return null;

        try
        {
            var env = geom.EnvelopeInternal;
            return env.IsNull ? null : new[] { env.MinX, env.MinY, env.MaxX, env.MaxY };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Simplifies a GeoJSON geometry for a shorter Mapbox URL: reduces vertex count with a
    /// topology-preserving simplification (never produces self-intersecting output, unlike
    /// naive coordinate rounding) and snaps remaining coordinates to a coarser grid to shrink
    /// their decimal digit width. <paramref name="precision"/> follows the old "decimal places"
    /// convention (2 then 1) purely to keep call sites unchanged: 2 ≈ 0.01° tolerance/grid
    /// (~1.1km), 1 ≈ 0.1° (~11km).
    /// </summary>
    private static string? SimplifyGeometry(string geometryJson, int precision)
    {
        var geom = PolygonGeometry.Parse(geometryJson);
        if (geom == null) return null;

        try
        {
            double tolerance = precision >= 2 ? 0.01 : 0.1;
            var simplified = TopologyPreservingSimplifier.Simplify(geom, tolerance);
            if (simplified.IsEmpty) return null;

            var reduced = GeometryPrecisionReducer.Reduce(simplified, new PrecisionModel(1 / tolerance));
            return reduced.IsEmpty ? null : PolygonGeometry.ToGeoJson(reduced);
        }
        catch
        {
            // GeometryPrecisionReducer can throw (rather than return empty) on topologically
            // awkward input at coarse tolerances — treat like any other simplification failure.
            return null;
        }
    }
}
