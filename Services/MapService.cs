using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NwsAlertBot.Config;
using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>
/// Generates Mapbox Static Images API URLs showing the affected area of each alert.
///
/// Overlay geometry priority:
///   1. Alert's own GeoJSON polygon (when NWS provides one)
///   2. MultiPolygon of the alert's UGC zone/county geometries (limited to configured zones),
///      uploaded to a temporary GitHub Gist when GitHubToken is set — this bypasses Mapbox's
///      8000-char URL limit and draws exact county shapes. Falls back to a convex hull when no
///      GitHub token is configured.
///
/// Bounding box falls back to configured zones/counties if neither source has geometry.
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

    // Tracks gist IDs that need deletion after the map image is downloaded.
    // Keyed by alert ID so the orchestrator can call CleanupAsync after DownloadMapImageAsync.
    private readonly ConcurrentDictionary<string, string> _pendingGistDeletions = new();

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
    /// Returns a Mapbox Static Images URL for the alert's area, or null if map generation
    /// is disabled, unconfigured, or no bounding box could be determined.
    /// </summary>
    public async Task<string?> GetMapUrlAsync(NwsAlert alert)
    {
        if (!_settings.Enabled || string.IsNullOrEmpty(_settings.AccessToken)) return null;

        double[]? bbox      = null;
        string? rawOverlay  = null; // full MultiPolygon — used for Gist upload
        string? hullOverlay = null; // convex hull — used for inline fallback

        // Priority 1: alert's own GeoJSON geometry
        if (!string.IsNullOrEmpty(alert.GeometryJson))
        {
            bbox       = ExtractBbox(alert.GeometryJson);
            rawOverlay = alert.GeometryJson;
            if (bbox != null)
                _logger.LogInformation("Map: Using alert geometry polygon for {Id}.", alert.Id);
        }

        // Priority 2: zone/county geometries, limited to configured monitoring codes.
        // Large alerts list 40+ zones; we intersect with our configured codes for a focused overlay.
        // If the intersection is empty (zone/county code format mismatch), fall back to all
        // configured codes — the alert covers them by definition since NWS returned it for our filter.
        if (bbox == null && alert.GeocodeUgc.Count > 0)
        {
            var configured = new HashSet<string>(
                _nwsSettings.Zones.Concat(_nwsSettings.Counties), StringComparer.OrdinalIgnoreCase);
            var relevant   = alert.GeocodeUgc.Where(c => configured.Contains(c)).ToList();
            var codesToUse = relevant.Count > 0
                ? relevant
                : _nwsSettings.Zones.Concat(_nwsSettings.Counties).ToList();

            _logger.LogInformation(
                "Map: No alert geometry; fetching geometry for {Use} of {Total} UGC code(s).",
                codesToUse.Count, alert.GeocodeUgc.Count);

            (bbox, rawOverlay, hullOverlay) = await GetBboxAndOverlayAsync(codesToUse);

            if (bbox != null)
                _logger.LogInformation("Map: Built overlay geometry from {Count} code(s) for {Id}.", codesToUse.Count, alert.Id);
            else
                _logger.LogWarning("Map: UGC code geometry fetch returned nothing for {Id}.", alert.Id);
        }

        // Priority 3: configured zones/counties bbox only — no overlay
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

        // Try Gist-based URL first (exact MultiPolygon, no URL size limit)
        if (!string.IsNullOrEmpty(_settings.GitHubToken) && !string.IsNullOrEmpty(rawOverlay))
        {
            var gistUrl = await TryBuildGistMapboxUrlAsync(alert.Id, bbox, rawOverlay);
            if (gistUrl != null) return gistUrl;
        }

        // Inline convex hull fallback (always fits in 8000 chars)
        return BuildMapboxUrl(bbox, hullOverlay);
    }

    /// <summary>
    /// Deletes the temporary Gist (if any) created for this alert.
    /// Call this immediately after the map image has been downloaded.
    /// </summary>
    public async Task CleanupAsync(string alertId)
    {
        if (_pendingGistDeletions.TryRemove(alertId, out var gistId))
        {
            _logger.LogInformation("Map: Deleting temporary Gist {GistId}.", gistId);
            await DeleteGistAsync(gistId);
        }
    }

    // -------------------------------------------------------------------------
    // Bbox + overlay resolution
    // -------------------------------------------------------------------------

    private async Task<(double[]? Bbox, string? RawGeometry, string? HullGeometry)> GetBboxAndOverlayAsync(IList<string> codes)
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

        double[] bbox      = new[] { minLon, minLat, maxLon, maxLat };
        string? raw2       = CombineGeometries(geoStrings);
        string? hull       = ConvexHullJson(allPoints);
        return (bbox, raw2, hull);
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

    private async Task<string?> TryBuildGistMapboxUrlAsync(string alertId, double[] bbox, string geometryJson)
    {
        string feature = BuildFeatureJson(geometryJson);
        var (gistId, rawUrl) = await CreateGistAsync(feature);
        if (gistId == null || rawUrl == null) return null;

        _pendingGistDeletions[alertId] = gistId;
        _logger.LogInformation("Map: Using Gist {GistId} overlay for {AlertId}.", gistId, alertId);

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
        string encoded    = Uri.EscapeDataString(rawUrl);

        return $"{baseUrl}geojson({encoded})/{bboxStr}/{dimensions}{suffix}";
    }

    private string BuildMapboxUrl(double[] bbox, string? geometryJson = null)
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
                    _logger.LogInformation("Map: Inline convex hull overlay at precision={Precision}, length={Length}.", precision, candidate.Length);
                    return candidate;
                }

                _logger.LogInformation("Map: Inline overlay at precision={Precision} is {Length} chars; retrying.", precision, candidate.Length);
            }

            _logger.LogWarning("Map: Convex hull too large even at precision=1; posting without polygon.");
        }

        return $"{baseUrl}{bboxStr}/{dimensions}{suffix}";
    }

    private static string BuildFeatureJson(string geometryJson) =>
        $"{{\"type\":\"Feature\",\"properties\":{{" +
        $"\"fill\":\"{FillColor}\",\"fill-opacity\":0.3," +
        $"\"stroke\":\"{StrokeColor}\",\"stroke-width\":2,\"stroke-opacity\":0.9" +
        $"}},\"geometry\":{geometryJson}}}";

    // -------------------------------------------------------------------------
    // GitHub Gist
    // -------------------------------------------------------------------------

    private async Task<(string? GistId, string? RawUrl)> CreateGistAsync(string featureJson)
    {
        try
        {
            var payload = $"{{\"public\":false,\"description\":\"NWS Alert Bot overlay (temporary)\"," +
                          $"\"files\":{{\"overlay.geojson\":{{\"content\":{JsonSerializer.Serialize(featureJson)}}}}}}}";

            using var client  = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/gists")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"token {_settings.GitHubToken}");
            request.Headers.Add("User-Agent", "NwsAlertBot");
            request.Headers.Add("Accept", "application/vnd.github.v3+json");

            var resp = await client.SendAsync(request);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Map: GitHub Gist creation failed. Status={Status}", resp.StatusCode);
                return (null, null);
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string? gistId = root.GetProperty("id").GetString();
            string? rawUrl = root.GetProperty("files")
                                 .GetProperty("overlay.geojson")
                                 .GetProperty("raw_url").GetString();

            return (gistId, rawUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Map: Exception creating GitHub Gist.");
            return (null, null);
        }
    }

    private async Task DeleteGistAsync(string gistId)
    {
        try
        {
            using var client  = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://api.github.com/gists/{gistId}");
            request.Headers.Add("Authorization", $"token {_settings.GitHubToken}");
            request.Headers.Add("User-Agent", "NwsAlertBot");
            request.Headers.Add("Accept", "application/vnd.github.v3+json");

            await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Map: Exception deleting GitHub Gist {GistId}.", gistId);
        }
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
