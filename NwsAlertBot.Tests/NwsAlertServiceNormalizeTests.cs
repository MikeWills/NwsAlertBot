using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class NwsAlertServiceNormalizeTests
{
    [Fact]
    public void NormalizeNwsText_CollapsesSingleLineWrapsToSpaces()
    {
        var result = NwsAlertService.NormalizeNwsText("Line one\nline two\nline three");
        Assert.Equal("Line one line two line three", result);
    }

    [Fact]
    public void NormalizeNwsText_PreservesIntentionalParagraphBreaks()
    {
        var result = NwsAlertService.NormalizeNwsText("Paragraph one.\n\nParagraph two.");
        Assert.Equal("Paragraph one.\n\nParagraph two.", result);
    }

    [Fact]
    public void NormalizeNwsText_CollapsesExcessiveBlankLinesToOneParagraphBreak()
    {
        var result = NwsAlertService.NormalizeNwsText("Paragraph one.\n\n\n\n\nParagraph two.");
        Assert.Equal("Paragraph one.\n\nParagraph two.", result);
    }

    [Fact]
    public void NormalizeNwsText_NormalizesWindowsAndOldMacLineEndings()
    {
        var result = NwsAlertService.NormalizeNwsText("Line one\r\nline two\rline three");
        Assert.Equal("Line one line two line three", result);
    }

    [Fact]
    public void NormalizeNwsText_TrimsLeadingAndTrailingWhitespace()
    {
        var result = NwsAlertService.NormalizeNwsText("  \n Padded text \n  ");
        Assert.Equal("Padded text", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeNwsText_HandlesNullOrEmptyGracefully(string? input)
    {
        var result = NwsAlertService.NormalizeNwsText(input!);
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeNwsText_MixedWrappedLinesAndParagraphBreaks()
    {
        // Simulates a real NWS product: line-wrapped sentences within a paragraph, a blank
        // line between paragraphs.
        var input = "A tornado warning remains in effect\nfor the following counties.\n\nTake shelter now.";
        var result = NwsAlertService.NormalizeNwsText(input);

        Assert.Equal("A tornado warning remains in effect for the following counties.\n\nTake shelter now.", result);
    }
}
