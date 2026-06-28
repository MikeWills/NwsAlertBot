using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Generates Mapbox Static Images API URLs showing the affected area of each alert.
/// Uses the alert's own GeoJSON geometry polygon when present; falls back to the
/// union bounding box of the configured NWS zones/counties (fetched once from the
/// NWS zone API and cached for the lifetime of the process).
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
    /// Resolution order: alert geometry polygon → alert geocode UGC zones → configured zones/counties.
    /// </summary>
    public async Task<string?> GetMapUrlAsync(NwsAlert alert)
    {
        if (!_settings.Enabled || string.IsNullOrEmpty(_settings.AccessToken)) return null;

        double[]? bbox = null;

        if (!string.IsNullOrEmpty(alert.GeometryJson))
            bbox = ExtractBbox(alert.GeometryJson);

        if (bbox == null && alert.GeocodeUgc.Count > 0)
            bbox = await GetBboxForCodesAsync(alert.GeocodeUgc);

        bbox ??= await GetFallbackBboxAsync();

        if (bbox == null)
        {
            _logger.LogWarning("Map: No bounding box available for alert {Id}.", alert.Id);
            return null;
        }

        return BuildMapboxUrl(bbox, alert.GeometryJson);
    }

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
        catch
        {
            return null;
        }
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

            _fallbackBbox = await GetBboxForCodesAsync(codes);
            if (_fallbackBbox != null)
                _logger.LogInformation("Map: Cached fallback bounding box for {Count} configured zone(s)/county(s).", codes.Count);

            return _fallbackBbox;
        }
        finally
        {
            _fallbackLock.Release();
        }
    }

    private async Task<double[]?> GetBboxForCodesAsync(IList<string> codes)
    {
        double minLon = double.MaxValue, minLat = double.MaxValue;
        double maxLon = double.MinValue, maxLat = double.MinValue;
        bool found = false;

        var geos = await Task.WhenAll(codes.Select(c => _zones.GetGeometryAsync(c)));

        foreach (var geo in geos)
        {
            if (geo == null) continue;

            var bbox = ExtractBbox(geo.Value.GetRawText());
            if (bbox == null) continue;

            if (bbox[0] < minLon) minLon = bbox[0];
            if (bbox[1] < minLat) minLat = bbox[1];
            if (bbox[2] > maxLon) maxLon = bbox[2];
            if (bbox[3] > maxLat) maxLat = bbox[3];
            found = true;
        }

        return found ? new[] { minLon, minLat, maxLon, maxLat } : null;
    }

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
        // duplicate points removed to keep the URL well within Mapbox's 8192-byte limit.
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
            }
        }

        // Mapbox Static Images API — bounding-box auto-fit (no overlay)
        // https://docs.mapbox.com/api/maps/static-images/
        return $"{baseUrl}{bboxStr}/{dimensions}{suffix}";
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
                "Polygon" => BuildPolygonJson(SimplifyRings(coords)),
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
