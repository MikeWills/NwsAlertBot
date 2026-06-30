using System.Text.Json;

namespace NwsAlertBot.Services;

/// <summary>Shared polygon centroid geometry used by SPC data-source services.</summary>
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
