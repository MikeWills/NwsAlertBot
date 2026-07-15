using NwsAlertBot.Models;

namespace NwsAlertBot.Tests;

public class NwsAlertFormatPostTests
{
    private static NwsAlert MakeAlert() => new()
    {
        Event = "Tornado Warning",
        Headline = "Tornado Warning issued for Example County",
        AreaDesc = "Example County",
        Instruction = "Take shelter immediately.",
        Sent = new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero),
        Expires = new DateTimeOffset(2026, 6, 1, 19, 0, 0, TimeSpan.Zero),
        MessageType = "Alert",
        DisplayTimeZone = TimeZoneInfo.Utc, // avoid depending on the test runner's local time zone
    };

    [Fact]
    public void FormatPost_IncludesHeadlineAndInstructionWhenTheyFit()
    {
        var alert = MakeAlert();
        var result = alert.FormatPost(maxLength: 500);

        Assert.Contains("Tornado Warning issued for Example County", result);
        Assert.Contains("Take shelter immediately.", result);
    }

    [Fact]
    public void FormatPost_NeverExceedsMaxLength()
    {
        var alert = MakeAlert();
        alert.Instruction = new string('x', 1000);

        var result = alert.FormatPost(maxLength: 280);
        Assert.True(result.Length <= 280);
    }

    [Fact]
    public void FormatPost_TruncatesWithEllipsisWhenOverLimit()
    {
        // A huge Instruction alone does NOT force truncation -- FormatPost omits it entirely if it
        // doesn't fit (see OmitsInstructionEntirelyRatherThanTruncatingItMidway below). To force the
        // final truncate-with-ellipsis path, maxLength must be smaller than the *unavoidable* content
        // (header + dates), which a 1-word Headline like this alone already exceeds at maxLength 10.
        var alert = MakeAlert();
        var result = alert.FormatPost(maxLength: 10);

        Assert.True(result.Length <= 10);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void FormatPost_OmitsInstructionEntirelyRatherThanTruncatingItMidway()
    {
        var alert = MakeAlert();
        alert.Instruction = "UNIQUE_INSTRUCTION_MARKER_" + new string('y', 300);

        var result = alert.FormatPost(maxLength: 100);

        // The instruction shouldn't appear at all (not even a fragment) once it doesn't fit
        // whole -- FormatPost only appends it if withInstruction.Length <= maxLength.
        Assert.DoesNotContain("UNIQUE_INSTRUCTION_MARKER", result);
    }

    [Fact]
    public void FormatPost_HwoAlertsUseHwoTextInsteadOfInstruction()
    {
        var alert = MakeAlert();
        alert.IsHwo = true;
        alert.HwoText = "Full HWO product text goes here.";
        alert.Instruction = ""; // HWO alerts don't populate Instruction

        var result = alert.FormatPost(maxLength: 2000);

        Assert.Contains("Full HWO product text goes here.", result);
        Assert.Contains("📋", result); // HWO-specific prefix, not the default warning emoji
    }

    [Fact]
    public void FormatPost_SpcOutlookAppendsAttributionWhenItFits()
    {
        var alert = MakeAlert();
        alert.IsSpcOutlook = true;

        var result = alert.FormatPost(maxLength: 2000);

        Assert.Contains("Iowa State University", result);
    }

    [Fact]
    public void FormatPost_SpcOutlookOmitsAttributionWhenItWouldNotFit()
    {
        // maxLength here is too small for the header alone plus the ~86-char attribution line,
        // regardless of whether Instruction ends up included or omitted.
        var alert = MakeAlert();
        alert.IsSpcOutlook = true;

        var result = alert.FormatPost(maxLength: 10);

        Assert.DoesNotContain("Iowa State University", result);
    }

    [Theory]
    [InlineData("cancel", "CANCELLED")]
    [InlineData("update", "UPDATE")]
    public void FormatPost_UsesMessageTypeSpecificPrefix(string messageType, string expectedMarker)
    {
        var alert = MakeAlert();
        alert.MessageType = messageType;

        var result = alert.FormatPost(maxLength: 500);

        Assert.Contains(expectedMarker, result);
    }

    [Fact]
    public void FormatPost_AppendsDetailsLinkWhenSet()
    {
        var alert = MakeAlert();
        alert.DetailsUrl = "https://forecast.weather.gov/product.php?site=NWS&issuedby=DLH&product=NPW";

        var result = alert.FormatPost(maxLength: 500);

        Assert.Contains($"Details: {alert.DetailsUrl}", result);
    }

    [Fact]
    public void FormatPost_OmitsDetailsLinkWhenItWouldNotFit()
    {
        var alert = MakeAlert();
        alert.DetailsUrl = "https://forecast.weather.gov/product.php?site=NWS&issuedby=DLH&product=NPW";

        var result = alert.FormatPost(maxLength: 30);

        Assert.DoesNotContain(alert.DetailsUrl, result);
    }

    [Fact]
    public void FormatPost_DoesNotDuplicateDetailsLinkAlreadyBakedIntoInstruction()
    {
        // SPC Outlook/MCD/ERO append their details URL directly into Instruction at construction
        // time (so short-form platforms without a separate Details line still show it). FormatPost
        // must not add a second "Details:" line for the same URL.
        var alert = MakeAlert();
        alert.DetailsUrl = "https://www.spc.noaa.gov/products/outlook/day1otlk.html";
        alert.Instruction = $"Tornado: 5%\nFor more details: {alert.DetailsUrl}";

        var result = alert.FormatPost(maxLength: 2000);

        var occurrences = result.Split(alert.DetailsUrl).Length - 1;
        Assert.Equal(1, occurrences);
    }
}
