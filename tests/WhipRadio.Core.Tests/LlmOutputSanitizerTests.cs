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
    public void TrySanitizeSpokenText_KeepsLeadingSpeechMarkersIntact()
    {
        var ok = LlmOutputSanitizer.TrySanitizeSpokenText(
            "[rate:slow] Coming up next [pause:300ms] something quiet and strange.",
            out var result,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("[rate:slow] Coming up next [pause:300ms] something quiet and strange.", result);
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

    [TestMethod]
    public void TrySanitizeSpokenText_ExtractsAnnounceToolJson()
    {
        var ok = LlmOutputSanitizer.TrySanitizeSpokenText(
            """{"tool":"Announce","arguments":{"text":"Good evening."}}""",
            out var result,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("Good evening.", result);
    }

    [TestMethod]
    public void TrySanitizeSpokenText_ExtractsFencedAnnounceToolJson()
    {
        var ok = LlmOutputSanitizer.TrySanitizeSpokenText(
            """
            ```json
            {"tool":"Announce","arguments":{"text":"Weather next."}}
            ```
            """,
            out var result,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("Weather next.", result);
    }

    [TestMethod]
    public void TrySanitizeSpokenText_ExtractsAnnounceToolJsonArray()
    {
        var ok = LlmOutputSanitizer.TrySanitizeSpokenText(
            """[{"tool":"Announce","arguments":{"text":"News after the break."}}]""",
            out var result,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("News after the break.", result);
    }

    [TestMethod]
    public void TrySanitizeSpokenText_RejectsMissingAnnounceText()
    {
        var ok = LlmOutputSanitizer.TrySanitizeSpokenText(
            """{"tool":"Announce","arguments":{}}""",
            out var result,
            out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, result);
        Assert.Contains("arguments.text", error);
    }
}
