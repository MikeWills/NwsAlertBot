using NwsAlertBot.Services;
using static NwsAlertBot.Services.WpcWssiService;

namespace NwsAlertBot.Tests;

public class WpcWssiServiceTests
{
    // Trimmed from a live MapServer/1/query response (2026-11-22). Nested polygons mean one
    // point returns every tier up to its highest.
    private const string SampleResponse = """
        {"displayFieldName":"product","features":[
          {"attributes":{"impact":"WINTER WEATHER AREA","issue_time":"2026-11-22 0114Z","start_time":"00Z 11/22/26","end_time":"00Z 11/23/26"}},
          {"attributes":{"impact":"MINOR","issue_time":"2026-11-22 0114Z","start_time":"00Z 11/22/26","end_time":"00Z 11/23/26"}},
          {"attributes":{"impact":"MODERATE","issue_time":"2026-11-22 0114Z","start_time":"00Z 11/22/26","end_time":"00Z 11/23/26"}}
        ]}
        """;

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static int Minor => GetImpactRank("Minor");

    private static WssiHit Hit(string component, string impact, string issue = "2026-11-22 0114Z", string end = "00Z 11/23/26")
        => new(component, impact, issue, "00Z 11/22/26", end);

    [Fact]
    public void ParseFeatures_ReadsEveryFeature()
    {
        var hits = ParseFeatures(SampleResponse, "Overall");

        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal("Overall", h.Component));
        Assert.Equal("MODERATE", hits[2].Impact);
    }

    [Fact]
    public void ParseFeatures_ErrorBody_ReturnsEmpty()
    {
        var hits = ParseFeatures("""{"error":{"code":400,"message":"Invalid or missing input parameters."}}""", "Overall");
        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("WINTER WEATHER AREA", 0)]
    [InlineData("minor", 1)]
    [InlineData("Moderate", 2)]
    [InlineData("MAJOR", 3)]
    [InlineData("EXTREME", 4)]
    [InlineData("bogus", -1)]
    [InlineData("", -1)]
    public void GetImpactRank_MapsAllTiers(string impact, int expected)
        => Assert.Equal(expected, GetImpactRank(impact));

    [Fact]
    public void ParseIssueTime_ParsesWpcFormatAsUtc()
        => Assert.Equal(new DateTimeOffset(2026, 11, 22, 1, 14, 0, TimeSpan.Zero), ParseIssueTime("2026-11-22 0114Z"));

    [Fact]
    public void ParseWssiTime_ParsesWpcFormatAsUtc()
        => Assert.Equal(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero), ParseWssiTime("00Z 11/25/26"));

    [Fact]
    public void BuildAlert_NoQualifyingHits_ReturnsNull()
    {
        var alert = BuildAlert(new[] { Hit("Overall", "WINTER WEATHER AREA") }, Minor, "MPX", Utc);
        Assert.Null(alert);
    }

    [Fact]
    public void BuildAlert_ListsHighestImpactPerComponent_HighestFirst()
    {
        var hits = new[]
        {
            Hit("Overall", "MINOR"), Hit("Overall", "MODERATE"),
            Hit("Snow Amount", "MAJOR"),
            Hit("Blowing Snow", "WINTER WEATHER AREA"), // below MinImpact, dropped
        };

        var alert = BuildAlert(hits, Minor, "MPX", Utc)!;

        Assert.True(alert.IsWssi);
        Assert.Equal("Severe", alert.Severity);
        Assert.Equal("Major Impacts — Winter Storm Severity Index", alert.Headline);
        Assert.StartsWith("Snow Amount: Major Impacts\nOverall: Moderate Impacts\n", alert.Instruction);
        Assert.DoesNotContain("Blowing Snow", alert.Instruction);
        Assert.Equal("https://www.wpc.ncep.noaa.gov/wwd/wssi/images/WSSI_OVERALL_MPX.png", alert.MapImageUrl);
        Assert.Equal("https://www.wpc.ncep.noaa.gov/wwd/wssi/wssi.php?id=MPX", alert.DetailsUrl);
    }

    [Theory]
    [InlineData("MINOR", "Minor")]
    [InlineData("MODERATE", "Moderate")]
    [InlineData("MAJOR", "Severe")]
    [InlineData("EXTREME", "Extreme")]
    public void BuildAlert_MapsSeverity(string impact, string severity)
        => Assert.Equal(severity, BuildAlert(new[] { Hit("Overall", impact) }, Minor, "MPX", Utc)!.Severity);

    [Fact]
    public void BuildAlert_Id_StableAcrossReissuesSameDay_ChangesWhenImpactRises()
    {
        var morning = BuildAlert(new[] { Hit("Overall", "MODERATE", issue: "2026-11-22 0114Z") }, Minor, "MPX", Utc)!;
        var later   = BuildAlert(new[] { Hit("Overall", "MODERATE", issue: "2026-11-22 1314Z") }, Minor, "MPX", Utc)!;
        var upgrade = BuildAlert(new[] { Hit("Overall", "MAJOR",    issue: "2026-11-22 1514Z") }, Minor, "MPX", Utc)!;

        Assert.Equal(morning.Id, later.Id);
        Assert.NotEqual(morning.Id, upgrade.Id);
    }

    [Fact]
    public void BuildAlert_ExpiresAtLatestEndTime()
    {
        var hits = new[] { Hit("Overall", "MINOR", end: "00Z 11/23/26"), Hit("Snow Load", "MINOR", end: "00Z 11/25/26") };
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero), BuildAlert(hits, Minor, "MPX", Utc)!.Expires);
    }

    [Fact]
    public void BuildMultipointGeometry_UsesInvariantCulture()
        => Assert.Equal("""{"points":[[-93.26,44.98],[-122.2,41.4]],"spatialReference":{"wkid":4326}}""",
            BuildMultipointGeometry(new[] { (-93.26, 44.98), (-122.2, 41.4) }));
}
