using NwsAlertBot.Models;
using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class PlatformHelpersTests
{
    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly ten", 11, "exactly ten")]
    [InlineData("this is too long to fit", 10, "this is...")]
    public void TruncateWithEllipsis_TruncatesOnlyWhenOverLimit(string value, int maxLength, string expected)
    {
        Assert.Equal(expected, PlatformHelpers.TruncateWithEllipsis(value, maxLength));
    }

    [Fact]
    public void TruncateWithEllipsis_ResultNeverExceedsMaxLength()
    {
        var result = PlatformHelpers.TruncateWithEllipsis(new string('x', 500), 100);
        Assert.Equal(100, result.Length);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void CacheBust_ReturnsNullForNullOrEmptyUrl()
    {
        Assert.Null(PlatformHelpers.CacheBust(null, "alert-1"));
        Assert.Null(PlatformHelpers.CacheBust("", "alert-1"));
    }

    [Fact]
    public void CacheBust_AppendsQuestionMarkWhenNoExistingQueryString()
    {
        var result = PlatformHelpers.CacheBust("https://example.com/map.png", "alert-1");
        Assert.Equal("https://example.com/map.png?_cb=alert-1", result);
    }

    [Fact]
    public void CacheBust_AppendsAmpersandWhenQueryStringAlreadyPresent()
    {
        var result = PlatformHelpers.CacheBust("https://example.com/map.png?zoom=5", "alert-1");
        Assert.Equal("https://example.com/map.png?zoom=5&_cb=alert-1", result);
    }

    [Fact]
    public void CacheBust_EscapesAlertId()
    {
        var result = PlatformHelpers.CacheBust("https://example.com/map.png", "alert with spaces");
        Assert.Equal("https://example.com/map.png?_cb=alert%20with%20spaces", result);
    }

    [Theory]
    [InlineData("Extreme", 0xE53935)]
    [InlineData("extreme", 0xE53935)]
    [InlineData("Severe", 0xFB8C00)]
    [InlineData("Moderate", 0xFDD835)]
    [InlineData("Minor", 0x43A047)]
    [InlineData("Unknown", 0x757575)]
    [InlineData(null, 0x757575)]
    public void DiscordSeverityColor_MapsKnownSeveritiesCaseInsensitively(string? severity, int expected)
    {
        Assert.Equal(expected, PlatformHelpers.DiscordSeverityColor(severity));
    }

    private static NwsAlert MakeAlert(string areaDesc = "Test County", string instruction = "", string? detailsUrl = null, DateTimeOffset? ends = null) => new()
    {
        Event = "Test Warning",
        AreaDesc = areaDesc,
        Instruction = instruction,
        DetailsUrl = detailsUrl,
        Ends = ends,
    };

    [Fact]
    public void BuildSmsText_IncludesHeaderAreaAndInstructionWhenTheyFit()
    {
        var alert = MakeAlert(areaDesc: "Example County", instruction: "Take shelter now.");
        var result = PlatformHelpers.BuildSmsText(alert, 320);

        Assert.Contains("NWS ALERT: Test Warning", result);
        Assert.Contains("Example County", result);
        Assert.Contains("Take shelter now.", result);
    }

    [Fact]
    public void BuildSmsText_NeverTruncatesMidField_DropsWholeFieldInstead()
    {
        // A maxLength that can fit the header but not header+area+instruction together should
        // drop the instruction entirely rather than showing a truncated fragment of it.
        var alert = MakeAlert(areaDesc: "Example County", instruction: "This instruction text is long enough that it cannot fit in the remaining budget at all.");
        var result = PlatformHelpers.BuildSmsText(alert, 40);

        Assert.Contains("NWS ALERT: Test Warning", result);
        Assert.DoesNotContain("This instruction", result);
        Assert.True(result.Length <= 40);
    }

    [Fact]
    public void BuildSmsText_StripsDetailsUrlDuplicatedAtEndOfInstruction()
    {
        var alert = MakeAlert(instruction: "Take shelter now.\nhttps://example.com/details", detailsUrl: "https://example.com/details");
        var result = PlatformHelpers.BuildSmsText(alert, 320);

        // The link should appear exactly once (as the dedicated "Details:" line), not duplicated
        // from the end of Instruction too.
        var occurrences = result.Split("https://example.com/details").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Contains("Details: https://example.com/details", result);
    }

    [Fact]
    public void BuildSmsText_KeepsDetailsLinkOverInstructionWhenBothCannotFit()
    {
        var alert = MakeAlert(
            instruction: new string('x', 300),
            detailsUrl: "https://example.com/d");
        var result = PlatformHelpers.BuildSmsText(alert, 60);

        Assert.Contains("Details: https://example.com/d", result);
        Assert.DoesNotContain(new string('x', 10), result);
    }

    [Fact]
    public void BuildSmsText_OverallResultNeverExceedsMaxLength()
    {
        var alert = MakeAlert(
            areaDesc: new string('a', 100),
            instruction: new string('b', 500),
            detailsUrl: "https://example.com/" + new string('c', 100));
        var result = PlatformHelpers.BuildSmsText(alert, 160);

        Assert.True(result.Length <= 160);
    }
}
