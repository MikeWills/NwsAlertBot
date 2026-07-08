using System.Text.Json;
using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class PolygonGeometryTests
{
    private static JsonElement Parse(string geoJson) => JsonDocument.Parse(geoJson).RootElement;

    [Fact]
    public void ComputeCentroid_SimplePolygon_ReturnsGeometricCenter()
    {
        // Rectangle spanning lon 0-10, lat 0-4 -> centroid (lon 5, lat 2).
        var geometry = Parse("""{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,4],[0,4],[0,0]]]}""");

        var centroid = PolygonGeometry.ComputeCentroid(geometry);

        Assert.NotNull(centroid);
        Assert.Equal(2.0, centroid.Value.Lat, precision: 6);
        Assert.Equal(5.0, centroid.Value.Lon, precision: 6);
    }

    [Fact]
    public void ComputeCentroid_MultiPolygon_UsesLargestSubPolygonNotOverallCentroid()
    {
        // A tiny far-away island plus a large square at the origin. The overall bounding
        // centroid would be skewed toward the island; ComputeCentroid must pick the large
        // polygon's own centroid instead.
        var geometry = Parse("""
            {"type":"MultiPolygon","coordinates":[
                [[[100,100],[101,100],[101,101],[100,101],[100,100]]],
                [[[0,0],[10,0],[10,10],[0,10],[0,0]]]
            ]}
            """);

        var centroid = PolygonGeometry.ComputeCentroid(geometry);

        Assert.NotNull(centroid);
        Assert.Equal(5.0, centroid.Value.Lat, precision: 6);
        Assert.Equal(5.0, centroid.Value.Lon, precision: 6);
    }

    [Fact]
    public void ComputeCentroid_ReturnsNullForNonPolygonGeometry()
    {
        var geometry = Parse("""{"type":"Point","coordinates":[5,5]}""");
        Assert.Null(PolygonGeometry.ComputeCentroid(geometry));
    }

    [Fact]
    public void PointInGeometry_ReturnsTrueForPointInsideOuterRing()
    {
        var geometry = Parse("""{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}""");
        Assert.True(PolygonGeometry.PointInGeometry(geometry, lon: 1, lat: 1));
    }

    [Fact]
    public void PointInGeometry_ReturnsFalseForPointOutsidePolygon()
    {
        var geometry = Parse("""{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}""");
        Assert.False(PolygonGeometry.PointInGeometry(geometry, lon: 20, lat: 20));
    }

    [Fact]
    public void PointInGeometry_ReturnsFalseForPointInsideAHole()
    {
        // Outer ring 0-10, hole carved out 4-6.
        var geometry = Parse("""
            {"type":"Polygon","coordinates":[
                [[0,0],[10,0],[10,10],[0,10],[0,0]],
                [[4,4],[6,4],[6,6],[4,6],[4,4]]
            ]}
            """);

        Assert.False(PolygonGeometry.PointInGeometry(geometry, lon: 5, lat: 5)); // inside the hole
        Assert.True(PolygonGeometry.PointInGeometry(geometry, lon: 1, lat: 1));  // inside outer, not in hole
    }

    [Fact]
    public void PointInGeometry_ChecksAllSubPolygonsOfAMultiPolygon()
    {
        var geometry = Parse("""
            {"type":"MultiPolygon","coordinates":[
                [[[0,0],[2,0],[2,2],[0,2],[0,0]]],
                [[[100,100],[102,100],[102,102],[100,102],[100,100]]]
            ]}
            """);

        Assert.True(PolygonGeometry.PointInGeometry(geometry, lon: 1, lat: 1));
        Assert.True(PolygonGeometry.PointInGeometry(geometry, lon: 101, lat: 101));
        Assert.False(PolygonGeometry.PointInGeometry(geometry, lon: 50, lat: 50));
    }

    [Fact]
    public void PointInGeometry_ReturnsFalseForNonPolygonGeometry()
    {
        var geometry = Parse("""{"type":"Point","coordinates":[1,1]}""");
        Assert.False(PolygonGeometry.PointInGeometry(geometry, lon: 1, lat: 1));
    }
}
