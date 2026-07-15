using NwsAlertBot.Models;
using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class PushoverServiceBuildBodyTests
{
    private static NwsAlert MakeAlert() => new()
    {
        Event = "Tornado Warning",
        AreaDesc = "Example County",
        Instruction = "Take shelter immediately.",
        Sent = new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void BuildBody_AppendsDetailsLinkWhenSet()
    {
        var alert = MakeAlert();
        alert.DetailsUrl = "https://forecast.weather.gov/product.php?site=NWS&issuedby=DLH&product=NPW";

        var result = PushoverService.BuildBody(alert);

        Assert.Contains($"Details: {alert.DetailsUrl}", result);
    }

    [Fact]
    public void BuildBody_DoesNotDuplicateDetailsLinkAlreadyBakedIntoInstruction()
    {
        var alert = MakeAlert();
        alert.DetailsUrl = "https://www.spc.noaa.gov/products/outlook/day1otlk.html";
        alert.Instruction = $"Tornado: 5%\nFor more details: {alert.DetailsUrl}";

        var result = PushoverService.BuildBody(alert);

        var occurrences = result.Split(alert.DetailsUrl).Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void BuildBody_OmitsDetailsLineWhenNotSet()
    {
        var alert = MakeAlert();
        alert.DetailsUrl = null;

        var result = PushoverService.BuildBody(alert);

        Assert.DoesNotContain("Details:", result);
    }
}
