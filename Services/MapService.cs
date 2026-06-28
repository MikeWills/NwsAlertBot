using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Generates Mapbox Static Images API URLs showing the affected area of each alert.
/// Overlay geometry priority: alert's own GeoJSON polygon → alert's UGC zone/county geometries
/// → configured zones/counties. Bounding box follows the same priority order.
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
                _logger.LogDebug("Map: Using alert geometry polygon for {Id}.", alert.Id);
        }

        // Priority 2: union of the alert's geocode UGC zones/counties
        if (bbox == null && alert.GeocodeUgc.Count > 0)
        {
            (bbox, overlay) = await GetBboxAndOverlayAsync(alert.GeocodeUgc);
            if (bbox != null)
                _logger.LogDebug("Map: Using {Count} alert UGC code(s) for {Id}.", alert.GeocodeUgc.Count, alert.Id);
        }

        // Priority 3: configured zones/counties bbox only — no overlay (this is the monitoring
        // area, not the alert area, so drawing it as a polygon would be misleading)
        if (bbox == null)
        {
            bbox = await GetFallbackBboxAsync();
            if (bbox != null)
                _logger.LogDebug("Map: Using configured zone/county fallback bbox for {Id}.", alert.Id);
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
        // Coordinates are rounded to 2 decimal places (~1 km precision) and consecutive
        // duplicate points removed to keep the URL within Mapbox's 8192-byte limit.
        if (!string.IsNullOrEmpty(geometryJson))
        {
            string? simplified = SimplifyGeometry(geometryJson);
            if (simplified != null)
            {
                string feature = $"{{\"type\":\"Feature\",\"properties\":{{" +
                                 $"\"fill\":\"#ff6600\",\"fill-opacity\":0.3," +
                                 $"\"stroke\":\"#cc4400\",\"stroke-width\":2,\"stroke-opacity\":0.9" +
                                 $"}},\"geometry\":{simplified}}}";
                string encoded   = Uri.EscapeDataString(feature);
                string candidate = $"{baseUrl}geojson({encoded})/{bboxStr}/{dimensions}{suffix}";
                if (candidate.Length <= 8000)
                    return candidate;

                _logger.LogDebug("Map: Overlay URL exceeded 8000 chars ({Length}); posting without polygon.", candidate.Length);
            }
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
    /// Merges one or more GeoJSON geometry strings into a single geometry string.
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
                        var rings = SimplifyRings(coords);
                        if (rings.Count > 0) allPolygons.Add(rings);
                        break;
                    case "MultiPolygon":
                        foreach (var polygon in coords.EnumerateArray())
                        {
                            var pRings = SimplifyRings(polygon);
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
    /// Returns a simplified GeoJSON geometry string with coordinates rounded to 2 decimal
    /// places and consecutive duplicate points removed, or null if parsing fails.
    /// </summary>
    private static string? SimplifyGeometry(string geometryJson)
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
                "Polygon"      => BuildPolygonJson(SimplifyRings(coords)),
                "MultiPolygon" => BuildMultiPolygonJson(
                    coords.EnumerateArray()
                          .Select(p => SimplifyRings(p))
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
    /// Rounds each coordinate to 2 decimal places and drops consecutive duplicate points.
    /// Skips rings that collapse to fewer than 4 points (the GeoJSON minimum for a closed ring).
    /// </summary>
    private static List<List<(double Lon, double Lat)>> SimplifyRings(JsonElement rings)
    {
        var result = new List<List<(double Lon, double Lat)>>();
        foreach (var ring in rings.EnumerateArray())
        {
            var pts = new List<(double Lon, double Lat)>();
            (double Lon, double Lat) prev = (double.NaN, double.NaN);
            foreach (var pt in ring.EnumerateArray())
            {
                double lon = Math.Round(pt[0].GetDouble(), 2);
                double lat = Math.Round(pt[1].GetDouble(), 2);
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
                sb.Append($"[{ring[j].Lon:F2},{ring[j].Lat:F2}]");
            }
            sb.Append(']');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
