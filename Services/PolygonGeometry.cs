using System.Linq;
using System.Text.Json;

namespace NwsAlertBot.Services;

/// <summary>Shared polygon centroid/point-in-polygon geometry used by SPC/WPC data-source services.</summary>
internal static class PolygonGeometry
{
    internal static (double Lat, double Lon)? ComputeCentroid(JsonElement geometry)
    {
        var type   = geometry.GetProperty("type").GetString();
        var coords = geometry.GetProperty("coordinates");

        JsonElement exterior;
        switch (type)
        {
            case "Polygon":
                exterior = coords[0];
                break;
            case "MultiPolygon":
                JsonElement? largest = null;
                double bestArea = -1;
                foreach (var poly in coords.EnumerateArray())
                {
                    var ring = poly[0];
                    double area = Math.Abs(RingSignedArea(ring));
                    if (area > bestArea) { bestArea = area; largest = ring; }
                }
                if (largest == null) return null;
                exterior = largest.Value;
                break;
            default:
                return null;
        }
        return RingCentroid(exterior);
    }

    /// <summary>Point-in-polygon test against a GeoJSON Polygon/MultiPolygon geometry (ray casting, honors holes).</summary>
    internal static bool PointInGeometry(JsonElement geometry, double lon, double lat)
    {
        var type   = geometry.GetProperty("type").GetString();
        var coords = geometry.GetProperty("coordinates");

        return type switch
        {
            "Polygon"      => PointInPolygonRings(coords, lon, lat),
            "MultiPolygon" => coords.EnumerateArray().Any(poly => PointInPolygonRings(poly, lon, lat)),
            _              => false,
        };
    }

    private static bool PointInPolygonRings(JsonElement rings, double lon, double lat)
    {
        if (!PointInRing(rings[0], lon, lat)) return false;

        for (int i = 1; i < rings.GetArrayLength(); i++)
            if (PointInRing(rings[i], lon, lat)) return false; // inside a hole

        return true;
    }

    private static bool PointInRing(JsonElement ring, double lon, double lat)
    {
        bool inside = false;
        int n = ring.GetArrayLength();

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var pi = ring[i];
            var pj = ring[j];
            double xi = pi[0].GetDouble(), yi = pi[1].GetDouble();
            double xj = pj[0].GetDouble(), yj = pj[1].GetDouble();

            if ((yi > lat) != (yj > lat) &&
                lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
                inside = !inside;
        }

        return inside;
    }

    private static double RingSignedArea(JsonElement ring)
    {
        double area = 0;
        int n = ring.GetArrayLength();
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[i][0].GetDouble(), yi = ring[i][1].GetDouble();
            double xj = ring[j][0].GetDouble(), yj = ring[j][1].GetDouble();
            area += xj * yi - xi * yj;
        }
        return area / 2.0;
    }

    private static (double Lat, double Lon)? RingCentroid(JsonElement ring)
    {
        double area = RingSignedArea(ring);
        int n = ring.GetArrayLength();
        if (Math.Abs(area) < 1e-12)
        {
            double sLon = 0, sLat = 0;
            for (int i = 0; i < n; i++) { sLon += ring[i][0].GetDouble(); sLat += ring[i][1].GetDouble(); }
            return n == 0 ? null : (sLat / n, sLon / n);
        }
        double cx = 0, cy = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[i][0].GetDouble(), yi = ring[i][1].GetDouble();
            double xj = ring[j][0].GetDouble(), yj = ring[j][1].GetDouble();
            double cross = xj * yi - xi * yj;
            cx += (xj + xi) * cross;
            cy += (yj + yi) * cross;
        }
        double factor = 1.0 / (6.0 * area);
        return (cy * factor, cx * factor);
    }
}
