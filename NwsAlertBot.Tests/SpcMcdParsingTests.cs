using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class SpcMcdParsingTests
{
    [Fact]
    public void ParseMcdNumber_ExtractsNumberCaseInsensitively()
    {
        Assert.Equal(1234, SpcMcdService.ParseMcdNumber("Mesoscale Discussion 1234"));
        Assert.Equal(7, SpcMcdService.ParseMcdNumber("mesoscale discussion 7"));
    }

    [Fact]
    public void ParseMcdNumber_ReturnsNullWhenNotPresent()
    {
        Assert.Null(SpcMcdService.ParseMcdNumber("No discussion number here."));
    }

    [Fact]
    public void ParseValidWindow_ParsesIssueAndExpireWithinSameDay()
    {
        var now = new DateTimeOffset(2026, 6, 1, 18, 30, 0, TimeSpan.Zero);
        var (issue, expire) = SpcMcdService.ParseValidWindow("Valid 011800Z - 012000Z", now);

        Assert.Equal(new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero), issue);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero), expire);
    }

    [Fact]
    public void ParseValidWindow_HandlesMidnightCrossoverBySettingExpireAfterIssue()
    {
        // Issue at 23:50 on day 01, expire at 00:10 "day 01" (product text uses the same day
        // label near the boundary) -- naive parsing would put expire chronologically before
        // issue; ParseValidWindow must correct this so expire always ends up after issue.
        var now = new DateTimeOffset(2026, 6, 1, 23, 0, 0, TimeSpan.Zero);
        var (issue, expire) = SpcMcdService.ParseValidWindow("Valid 012350Z - 010010Z", now);

        Assert.NotNull(issue);
        Assert.NotNull(expire);
        Assert.True(expire > issue, $"Expected expire ({expire}) to be after issue ({issue}).");
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 23, 50, 0, TimeSpan.Zero), issue);
        Assert.Equal(new DateTimeOffset(2026, 6, 2, 0, 10, 0, TimeSpan.Zero), expire);
    }

    [Fact]
    public void ParseValidWindow_ReturnsNullTupleWhenNoMatch()
    {
        var (issue, expire) = SpcMcdService.ParseValidWindow("No valid window here.", DateTimeOffset.UtcNow);
        Assert.Null(issue);
        Assert.Null(expire);
    }

    [Fact]
    public void ParseLatLon_ParsesUnwrappedLongitudeToken()
    {
        // 42769694 -> lat 42.76N, lon 9694 (>= 5000, no wrap) -> 96.94W -> -96.94.
        // A real MCD polygon always has >= 3 points, so pad with two more valid tokens --
        // ParseLatLon requires at least 3 to return a polygon at all (see the dedicated test below).
        var result = SpcMcdService.ParseLatLon("LAT...LON 42769694 43009500 42509300");

        Assert.NotNull(result);
        Assert.Equal((42.76, -96.94), result[0]);
    }

    [Fact]
    public void ParseLatLon_UnwrapsLongitudeAtOrPast100DegreesWest()
    {
        // SPC drops the leading "1" for lon >= 100.00W: 0173 (< 5000) means the real value is
        // 10173 -> 101.73W -> -101.73. This is the specific wrap-around case flagged as fragile.
        var result = SpcMcdService.ParseLatLon("LAT...LON 42760173 43009500 42509300");

        Assert.NotNull(result);
        Assert.Equal((42.76, -101.73), result[0]);
    }

    [Fact]
    public void ParseLatLon_ParsesMultipleTokensInOrder()
    {
        // 43009500 -> lat 43.00, lon 9500 (>= 5000, no wrap) -> 95.00W -> -95.00
        // 42509300 -> lat 42.50, lon 9300 (>= 5000, no wrap) -> 93.00W -> -93.00
        var result = SpcMcdService.ParseLatLon("LAT...LON 42769694 43009500 42509300");

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal((42.76, -96.94), result[0]);
        Assert.Equal((43.00, -95.00), result[1]);
        Assert.Equal((42.50, -93.00), result[2]);
    }

    [Fact]
    public void ParseLatLon_SkipsMalformedTokensWithoutThrowing()
    {
        // "123" is not 8 chars -- should be silently skipped, not throw, leaving the 3 valid tokens.
        var result = SpcMcdService.ParseLatLon("LAT...LON 123 42769694 43009500 42509300");

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal((42.76, -96.94), result[0]);
    }

    [Fact]
    public void ParseLatLon_ReturnsNullWhenFewerThanThreeValidPoints()
    {
        // A polygon needs at least 3 points; 1 or 2 valid tokens isn't a shape SPC could have
        // meant, so ParseLatLon treats it the same as "no polygon" rather than returning a
        // degenerate 1- or 2-point list.
        Assert.Null(SpcMcdService.ParseLatLon("LAT...LON 42769694"));
        Assert.Null(SpcMcdService.ParseLatLon("LAT...LON 42769694 43009500"));
    }

    [Fact]
    public void ParseLatLon_ReturnsNullWhenBlockNotPresent()
    {
        Assert.Null(SpcMcdService.ParseLatLon("No polygon block here."));
    }
}
