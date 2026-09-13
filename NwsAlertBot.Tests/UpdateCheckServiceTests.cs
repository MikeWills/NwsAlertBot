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
    public void ParseVersion_CalVerTagsOrderCorrectlyAndOutrankLegacySemVer()
    {
        // Tags are vYYYY.MM.PATCH (CONTRIBUTING.md "Version scheme"). The leading zero on the
        // month is dropped by System.Version but must not affect ordering, and every CalVer tag
        // must compare newer than the last SemVer release so Auto-Update moves instances forward.
        var legacy   = UpdateCheckService.ParseVersion("v0.4.0")!;
        var sep0     = UpdateCheckService.ParseVersion("v2026.09.0")!;
        var sep1     = UpdateCheckService.ParseVersion("v2026.09.1")!;
        var oct0     = UpdateCheckService.ParseVersion("v2026.10.0")!;
        var nextYear = UpdateCheckService.ParseVersion("v2027.01.0")!;

        Assert.Equal(new Version(2026, 9, 0), sep0);
        Assert.True(sep0 > legacy);
        Assert.True(sep1 > sep0);
        Assert.True(oct0 > sep1);
        Assert.True(nextYear > oct0);
        // What release.yml injects (-p:Version=2026.09.0) becomes assembly version 2026.9.0.0;
        // a 4-part running version must still compare equal-or-newer to the 3-part tag, not older.
        Assert.False(new Version(2026, 9, 0, 0) < sep0);
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

    [Fact]
    public void UnversionedDevBuild_MatchesTheAssemblyVersionALocalBuildActuallyProduces()
    {
        // NwsAlertBot.csproj's <Version>0.0.0</Version> (3 parts) compiles to assembly version
        // 0.0.0.0 (4 parts) -- CheckForUpdateAsync's dev-build guard compares against this exact
        // value, so it must match reality, not just "looks like zero".
        Assert.Equal(new Version(0, 0, 0, 0), UpdateCheckService.UnversionedDevBuild);
    }
}
