using Microsoft.Extensions.Logging.Abstractions;
using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class WpcPwpfServiceTests
{
    // Trimmed from a real prb_24hsnow_ge01_f024_cntr_latest.kmz: document header + one level
    // label Placemark (no Polygon) + two nested contour rings. The 10% ring is a square around
    // (-93, 45); the 40% ring is a smaller square inside it. Coordinates are lon,lat,alt.
    private const string SampleKml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2">
        <Document id="prb_24hsnow_ge01_f024_cntr">
        <name>WPC 24-Hr Prob of Snow > 1"</name>
          <open>1</open>
          <snippet>Valid 00Z 09/12/2026 - 00Z 09/13/2026</snippet>
          <description></description>
          <Folder>
            <name>Snow Probabilities</name>
            <open>0</open>
        <Placemark>
          <name>10</name>
          <visibility>1</visibility>
          <styleUrl>#pct_10</styleUrl>
        </Placemark>
        <Placemark>
          <name>10</name>
          <styleUrl>#poly_10</styleUrl>
          <Polygon>
            <outerBoundaryIs>
              <LinearRing>
                <coordinates>
        -94.00,44.00,0 -92.00,44.00,0 -92.00,46.00,0 -94.00,46.00,0 -94.00,44.00,0
                </coordinates>
              </LinearRing>
            </outerBoundaryIs>
          </Polygon>
        </Placemark>
        <Placemark>
          <name>40</name>
          <styleUrl>#poly_40</styleUrl>
          <Polygon>
            <outerBoundaryIs>
              <LinearRing>
                <coordinates>
        -93.50,44.50,0 -92.50,44.50,0 -92.50,45.50,0 -93.50,45.50,0 -93.50,44.50,0
                </coordinates>
              </LinearRing>
            </outerBoundaryIs>
          </Polygon>
        </Placemark>
            </Folder>
        </Document>
        </kml>
        """;

    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void ParseKml_ReadsValidWindowAndPolygonPlacemarksOnly()
    {
        var contour = WpcPwpfService.ParseKml(SampleKml);

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero), contour.ValidStart);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero), contour.ValidEnd);
        // The label-only Placemark (no <Polygon>) is skipped.
        Assert.Equal(2, contour.Polygons.Count);
        Assert.Equal(new[] { 10, 40 }, contour.Polygons.Select(p => p.Level).ToArray());
    }

    [Fact]
    public void FindBand_ReturnsHighestEnclosingContour()
    {
        var contour = WpcPwpfService.ParseKml(SampleKml);

        Assert.Equal(40, WpcPwpfService.FindBand(contour.Polygons, -93.0, 45.0)); // inside both rings
        Assert.Equal(10, WpcPwpfService.FindBand(contour.Polygons, -93.9, 44.1)); // inside 10% only
        Assert.Equal(0,  WpcPwpfService.FindBand(contour.Polygons, -90.0, 45.0)); // outside everything
    }

    [Theory]
    [InlineData("Valid 00Z 09/12/2026 - 00Z 09/13/2026", "2026-09-12T00:00:00Z", "2026-09-13T00:00:00Z")]
    [InlineData("Valid 12Z 01/31/2027 - 12Z 02/01/2027", "2027-01-31T12:00:00Z", "2027-02-01T12:00:00Z")]
    public void ParseValidWindow_ParsesUtcStartAndEnd(string snippet, string expectedStart, string expectedEnd)
    {
        var (start, end) = WpcPwpfService.ParseValidWindow(snippet);
        Assert.Equal(DateTimeOffset.Parse(expectedStart), start);
        Assert.Equal(DateTimeOffset.Parse(expectedEnd), end);
    }

    [Fact]
    public void ParseValidWindow_ReturnsNullsOnGarbage()
    {
        var (start, end) = WpcPwpfService.ParseValidWindow("no dates here");
        Assert.Null(start);
        Assert.Null(end);
    }

    [Theory]
    [InlineData(PrecipType.Snow, 1,    1, "prb_24hsnow_ge01_f024_cntr_latest.kmz")]
    [InlineData(PrecipType.Snow, 12,   2, "prb_24hsnow_ge12_f048_cntr_latest.kmz")]
    [InlineData(PrecipType.Ice,  0.25, 1, "prb_24hicez_ge.25_f024_cntr_latest.kmz")]
    [InlineData(PrecipType.Ice,  0.10, 2, "prb_24hicez_ge.10_f048_cntr_latest.kmz")]
    [InlineData(PrecipType.Ice,  0.01, 1, "prb_24hicez_ge.01_f024_cntr_latest.kmz")]
    public void BuildFileName_MatchesWpcNamingConvention(PrecipType ptype, double threshold, int day, string expected)
    {
        Assert.Equal(expected, WpcPwpfService.BuildFileName(ptype, threshold, day));
    }

    [Theory]
    [InlineData(0,  "<1%")]
    [InlineData(1,  "1–5%")]
    [InlineData(40, "40–50%")]
    [InlineData(90, "90–95%")]
    [InlineData(95, "≥95%")]
    public void FormatBand_LabelsContourRange(int band, string expected)
    {
        Assert.Equal(expected, WpcPwpfService.FormatBand(band));
    }

    [Fact]
    public void ParseThresholds_KeepsOnlyPublishedValuesSortedAndDeduped()
    {
        var result = WpcPwpfService.ParseThresholds("12, 4,4, 3, abc, 1", WpcPwpfService.AvailableSnowThresholds, "SnowThresholds", NullLogger.Instance);
        Assert.Equal(new[] { 1.0, 4.0, 12.0 }, result);

        var ice = WpcPwpfService.ParseThresholds(".25,0.10", WpcPwpfService.AvailableIceThresholds, "IceThresholds", NullLogger.Instance);
        Assert.Equal(new[] { 0.10, 0.25 }, ice);

        Assert.Empty(WpcPwpfService.ParseThresholds("", WpcPwpfService.AvailableSnowThresholds, "SnowThresholds", NullLogger.Instance));
    }

    [Fact]
    public void BuildAlert_PicksHighestQualifyingThreshold()
    {
        var bands = new List<(double Threshold, int Band)> { (1, 90), (4, 60), (8, 20), (12, 0) };
        var start = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);

        var alert = WpcPwpfService.BuildAlert(1, PrecipType.Snow, bands, start, end, start.AddHours(-3), 40, Central);

        Assert.NotNull(alert);
        Assert.True(alert!.IsPwpf);
        Assert.Equal(4, alert.PwpfThreshold);
        Assert.Equal(60, alert.PwpfBand);
        Assert.Equal("Moderate", alert.Severity);
        Assert.Equal("WPC-PWPF-Day1-Snow-20260115-ge4-p60", alert.Id);
        Assert.Equal("WPC Day 1 Snowfall Outlook", alert.Event);
        Assert.StartsWith("60–70% chance of ≥4\" snow", alert.Headline);
        Assert.Contains("≥1\": ≥90%", alert.Instruction.Replace("90–95%", "≥90%")); // band list present
        Assert.Contains("≥12\": <1%", alert.Instruction);
        Assert.Contains("ptype=snow&amt=4&day=1", alert.DetailsUrl!);
        Assert.Equal("https://www.wpc.ncep.noaa.gov/wwd/day1_psnow_gt_04_conus.gif", alert.MapImageUrl);
        Assert.Equal(end, alert.Expires);
    }

    [Fact]
    public void BuildAlert_ReturnsNullWhenNothingMeetsMinProbability()
    {
        var bands = new List<(double Threshold, int Band)> { (1, 30), (4, 10) };
        var start = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(WpcPwpfService.BuildAlert(1, PrecipType.Snow, bands, start, start.AddDays(1), null, 40, Central));
        // ...but the same bands qualify at a lower MinProbabilityPercent.
        Assert.NotNull(WpcPwpfService.BuildAlert(1, PrecipType.Snow, bands, start, start.AddDays(1), null, 30, Central));
    }

    [Fact]
    public void BuildAlert_ReturnsNullWithoutValidStart()
    {
        var bands = new List<(double Threshold, int Band)> { (1, 90) };
        Assert.Null(WpcPwpfService.BuildAlert(1, PrecipType.Snow, bands, null, null, null, 40, Central));
    }

    [Fact]
    public void BuildAlert_IceUsesIceNamingAndSeverity()
    {
        var bands = new List<(double Threshold, int Band)> { (0.10, 70), (0.25, 40) };
        var start = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

        var alert = WpcPwpfService.BuildAlert(2, PrecipType.Ice, bands, start, start.AddDays(1), null, 40, Central);

        Assert.NotNull(alert);
        Assert.Equal("WPC-PWPF-Day2-Ice-20260201-ge0.25-p40", alert!.Id);
        Assert.Equal("WPC Day 2 Freezing Rain Outlook", alert.Event);
        Assert.Equal("Severe", alert.Severity);
        Assert.Equal("https://www.wpc.ncep.noaa.gov/wwd/day2_pice_gt_25_conus.gif", alert.MapImageUrl);
        Assert.Contains("ptype=icez&amt=0.25&day=2", alert.DetailsUrl!);
    }

    [Theory]
    [InlineData(PrecipType.Snow, 1,    "Minor")]
    [InlineData(PrecipType.Snow, 4,    "Moderate")]
    [InlineData(PrecipType.Snow, 8,    "Severe")]
    [InlineData(PrecipType.Snow, 12,   "Extreme")]
    [InlineData(PrecipType.Snow, 18,   "Extreme")]
    [InlineData(PrecipType.Ice,  0.01, "Minor")]
    [InlineData(PrecipType.Ice,  0.10, "Moderate")]
    [InlineData(PrecipType.Ice,  0.25, "Severe")]
    [InlineData(PrecipType.Ice,  0.50, "Extreme")]
    public void MapSeverity_KeyedToThreshold(PrecipType ptype, double threshold, string expected)
    {
        Assert.Equal(expected, WpcPwpfService.MapSeverity(ptype, threshold));
    }

    [Theory]
    [InlineData(PrecipType.Snow, 1,  "day1_composite_conus.gif")]
    [InlineData(PrecipType.Snow, 8,  "day1_psnow_gt_08_conus.gif")]
    [InlineData(PrecipType.Ice,  0.10, "day1_composite_conus.gif")]
    public void BuildImageUrl_FallsBackToCompositeForThresholdsWithoutADedicatedImage(PrecipType ptype, double threshold, string expectedFile)
    {
        Assert.EndsWith("/wwd/" + expectedFile, WpcPwpfService.BuildImageUrl(1, ptype, threshold));
    }
}
