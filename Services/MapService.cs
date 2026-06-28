using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Generates Mapbox Static Images API URLs showing the affected area of each alert.
/// Overlay geometry priority: alert's own GeoJSON polygon → alert's UGC zone/county geometries.
/// Bounding box falls back to configured zones/counties if neither source has geometry.
/// </summary>
public class MapService
{
    private readonly MapSettings _settings;
    private readonly NwsSettings _nwsSettings;
    private readonly NwsZoneService _zones;
    private readonly ILogger<MapService> _logger;

    private double[]? _fallbackBbox;
    private readonly SemaphoreSlim _fallbackLock = new(1, 1);

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

        // Priority 2: union of the alert's geocode UGC zones/counties
        if (bbox == null && alert.GeocodeUgc.Count > 0)
        {
            _logger.LogInformation("Map: No alert geometry; fetching geometry for {Count} UGC code(s).", alert.GeocodeUgc.Count);
            (bbox, overlay) = await GetBboxAndOverlayAsync(alert.GeocodeUgc);
            if (bbox != null)
                _logger.LogInformation("Map: Built overlay from {Count} UGC code geometry(s) for {Id}.", alert.GeocodeUgc.Count, alert.Id);
            else
                _logger.LogWarning("Map: UGC code geometry fetch returned nothing for {Id}.", alert.Id);
        }

        // Priority 3: configured zones/counties bbox only — no overlay (this is the monitoring
        // area, not the alert area, so drawing it as a polygon would be misleading)
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
        var geoStrings = new List<string>();

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
        }

        if (!found) return (null, null);
        return (new[] { minLon, minLat, maxLon, maxLat }, CombineGeometries(geoStrings));
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

        // Overlay the alert polygon using Mapbox simplestyle GeoJSON.
        // Try precision=2 (~1 km) first; if the URL exceeds 8000 chars, retry at precision=1
        // (~10 km) which dramatically reduces point count for large multi-zone alerts.
        if (!string.IsNullOrEmpty(geometryJson))
        {
            foreach (int precision in new[] { 2, 1 })
            {
                string? simplified = SimplifyGeometry(geometryJson, precision);
                if (simplified == null) break;

                string feature = $"{{\"type\":\"Feature\",\"properties\":{{" +
                                 $"\"fill\":\"#ff6600\",\"fill-opacity\":0.3," +
                                 $"\"stroke\":\"#cc4400\",\"stroke-width\":2,\"stroke-opacity\":0.9" +
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

    /// <summary>
    /// Merges one or more GeoJSON geometry strings into a single geometry string at precision=2.
    /// Multiple geometries are combined into a MultiPolygon.
    /// </summary>
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
                        var rings = SimplifyRings(coords, 2);
                        if (rings.Count > 0) allPolygons.Add(rings);
                        break;
                    case "MultiPolygon":
                        foreach (var polygon in coords.EnumerateArray())
                        {
                            var pRings = SimplifyRings(polygon, 2);
                            if (pRings.Count > 0) allPolygons.Add(pRings);
                        }
                        break;
                }
            }
            catch { }
        }

        return BuildMultiPolygonJson(allPolygons);
    }

    /// <summary>
    /// Returns a simplified GeoJSON geometry string with coordinates rounded to
    /// <paramref name="precision"/> decimal places and consecutive duplicate points removed.
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

    /// <summary>
    /// Rounds each coordinate to <paramref name="precision"/> decimal places and drops
    /// consecutive duplicate points. Skips rings that collapse to fewer than 4 points
    /// (the GeoJSON minimum for a closed ring).
    /// </summary>
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
            // Ensure ring is closed (GeoJSON requires first == last)
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
