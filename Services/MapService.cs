using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Generates Mapbox Static Images API URLs showing the affected area of each alert.
/// Overlay geometry priority: alert's own GeoJSON polygon → convex hull of alert's UGC
/// zone/county geometries. Bounding box falls back to configured zones/counties if neither
/// source has geometry.
/// </summary>
public class MapService
{
    private readonly MapSettings _settings;
    private readonly NwsSettings _nwsSettings;
    private readonly NwsZoneService _zones;
    private readonly ILogger<MapService> _logger;

    private double[]? _fallbackBbox;
    private readonly SemaphoreSlim _fallbackLock = new(1, 1);

    // Overlay colors — blue palette chosen for color-blind accessibility
    // (most color blindness affects red-green perception; blue is universally distinguishable)
    private const string FillColor   = "#0066CC";
    private const string StrokeColor = "#003D99";

    public MapService(MapSettings settings, NwsSettings nwsSettings, NwsZoneService zones, ILogger<MapService> logger)
    {
        _settings = settings;
        _nwsSettings = nwsSettings;
        _zones = zones;
        _logger = logger;
    }

    /// <summary>
    /// Returns a Mapbox Static Images URL for the alert's area, or null if map generation
    /// is disabled, unconfigured, or no bounding box could be determined.
    /// </summary>
    public async Task<string?> GetMapUrlAsync(NwsAlert alert)
    {
        if (!_settings.Enabled || string.IsNullOrEmpty(_settings.AccessToken)) return null;

        double[]? bbox = null;
        string? overlay = null;

        // Priority 1: alert's own GeoJSON geometry
        if (!string.IsNullOrEmpty(alert.GeometryJson))
        {
            bbox    = ExtractBbox(alert.GeometryJson);
            overlay = alert.GeometryJson;
            if (bbox != null)
                _logger.LogInformation("Map: Using alert geometry polygon for {Id}.", alert.Id);
        }

        // Priority 2: convex hull of UGC zone/county geometries, limited to codes within our
        // monitoring area. Large alerts (e.g. statewide warnings) may list 40+ zones; a convex
        // hull of all their combined points produces a single compact polygon that fits in the
        // Mapbox URL limit regardless of how many zones are involved.
        // Since NWS only returns this alert because it covers our configured zones, intersecting
        // the alert's UGC list with our configured codes gives a focused, accurate overlay.
        // If the intersection is empty (e.g. alert uses county codes, config uses zone codes for
        // the same area), fall back to all configured codes — the alert covers them by definition.
        if (bbox == null && alert.GeocodeUgc.Count > 0)
        {
            var configured = new HashSet<string>(
                _nwsSettings.Zones.Concat(_nwsSettings.Counties), StringComparer.OrdinalIgnoreCase);
            var relevant = alert.GeocodeUgc.Where(c => configured.Contains(c)).ToList();
            var codesToUse = relevant.Count > 0
                ? relevant
                : _nwsSettings.Zones.Concat(_nwsSettings.Counties).ToList();

            _logger.LogInformation(
                "Map: No alert geometry; using {Use} of {Total} UGC code(s) for overlay.",
                codesToUse.Count, alert.GeocodeUgc.Count);

            (bbox, overlay) = await GetBboxAndOverlayAsync(codesToUse);
            if (bbox != null)
                _logger.LogInformation("Map: Built convex hull overlay from {Count} code(s) for {Id}.", codesToUse.Count, alert.Id);
            else
                _logger.LogWarning("Map: UGC code geometry fetch returned nothing for {Id}.", alert.Id);
        }

        // Priority 3: configured zones/counties bbox only — no overlay (the monitoring area is
        // not the same as the alert area, so drawing it as a polygon would be misleading)
        if (bbox == null)
        {
            bbox = await GetFallbackBboxAsync();
            if (bbox != null)
                _logger.LogInformation("Map: No alert geometry found; using configured zone/county bbox for {Id}.", alert.Id);
        }

        if (bbox == null)
        {
            _logger.LogWarning("Map: No bounding box available for alert {Id}.", alert.Id);
            return null;
        }

        return BuildMapboxUrl(bbox, overlay);
    }

    // -------------------------------------------------------------------------
    // Bbox + overlay resolution
    // -------------------------------------------------------------------------

    private async Task<(double[]? Bbox, string? Overlay)> GetBboxAndOverlayAsync(IList<string> codes)
    {
        double minLon = double.MaxValue, minLat = double.MaxValue;
        double maxLon = double.MinValue, maxLat = double.MinValue;
        bool found = false;
        var allPoints = new List<(double Lon, double Lat)>();

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
            CollectPoints(raw, allPoints);
        }

        if (!found) return (null, null);

        string? overlay = ConvexHullJson(allPoints);
        return (new[] { minLon, minLat, maxLon, maxLat }, overlay);
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

            var (bbox, _) = await GetBboxAndOverlayAsync(codes);
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

    private string BuildMapboxUrl(double[] bbox, string? geometryJson = null)
    {
        // Pad 10% (minimum 0.05°) so the area isn't cropped to its exact edge
        double lonPad = Math.Max((bbox[2] - bbox[0]) * 0.10, 0.05);
        double latPad = Math.Max((bbox[3] - bbox[1]) * 0.10, 0.05);

        double west  = bbox[0] - lonPad;
        double south = bbox[1] - latPad;
        double east  = bbox[2] + lonPad;
        double north = bbox[3] + latPad;

        string bboxStr    = $"[{west:F4},{south:F4},{east:F4},{north:F4}]";
        string dimensions = $"{_settings.Width}x{_settings.Height}";
        string baseUrl    = $"https://api.mapbox.com/styles/v1/{_settings.Style}/static/";
        string suffix     = $"?access_token={_settings.AccessToken}";

        // Zone-based overlays come in as pre-computed convex hulls (already compact).
        // Alert-geometry overlays may still be large; try precision=2 then precision=1.
        if (!string.IsNullOrEmpty(geometryJson))
        {
            foreach (int precision in new[] { 2, 1 })
            {
                string? simplified = SimplifyGeometry(geometryJson, precision);
                if (simplified == null) break;

                string feature = $"{{\"type\":\"Feature\",\"properties\":{{" +
                                 $"\"fill\":\"{FillColor}\",\"fill-opacity\":0.3," +
                                 $"\"stroke\":\"{StrokeColor}\",\"stroke-width\":2,\"stroke-opacity\":0.9" +
                                 $"}},\"geometry\":{simplified}}}";
                string encoded   = Uri.EscapeDataString(feature);
                string candidate = $"{baseUrl}geojson({encoded})/{bboxStr}/{dimensions}{suffix}";

                if (candidate.Length <= 8000)
                {
                    _logger.LogInformation("Map: Overlay URL built at precision={Precision}, length={Length}.", precision, candidate.Length);
                    return candidate;
                }

                _logger.LogInformation("Map: Overlay at precision={Precision} is {Length} chars (limit 8000); retrying.", precision, candidate.Length);
            }

            _logger.LogWarning("Map: Overlay geometry too large even at precision=1; posting without polygon.");
        }

        return $"{baseUrl}{bboxStr}/{dimensions}{suffix}";
    }

    // -------------------------------------------------------------------------
    // Geometry helpers
    // -------------------------------------------------------------------------

    private double[]? ExtractBbox(string geometryJson)
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
                        foreach (var pt in ring.EnumerateArray())
                            Visit(pt);
                    break;
                case "MultiPolygon":
                    foreach (var polygon in coords.EnumerateArray())
                        foreach (var ring in polygon.EnumerateArray())
                            foreach (var pt in ring.EnumerateArray())
                                Visit(pt);
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

    /// <summary>
    /// Computes the convex hull of the given points using Graham scan and returns it as a
    /// GeoJSON Polygon string, or null if fewer than 3 distinct points are available.
    /// The hull is a single compact polygon — always well within Mapbox's URL length limit.
    /// </summary>
    private static string? ConvexHullJson(List<(double Lon, double Lat)> points)
    {
        var hull = GrahamScan(points);
        if (hull == null || hull.Count < 3) return null;

        hull.Add(hull[0]); // close the ring

        var sb = new StringBuilder("{\"type\":\"Polygon\",\"coordinates\":[[");
        for (int i = 0; i < hull.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"[{hull[i].Lon:F2},{hull[i].Lat:F2}]");
        }
        sb.Append("]]}");
        return sb.ToString();
    }

    /// <summary>
    /// Graham scan convex hull. Returns the hull vertices in counter-clockwise order,
    /// or null if fewer than 3 points are provided.
    /// </summary>
    private static List<(double Lon, double Lat)>? GrahamScan(List<(double Lon, double Lat)> points)
    {
        if (points.Count < 3) return null;

        // Pivot: lowest latitude (southernmost), break ties by leftmost longitude
        var pivot = points.MinBy(p => (p.Lat, p.Lon));

        // Sort remaining points by polar angle from pivot, then by distance
        var sorted = points
            .Where(p => p != pivot)
            .OrderBy(p => Math.Atan2(p.Lat - pivot.Lat, p.Lon - pivot.Lon))
            .ThenBy(p => DistSq(pivot, p))
            .ToList();

        var hull = new List<(double Lon, double Lat)> { pivot };

        foreach (var pt in sorted)
        {
            // Remove points that make a clockwise turn (or are collinear)
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

    /// <summary>
    /// Returns a simplified GeoJSON geometry string with coordinates rounded to
    /// <paramref name="precision"/> decimal places and consecutive duplicate points removed.
    /// Used for alert-owned geometry (Priority 1); zone overlays use convex hull instead.
    /// </summary>
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
