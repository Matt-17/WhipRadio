using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>LLM helper for the music producer: invents titles and writes lyrics.</summary>
public class MusicCopywriter(ITextGenerationService llm)
{
    private const string SystemPrompt =
        "You are a creative assistant for a radio station's music department. " +
        "Answer exactly as instructed, with no extra commentary.";

    public async Task<string> InventTitleAsync(string genre, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("MusicTitle", new Dictionary<string, string> { ["Genre"] = genre });
        var title = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        var firstLine = title.Split('\n')[0].Trim();
        return string.IsNullOrWhiteSpace(firstLine) ? $"Untitled {genre} tune" : firstLine;
    }

    public async Task<string> WriteLyricsAsync(string genre, string language, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("LyricsWriter", new Dictionary<string, string>
        {
            ["Genre"] = genre,
            ["Language"] = language,
        });
        return LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
    }
}
