using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>Stage 2 of the announcement pipeline: persona rewrite + speech markers.</summary>
public class VoiceDirector(ITextGenerationService llm) : IVoiceDirector
{
    public async Task<string> DirectAsync(string script, Moderator moderator, CancellationToken ct)
    {
        var systemPrompt = PromptTemplates.Render("VoiceDirector.System", new Dictionary<string, string>
        {
            ["PersonaPrompt"] = moderator.PersonaPrompt,
            ["Style"] = moderator.Style,
        });

        var voiced = await llm.CompleteAsync(systemPrompt, script, ct);
        return LlmOutputSanitizer.Sanitize(voiced);
    }
}
