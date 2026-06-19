using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>Stage 2 of the announcement pipeline: persona rewrite + speech markers.</summary>
public class VoiceDirector(ITextGenerationService llm) : IVoiceDirector
{
    public async Task<string> DirectAsync(
        string script,
        Moderator moderator,
        CancellationToken ct,
        PromptContext? context = null)
    {
        var systemPrompt = PromptTemplates.Render("VoiceDirector.System", new Dictionary<string, string>
        {
            ["PersonaPrompt"] = moderator.PersonaPrompt,
            ["Style"] = moderator.Style,
            ["Language"] = moderator.Language,
            ["Gender"] = moderator.Gender == ModeratorGenders.Male ? "male" : "female",
        });

        if (context is not null)
        {
            if (context.BaselineTraits is not null && context.CurrentTraits is not null)
            {
                systemPrompt =
                    $"{systemPrompt}\n\nKeep the baseline persona stable ({context.BaselineTraits}), " +
                    $"but shade this delivery with the current mood traits ({context.CurrentTraits}).";
            }

            systemPrompt = $"{systemPrompt}\n\n{context.RenderSituation()}";
        }

        var voiced = await llm.CompleteAsync(systemPrompt, script, "Directing voice delivery", ct);
        return LlmOutputSanitizer.Sanitize(voiced);
    }
}
