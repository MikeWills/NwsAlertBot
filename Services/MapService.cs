using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
///   2. Dissolved outer perimeter of the alert's UGC zone/county geometries. Adjacent
///      counties have their shared borders removed so only the outer edge is drawn. Falls
///      back to a MultiPolygon of individual county outlines if dissolve fails, then to a
///      convex hull if still too large for the 8000-char URL limit.
///
/// Bounding box falls back to configured zones/counties if no geometry is available.
/// </summary>
public class MapService
{
    private readonly MapSettings _settings;
    private readonly NwsSettings _nwsSettings;
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
        NwsSettings nwsSettings,
        NwsZoneService zones,
        IHttpClientFactory httpFactory,
        ILogger<MapService> logger)
    {
        _settings    = settings;
        _nwsSettings = nwsSettings;
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
        if (alert.AfosId?.StartsWith("SPS", StringComparison.OrdinalIgnoreCase) == true)
        {
            var spsUrl = BuildIemSpsUrl(alert);
            if (spsUrl != null)
            {
                _logger.LogInformation("Map: Using IEM autoplot 217 for SPS {AfosId}.", alert.AfosId);
                return spsUrl;
            }
            _logger.LogWarning("Map: Could not build IEM SPS URL for {AfosId} (missing WMO identifier?); falling back to Mapbox.", alert.AfosId);
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
        var http = _httpFactory.CreateClient();
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
        if (string.IsNullOrEmpty(_settings.AccessToken)) return Task.FromResult<string?>(null);
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
                    _nwsSettings.Zones.Concat(_nwsSettings.Counties), StringComparer.OrdinalIgnoreCase);
                var fallbackCodes = alert.GeocodeUgc.Where(c => configured.Contains(c)).ToList();
                if (fallbackCodes.Count == 0)
                    fallbackCodes = _nwsSettings.Zones.Concat(_nwsSettings.Counties).ToList();

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
        double minLon = double.MaxValue, minLat = double.MaxValue;
        double maxLon = double.MinValue, maxLat = double.MinValue;
        bool found = false;
        var geoStrings = new List<string>();
        var allPoints  = new List<(double Lon, double Lat)>();

        var geos = await Task.WhenAll(codes.Select(c => _zones.GetGeometryAsync(c)));

        foreach (var geo in geos)
        {
            if (geo == null) continue;
            string raw = geo.Value.GetRawText();
            var b = ExtractBbox(raw);
            if (b == null) continue;

            if (b[0] < minLon) minLon = b[0];
            if (b[1] < minLat) minLat = b[1];
            if (b[2] > maxLon) maxLon = b[2];
            if (b[3] > maxLat) maxLat = b[3];
            found = true;
            geoStrings.Add(raw);
            CollectPoints(raw, allPoints);
        }

        if (!found) return (null, null, null);

        double[] bbox = new[] { minLon, minLat, maxLon, maxLat };

        // Try dissolving shared borders first; fall back to individual county MultiPolygon.
        string? dissolved = DissolveGeometries(geoStrings);
        _logger.LogInformation(dissolved != null
            ? "Map: Dissolved {Count} geometries into outer perimeter."
            : "Map: Dissolve failed; using individual county polygons.",
            geoStrings.Count);

        string? overlay = dissolved ?? CombineGeometries(geoStrings);
        string? hull    = ConvexHullJson(allPoints);

        return (bbox, overlay, hull);
    }

    private async Task<double[]?> GetFallbackBboxAsync()
    {
        if (_fallbackBbox != null) return _fallbackBbox;

        await _fallbackLock.WaitAsync();
        try
        {
            if (_fallbackBbox != null) return _fallbackBbox;

            var codes = _nwsSettings.Zones.Concat(_nwsSettings.Counties).ToList();
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

        // Try primary overlay geometry (dissolved or combined) at reducing precision
        if (!string.IsNullOrEmpty(geometryJson))
        {
            foreach (int precision in new[] { 2, 1 })
            {
                string? simplified = SimplifyGeometry(geometryJson, precision);
                if (simplified == null) break;

                string feature   = BuildFeatureJson(simplified);
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

                string feature   = BuildFeatureJson(simplified);
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

    /// <summary>
    /// Builds an IEM autoplot #217 URL for a Special Weather Statement.
    /// PID format: YYYYMMDDHHmm-K{WFO}-{WMO6}-SPS{WFO}
    /// WFO comes from the last 3 chars of AfosId (e.g. "SPSMPX" → "MPX").
    /// WMO6 comes from the first 6 chars of WmoIdentifier (e.g. "WWUS83 KMPX 011045" → "WWUS83").
    /// </summary>
    private static string? BuildIemSpsUrl(NwsAlert alert)
    {
        if (alert.AfosId == null || alert.AfosId.Length < 4) return null;
        if (string.IsNullOrEmpty(alert.WmoIdentifier) || alert.WmoIdentifier.Length < 6) return null;

        string wfo    = alert.AfosId[3..];              // "MPX" from "SPSMPX"
        string wmo6   = alert.WmoIdentifier[..6];       // "WWUS83" from "WWUS83 KMPX 011045"
        string pid    = $"{alert.Sent.UtcDateTime:yyyyMMddHHmm}-K{wfo}-{wmo6}-{alert.AfosId}";

        return $"https://mesonet.agron.iastate.edu/plotting/auto/plot/217/" +
               $"pid:{pid}::segnum:0::n:auto::_r:t::dpi:100.png";
    }

    private static string BuildFeatureJson(string geometryJson) =>
        $"{{\"type\":\"Feature\",\"properties\":{{" +
        $"\"fill\":\"{FillColor}\",\"fill-opacity\":0.3," +
        $"\"stroke\":\"{StrokeColor}\",\"stroke-width\":2,\"stroke-opacity\":0.9" +
        $"}},\"geometry\":{geometryJson}}}";

    // -------------------------------------------------------------------------
    // Dissolve: remove shared county borders, return outer perimeter only
    // -------------------------------------------------------------------------

    /// <summary>
    /// Combines county/zone polygons by dissolving shared borders.
    /// Uses edge-counting: edges that appear in exactly one polygon are outer boundary edges;
    /// edges that appear twice are shared borders between adjacent counties and are removed.
    /// Returns a Polygon or MultiPolygon, or null if the topology reconstruction fails
    /// (e.g. when NWS zone data for adjacent counties doesn't have matching shared-border coordinates).
    /// </summary>
    private static string? DissolveGeometries(List<string> geometries)
    {
        if (geometries.Count == 0) return null;

        // Use precision=3 (~111m) for edge matching. This normalizes small floating-point
        // differences between adjacent county boundary definitions in the NWS zone API.
        const int matchPrecision = 3;

        // Count how many times each undirected edge appears across all exterior rings.
        // Canonical form: smaller endpoint first, so (A→B) and (B→A) share the same key.
        var edgeCount = new Dictionary<((double, double), (double, double)), int>();
        var edgeDirs  = new Dictionary<((double, double), (double, double)), ((double, double) From, (double, double) To)>();

        foreach (var geo in geometries)
        {
            foreach (var ring in ExtractExteriorRings(geo, matchPrecision))
            {
                for (int i = 0; i < ring.Count - 1; i++)
                {
                    var a = (ring[i].Lon, ring[i].Lat);
                    var b = (ring[i + 1].Lon, ring[i + 1].Lat);
                    var key = CanonicalEdge(a, b);

                    if (!edgeCount.TryGetValue(key, out int c))
                    {
                        edgeCount[key] = 1;
                        edgeDirs[key]  = (a, b);
                    }
                    else
                    {
                        edgeCount[key] = c + 1;
                    }
                }
            }
        }

        // Build directed adjacency from outer edges only (count == 1).
        // Each outer boundary point should have exactly one outgoing edge.
        var adj = new Dictionary<(double, double), (double, double)>();
        foreach (var (key, count) in edgeCount)
        {
            if (count != 1) continue;
            var (from, to) = edgeDirs[key];
            if (adj.ContainsKey(from))
                return null; // topology error — multiple outgoing edges from same point
            adj[from] = to;
        }

        if (adj.Count == 0) return null;

        // Walk the adjacency chains to reconstruct closed rings.
        var rings   = new List<List<(double Lon, double Lat)>>();
        var visited = new HashSet<(double, double)>();

        foreach (var startKey in adj.Keys.ToList())
        {
            if (visited.Contains(startKey)) continue;

            var ring    = new List<(double Lon, double Lat)> { (startKey.Item1, startKey.Item2) };
            var current = startKey;
            visited.Add(current);

            while (true)
            {
                if (!adj.TryGetValue(current, out var next))
                    return null; // dead end — shared borders didn't cancel cleanly

                if (next == startKey)
                {
                    ring.Add((startKey.Item1, startKey.Item2)); // close the ring
                    break;
                }

                if (visited.Contains(next))
                    return null; // unexpected loop — broken topology

                ring.Add((next.Item1, next.Item2));
                visited.Add(next);
                current = next;
            }

            if (ring.Count >= 4)
                rings.Add(ring);
        }

        if (rings.Count == 0) return null;

        if (rings.Count == 1)
            return BuildPolygonJson(new List<List<(double Lon, double Lat)>> { rings[0] });

        return BuildMultiPolygonJson(
            rings.Select(r => new List<List<(double Lon, double Lat)>> { r }).ToList());
    }

    /// <summary>
    /// Returns the canonical (undirected) form of an edge: smaller endpoint first.
    /// </summary>
    private static ((double, double), (double, double)) CanonicalEdge((double, double) a, (double, double) b)
    {
        int cmp = a.Item1.CompareTo(b.Item1);
        if (cmp == 0) cmp = a.Item2.CompareTo(b.Item2);
        return cmp <= 0 ? (a, b) : (b, a);
    }

    /// <summary>
    /// Parses a GeoJSON geometry and returns only the exterior ring of each polygon,
    /// with coordinates rounded to the given precision. Interior rings (holes) are dropped.
    /// </summary>
    private static List<List<(double Lon, double Lat)>> ExtractExteriorRings(string geometryJson, int precision)
    {
        var result = new List<List<(double Lon, double Lat)>>();
        try
        {
            using var doc = JsonDocument.Parse(geometryJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) ||
                !root.TryGetProperty("coordinates", out var coords)) return result;

            void AddExteriorRing(JsonElement polygon)
            {
                // First element of a polygon's coordinate array is the exterior ring
                using var ringEnum = polygon.EnumerateArray();
                if (!ringEnum.MoveNext()) return;
                var exteriorRingEl = ringEnum.Current;

                var pts = new List<(double Lon, double Lat)>();
                (double Lon, double Lat) prev = (double.NaN, double.NaN);

                foreach (var pt in exteriorRingEl.EnumerateArray())
                {
                    double lon = Math.Round(pt[0].GetDouble(), precision);
                    double lat = Math.Round(pt[1].GetDouble(), precision);
                    if (lon == prev.Lon && lat == prev.Lat) continue;
                    pts.Add((lon, lat));
                    prev = (lon, lat);
                }

                // Ensure ring is closed
                if (pts.Count >= 2 && (pts[0].Lon != pts[^1].Lon || pts[0].Lat != pts[^1].Lat))
                    pts.Add(pts[0]);

                if (pts.Count >= 4)
                    result.Add(pts);
            }

            switch (typeEl.GetString())
            {
                case "Polygon":
                    AddExteriorRing(coords);
                    break;
                case "MultiPolygon":
                    foreach (var polygon in coords.EnumerateArray())
                        AddExteriorRing(polygon);
                    break;
            }
        }
        catch { }

        return result;
    }

    // -------------------------------------------------------------------------
    // Geometry helpers
    // -------------------------------------------------------------------------

    private static double[]? ExtractBbox(string geometryJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(geometryJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return null;
            if (!root.TryGetProperty("coordinates", out var coords)) return null;

            double minLon = double.MaxValue, minLat = double.MaxValue;
            double maxLon = double.MinValue, maxLat = double.MinValue;

            void Visit(JsonElement pt)
            {
                var lon = pt[0].GetDouble();
                var lat = pt[1].GetDouble();
                if (lon < minLon) minLon = lon;
                if (lon > maxLon) maxLon = lon;
                if (lat < minLat) minLat = lat;
                if (lat > maxLat) maxLat = lat;
            }

            switch (typeEl.GetString())
            {
                case "Polygon":
                    foreach (var ring in coords.EnumerateArray())
                        foreach (var pt in ring.EnumerateArray()) Visit(pt);
                    break;
                case "MultiPolygon":
                    foreach (var polygon in coords.EnumerateArray())
                        foreach (var ring in polygon.EnumerateArray())
                            foreach (var pt in ring.EnumerateArray()) Visit(pt);
                    break;
                default:
                    return null;
            }

            return minLon == double.MaxValue ? null : new[] { minLon, minLat, maxLon, maxLat };
        }
        catch { return null; }
    }

    private static void CollectPoints(string geometryJson, List<(double Lon, double Lat)> points)
    {
        try
        {
            using var doc = JsonDocument.Parse(geometryJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) ||
                !root.TryGetProperty("coordinates", out var coords)) return;

            void VisitRing(JsonElement ring)
            {
                foreach (var pt in ring.EnumerateArray())
                    if (pt.GetArrayLength() >= 2)
                        points.Add((pt[0].GetDouble(), pt[1].GetDouble()));
            }

            switch (typeEl.GetString())
            {
                case "Polygon":
                    foreach (var ring in coords.EnumerateArray()) VisitRing(ring);
                    break;
                case "MultiPolygon":
                    foreach (var polygon in coords.EnumerateArray())
                        foreach (var ring in polygon.EnumerateArray()) VisitRing(ring);
                    break;
            }
        }
        catch { }
    }

    private static string? CombineGeometries(List<string> geometries)
    {
        if (geometries.Count == 0) return null;
        if (geometries.Count == 1) return geometries[0];

        var allPolygons = new List<List<List<(double Lon, double Lat)>>>();

        foreach (var geo in geometries)
        {
            try
            {
                using var doc = JsonDocument.Parse(geo);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) ||
                    !root.TryGetProperty("coordinates", out var coords)) continue;

                switch (typeEl.GetString())
                {
                    case "Polygon":
                        var rings = SimplifyRings(coords, 4);
                        if (rings.Count > 0) allPolygons.Add(rings);
                        break;
                    case "MultiPolygon":
                        foreach (var polygon in coords.EnumerateArray())
                        {
                            var pRings = SimplifyRings(polygon, 4);
                            if (pRings.Count > 0) allPolygons.Add(pRings);
                        }
                        break;
                }
            }
            catch { }
        }

        return BuildMultiPolygonJson(allPolygons);
    }

    private static string? ConvexHullJson(List<(double Lon, double Lat)> points)
    {
        var hull = GrahamScan(points);
        if (hull == null || hull.Count < 3) return null;

        hull.Add(hull[0]);

        var sb = new StringBuilder("{\"type\":\"Polygon\",\"coordinates\":[[");
        for (int i = 0; i < hull.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"[{hull[i].Lon:F2},{hull[i].Lat:F2}]");
        }
        sb.Append("]]}");
        return sb.ToString();
    }

    private static List<(double Lon, double Lat)>? GrahamScan(List<(double Lon, double Lat)> points)
    {
        if (points.Count < 3) return null;

        var pivot  = points.MinBy(p => (p.Lat, p.Lon));
        var sorted = points
            .Where(p => p != pivot)
            .OrderBy(p => Math.Atan2(p.Lat - pivot.Lat, p.Lon - pivot.Lon))
            .ThenBy(p => DistSq(pivot, p))
            .ToList();

        var hull = new List<(double Lon, double Lat)> { pivot };
        foreach (var pt in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], pt) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(pt);
        }

        return hull.Count >= 3 ? hull : null;
    }

    private static double Cross((double Lon, double Lat) o, (double Lon, double Lat) a, (double Lon, double Lat) b)
        => (a.Lon - o.Lon) * (b.Lat - o.Lat) - (a.Lat - o.Lat) * (b.Lon - o.Lon);

    private static double DistSq((double Lon, double Lat) a, (double Lon, double Lat) b)
        => (a.Lon - b.Lon) * (a.Lon - b.Lon) + (a.Lat - b.Lat) * (a.Lat - b.Lat);

    private static string? SimplifyGeometry(string geometryJson, int precision)
    {
        try
        {
            using var doc = JsonDocument.Parse(geometryJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) ||
                !root.TryGetProperty("coordinates", out var coords))
                return null;

            return typeEl.GetString() switch
            {
                "Polygon"      => BuildPolygonJson(SimplifyRings(coords, precision)),
                "MultiPolygon" => BuildMultiPolygonJson(
                    coords.EnumerateArray()
                          .Select(p => SimplifyRings(p, precision))
                          .Where(r => r.Count > 0)
                          .ToList()),
                _ => null
            };
        }
        catch { return null; }
    }

    private static string? BuildPolygonJson(List<List<(double Lon, double Lat)>> rings)
    {
        if (rings.Count == 0) return null;
        return $"{{\"type\":\"Polygon\",\"coordinates\":{SerializeRings(rings)}}}";
    }

    private static string? BuildMultiPolygonJson(List<List<List<(double Lon, double Lat)>>> polygons)
    {
        if (polygons.Count == 0) return null;
        var sb = new StringBuilder("{\"type\":\"MultiPolygon\",\"coordinates\":[");
        for (int i = 0; i < polygons.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(SerializeRings(polygons[i]));
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static List<List<(double Lon, double Lat)>> SimplifyRings(JsonElement rings, int precision)
    {
        var result = new List<List<(double Lon, double Lat)>>();
        foreach (var ring in rings.EnumerateArray())
        {
            var pts = new List<(double Lon, double Lat)>();
            (double Lon, double Lat) prev = (double.NaN, double.NaN);
            foreach (var pt in ring.EnumerateArray())
            {
                double lon = Math.Round(pt[0].GetDouble(), precision);
                double lat = Math.Round(pt[1].GetDouble(), precision);
                if (lon == prev.Lon && lat == prev.Lat) continue;
                pts.Add((lon, lat));
                prev = (lon, lat);
            }
            if (pts.Count >= 2 && (pts[0].Lon != pts[^1].Lon || pts[0].Lat != pts[^1].Lat))
                pts.Add(pts[0]);
            if (pts.Count >= 4)
                result.Add(pts);
        }
        return result;
    }

    private static string SerializeRings(List<List<(double Lon, double Lat)>> rings)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < rings.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[');
            var ring = rings[i];
            for (int j = 0; j < ring.Count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append($"[{ring[j].Lon},{ring[j].Lat}]");
            }
            sb.Append(']');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
