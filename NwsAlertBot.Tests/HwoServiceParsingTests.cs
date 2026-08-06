using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class HwoServiceParsingTests
{
    private const string NoHazardsProduct = """
        Hazardous Weather Outlook
        National Weather Service Twin Cities/Chanhassen MN
        319 PM CDT Wed Aug 5 2026

        This Hazardous Weather Outlook is for portions of central and southern
        Minnesota, and west central Wisconsin.

        .DAY ONE...Tonight.

        No hazardous weather is expected at this time.

        .DAYS TWO THROUGH SEVEN...Thursday through Tuesday.

        No hazardous weather is expected at this time.

        .SPOTTER INFORMATION STATEMENT...

        SKYWARN spotter activation will not be needed.

        $$
        """;

    private const string HazardOnDayOneProduct = """
        Hazardous Weather Outlook
        National Weather Service Twin Cities/Chanhassen MN
        319 PM CDT Wed Aug 5 2026

        .DAY ONE...Tonight.

        Severe thunderstorms are possible this evening with damaging winds
        and large hail.

        .DAYS TWO THROUGH SEVEN...Thursday through Tuesday.

        No hazardous weather is expected at this time.

        $$
        """;

    private const string HazardInExtendedProduct = """
        Hazardous Weather Outlook
        National Weather Service Twin Cities/Chanhassen MN
        319 PM CDT Wed Aug 5 2026

        .DAY ONE...Tonight.

        No hazardous weather is expected at this time.

        .DAYS TWO THROUGH SEVEN...Thursday through Tuesday.

        Excessive heat is possible Friday into Saturday.

        $$
        """;

    private const string MissingSectionProduct = """
        Hazardous Weather Outlook
        National Weather Service Twin Cities/Chanhassen MN
        319 PM CDT Wed Aug 5 2026

        No hazardous weather is expected at this time.

        $$
        """;

    [Fact]
    public void ReportsNoHazards_TrueWhenBothSectionsAreClear()
    {
        Assert.True(HwoService.ReportsNoHazards(NoHazardsProduct));
    }

    [Fact]
    public void ReportsNoHazards_FalseWhenDayOneHasAHazard()
    {
        Assert.False(HwoService.ReportsNoHazards(HazardOnDayOneProduct));
    }

    [Fact]
    public void ReportsNoHazards_FalseWhenExtendedPeriodHasAHazard()
    {
        Assert.False(HwoService.ReportsNoHazards(HazardInExtendedProduct));
    }

    [Fact]
    public void ReportsNoHazards_FalseWhenSectionsCannotBeLocated()
    {
        Assert.False(HwoService.ReportsNoHazards(MissingSectionProduct));
    }
}
