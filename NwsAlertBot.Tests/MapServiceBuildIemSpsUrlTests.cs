using NwsAlertBot.Models;
using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class MapServiceBuildIemSpsUrlTests
{
    private static NwsAlert MakeAlert(string? afosId, string? wmoIdentifier, DateTimeOffset sent) => new()
    {
        Event = "Special Weather Statement",
        AfosId = afosId,
        WmoIdentifier = wmoIdentifier,
        Sent = sent,
    };

    [Fact]
    public void BuildIemSpsUrl_UsesTimestampParsedFromWmoHeaderWhenPresent()
    {
        var alert = MakeAlert("SPSMPX", "WWUS83 KMPX 011045", new DateTimeOffset(2026, 6, 1, 10, 47, 0, TimeSpan.Zero));

        var url = MapService.BuildIemSpsUrl(alert);

        Assert.NotNull(url);
        Assert.Contains("pid:202606011045-KMPX-WWUS83-SPSMPX::", url);
        Assert.StartsWith("https://mesonet.agron.iastate.edu/plotting/auto/plot/217/", url);
        Assert.EndsWith("::segnum:0::n:auto::_r:t::dpi:100.png", url);
    }

    [Fact]
    public void BuildIemSpsUrl_FallsBackToAlertSentWhenWmoHeaderHasNoTimestampToken()
    {
        var alert = MakeAlert("SPSMPX", "WWUS83 KMPX", new DateTimeOffset(2026, 6, 1, 10, 47, 0, TimeSpan.Zero));

        var url = MapService.BuildIemSpsUrl(alert);

        Assert.NotNull(url);
        Assert.Contains("pid:202606011047-KMPX-WWUS83-SPSMPX::", url);
    }

    [Fact]
    public void BuildIemSpsUrl_ExtractsWfoAsLast3CharsOfAfosId()
    {
        var alert = MakeAlert("SPSOUN", "WWUS84 KOUN 011045", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var url = MapService.BuildIemSpsUrl(alert);

        Assert.NotNull(url);
        Assert.Contains("-KOUN-", url);
    }

    [Fact]
    public void BuildIemSpsUrl_ReturnsNullForNullAfosId()
    {
        var alert = MakeAlert(null, "WWUS83 KMPX 011045", DateTimeOffset.UtcNow);
        Assert.Null(MapService.BuildIemSpsUrl(alert));
    }

    [Theory]
    [InlineData("SPSMP")]   // too short
    [InlineData("SPSMPXX")] // too long
    public void BuildIemSpsUrl_ReturnsNullForWrongLengthAfosId(string afosId)
    {
        var alert = MakeAlert(afosId, "WWUS83 KMPX 011045", DateTimeOffset.UtcNow);
        Assert.Null(MapService.BuildIemSpsUrl(alert));
    }

    [Fact]
    public void BuildIemSpsUrl_ReturnsNullForNullWmoIdentifier()
    {
        var alert = MakeAlert("SPSMPX", null, DateTimeOffset.UtcNow);
        Assert.Null(MapService.BuildIemSpsUrl(alert));
    }

    [Fact]
    public void BuildIemSpsUrl_ReturnsNullForTooShortWmoIdentifier()
    {
        var alert = MakeAlert("SPSMPX", "WWUS8", DateTimeOffset.UtcNow);
        Assert.Null(MapService.BuildIemSpsUrl(alert));
    }
}
