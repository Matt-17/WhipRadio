using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Tests;

public class LlmOutputSanitizerTests
{
    [Fact]
    public void Sanitize_StripsCodeFences()
    {
        var result = LlmOutputSanitizer.Sanitize("```text\nUp next: a great song!\n```");
        Assert.Equal("Up next: a great song!", result);
    }

    [Fact]
    public void Sanitize_StripsSurroundingQuotes()
    {
        Assert.Equal("Up next on WhipRadio!", LlmOutputSanitizer.Sanitize("\"Up next on WhipRadio!\""));
        Assert.Equal("Gleich geht's weiter!", LlmOutputSanitizer.Sanitize("„Gleich geht's weiter!“"));
    }

    [Fact]
    public void Sanitize_StripsLeadInLine()
    {
        var result = LlmOutputSanitizer.Sanitize("Sure, here is your radio intro: Up next, a banger!");
        Assert.Equal("Up next, a banger!", result);
    }

    [Fact]
    public void Sanitize_PlainTextPassesThrough()
    {
        var input = "Und jetzt: drei Minuten Lofi zum Runterkommen.";
        Assert.Equal(input, LlmOutputSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LlmOutputSanitizer.Sanitize("  "));
    }
}
