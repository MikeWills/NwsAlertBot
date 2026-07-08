using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;

namespace NwsAlertBot.Services;

/// <summary>Shared GeoJSON geometry parsing/serialization and geometric queries (NetTopologySuite-backed), used by the SPC/WPC data-source services and MapService.</summary>
internal static class PolygonGeometry
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new GeoJsonConverterFactory());
        return options;
    }

    internal static (double Lat, double Lon)? ComputeCentroid(JsonElement geometry)
    {
        var geom = Parse(geometry.GetRawText());
        if (geom is not (Polygon or MultiPolygon)) return null;

        try
        {
            // For a MultiPolygon, centroid the largest sub-polygon by area rather than the whole
            // collection — the overall centroid can land in a gap between disjoint pieces or get
            // skewed by small islands.
            var target = geom is MultiPolygon multi
                ? Enumerable.Range(0, multi.NumGeometries).Select(multi.GetGeometryN).MaxBy(g => g.Area)!
                : geom;

            var centroid = target.Centroid;
            return centroid.IsEmpty ? null : (centroid.Y, centroid.X);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Point-in-polygon test against a GeoJSON Polygon/MultiPolygon geometry (honors holes).</summary>
    internal static bool PointInGeometry(JsonElement geometry, double lon, double lat)
    {
        var geom = Parse(geometry.GetRawText());
        if (geom is not (Polygon or MultiPolygon)) return false;

        try
        {
            var point = geom.Factory.CreatePoint(new Coordinate(lon, lat));
            return geom.Covers(point);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parses a raw GeoJSON geometry string (e.g. from <c>JsonElement.GetRawText()</c>) into an NTS geometry, or null if it isn't parseable.</summary>
    internal static Geometry? Parse(string geometryJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Geometry>(geometryJson, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static string ToGeoJson(Geometry geometry) => JsonSerializer.Serialize(geometry, JsonOptions);
}
