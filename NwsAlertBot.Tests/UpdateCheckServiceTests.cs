using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("v2.0", "2.0")]
    public void ParseVersion_ParsesTagsWithOrWithoutVPrefix(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateCheckService.ParseVersion(tag));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("v")]
    public void ParseVersion_ReturnsNullForMalformedTags(string tag)
    {
        Assert.Null(UpdateCheckService.ParseVersion(tag));
    }

    [Fact]
    public void ParseVersion_ComparesNumericallyNotLexically()
    {
        // Guards against a naive string comparison, where "1.10.0" < "1.9.0" lexically but must
        // compare as greater numerically.
        var older = UpdateCheckService.ParseVersion("v1.9.0")!;
        var newer = UpdateCheckService.ParseVersion("v1.10.0")!;
        Assert.True(newer > older);
    }

    [Fact]
    public void IsCheckDue_TrueWhenNeverCheckedBefore()
    {
        Assert.True(UpdateCheckService.IsCheckDue(DateTimeOffset.MinValue, DateTimeOffset.UtcNow, intervalHours: 24));
    }

    [Fact]
    public void IsCheckDue_FalseWithinInterval()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var lastChecked = now.AddHours(-23);
        Assert.False(UpdateCheckService.IsCheckDue(lastChecked, now, intervalHours: 24));
    }

    [Fact]
    public void IsCheckDue_TrueOncePastInterval()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var lastChecked = now.AddHours(-25);
        Assert.True(UpdateCheckService.IsCheckDue(lastChecked, now, intervalHours: 24));
    }

    [Fact]
    public void IsCheckDue_TrueExactlyAtInterval()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var lastChecked = now.AddHours(-24);
        Assert.True(UpdateCheckService.IsCheckDue(lastChecked, now, intervalHours: 24));
    }

    [Fact]
    public void GetCurrentVersion_ReturnsANonNullVersion()
    {
        // Whatever the build sets (0.0.0 locally, the real tag in a release build), the
        // executing assembly should always report *some* version.
        Assert.NotNull(UpdateCheckService.GetCurrentVersion());
    }
}
