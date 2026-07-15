using NwsAlertBot.Models;
using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class NwsAlertServiceBuildDetailsUrlTests
{
    [Fact]
    public void BuildDetailsUrl_PrefersAfosIdOverVtecFields()
    {
        var alert = new NwsAlert
        {
            AfosId = "NPWDLH",
            VtecWfo = "DLH",
            VtecPhenomena = "HT",
            VtecSignificance = "Y",
            VtecEtn = 5,
            Sent = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        };

        var result = NwsAlertService.BuildDetailsUrl(alert);

        Assert.Equal(
            "https://forecast.weather.gov/product.php?site=NWS&issuedby=DLH&product=NPW&format=CI&version=1&glossary=0",
            result);
    }

    [Fact]
    public void BuildDetailsUrl_FallsBackToIemVtecEventPageWhenAfosIdMissing()
    {
        var alert = new NwsAlert
        {
            AfosId = null,
            VtecWfo = "DLH",
            VtecPhenomena = "HT",
            VtecSignificance = "Y",
            VtecEtn = 5,
            Sent = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        };

        var result = NwsAlertService.BuildDetailsUrl(alert);

        Assert.Equal(
            "https://mesonet.agron.iastate.edu/vtec/?wfo=KDLH&phenomena=HT&significance=Y&eventid=0005&year=2026",
            result);
    }

    [Theory]
    [InlineData("NPW")]        // too short
    [InlineData("NPWDLHX")]    // too long
    [InlineData("npwdlh")]     // lowercase
    [InlineData("NPW-DLH")]    // non-letter character
    public void BuildDetailsUrl_FallsBackToVtecWhenAfosIdMalformed(string afosId)
    {
        var alert = new NwsAlert
        {
            AfosId = afosId,
            VtecWfo = "DLH",
            VtecPhenomena = "HT",
            VtecSignificance = "Y",
            VtecEtn = 5,
            Sent = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        };

        var result = NwsAlertService.BuildDetailsUrl(alert);

        Assert.StartsWith("https://mesonet.agron.iastate.edu/vtec/", result);
    }

    [Fact]
    public void BuildDetailsUrl_ReturnsNullWhenNeitherAfosIdNorVtecFieldsAvailable()
    {
        var alert = new NwsAlert();

        var result = NwsAlertService.BuildDetailsUrl(alert);

        Assert.Null(result);
    }
}
