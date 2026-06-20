using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Tests;

[TestClass]
public class LlmOutputSanitizerTests
{
    [TestMethod]
    public void Sanitize_StripsCodeFences()
    {
        var result = LlmOutputSanitizer.Sanitize("```text\nUp next: a great song!\n```");
        Assert.Equal("Up next: a great song!", result);
    }

    [TestMethod]
    public void Sanitize_StripsSurroundingQuotes()
    {
        Assert.Equal("Up next on WhipRadio!", LlmOutputSanitizer.Sanitize("\"Up next on WhipRadio!\""));
        Assert.Equal("The show keeps moving!", LlmOutputSanitizer.Sanitize("\"The show keeps moving!\""));
    }

    [TestMethod]
    public void Sanitize_StripsLeadInLine()
    {
        var result = LlmOutputSanitizer.Sanitize("Sure, here is your radio intro: Up next, a banger!");
        Assert.Equal("Up next, a banger!", result);
    }

    [TestMethod]
    public void Sanitize_PlainTextPassesThrough()
    {
        var input = "Up next: three minutes of late-night lo-fi.";
        Assert.Equal(input, LlmOutputSanitizer.Sanitize(input));
    }

    [TestMethod]
    public void Sanitize_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LlmOutputSanitizer.Sanitize("  "));
    }

    [TestMethod]
    public void Sanitize_StripsParentheticalStageDirections()
    {
        var result = LlmOutputSanitizer.Sanitize("(Sound of a pulsing synth) WhipRadio, the sounds you need. (laughs softly) Up next!");
        Assert.Equal("WhipRadio, the sounds you need. Up next!", result);
    }

    [TestMethod]
    public void Sanitize_KeepsSpeechMarkersIntact()
    {
        var input = "Hello [pause:300ms] there [breath] friends.";
        Assert.Equal(input, LlmOutputSanitizer.Sanitize(input));
    }

    [TestMethod]
    public void Sanitize_StripsHereWeGoLeadIn()
    {
        Assert.Equal("Welcome back to WhipRadio!",
            LlmOutputSanitizer.Sanitize("Okay, here we go: Welcome back to WhipRadio!"));
    }

    [TestMethod]
    public void Sanitize_StripsTaskConfirmationFirstLine()
    {
        Assert.Equal("Up next, a real gem.",
            LlmOutputSanitizer.Sanitize("I created a text for a song intro\nUp next, a real gem."));
        Assert.Equal("Now the room gets quiet.",
            LlmOutputSanitizer.Sanitize("Here is your moderation text:\nNow the room gets quiet."));
    }

    [TestMethod]
    public void Sanitize_KeepsLegitimateOkayOpener()
    {
        var input = "Okay okay, settle down folks, big news tonight!";
        Assert.Equal(input, LlmOutputSanitizer.Sanitize(input));
    }

    [TestMethod]
    public void Sanitize_StripsTrailingMetaLine()
    {
        Assert.Equal("Up next a song.",
            LlmOutputSanitizer.Sanitize("Up next a song.\nLet me know if you want any changes!"));
    }

    [TestMethod]
    public void Sanitize_StripsMarkdownEmphasis()
    {
        Assert.Equal("Welcome to the show tonight!",
            LlmOutputSanitizer.Sanitize("*Welcome* to the **show** tonight!"));
    }
}
