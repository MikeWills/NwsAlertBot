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

        return BuildMapboxUrl(bbox);
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

            var codes = _nwsSettings.Zones.Count > 0 ? _nwsSettings.Zones : _nwsSettings.Counties;
            if (codes.Count == 0) return null;

            _fallbackBbox = await GetBboxForCodesAsync(codes);
            if (_fallbackBbox != null)
                _logger.LogInformation("Map: Cached fallback bounding box for {Count} configured zone(s).", codes.Count);

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

    private string BuildMapboxUrl(double[] bbox)
    {
        // Pad 10% (minimum 0.05°) so the area isn't cropped to its exact edge
        double lonPad = Math.Max((bbox[2] - bbox[0]) * 0.10, 0.05);
        double latPad = Math.Max((bbox[3] - bbox[1]) * 0.10, 0.05);

        double west  = bbox[0] - lonPad;
        double south = bbox[1] - latPad;
        double east  = bbox[2] + lonPad;
        double north = bbox[3] + latPad;

        // Mapbox Static Images API — bounding-box auto-fit
        // https://docs.mapbox.com/api/maps/static-images/
        return $"https://api.mapbox.com/styles/v1/{_settings.Style}/static/" +
               $"[{west:F4},{south:F4},{east:F4},{north:F4}]/" +
               $"{_settings.Width}x{_settings.Height}" +
               $"?access_token={_settings.AccessToken}";
    }
}
