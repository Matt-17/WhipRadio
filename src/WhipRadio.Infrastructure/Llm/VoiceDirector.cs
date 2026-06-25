using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
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

        var voiced = await llm.CompleteAsync(
            new TextGenerationRequest(systemPrompt, script, "Directing voice delivery", StructuredJson.SchemaFor<ScriptDto>(), "script"),
            ct);
        return await SanitizeOrRetryAsync(voiced, systemPrompt, script, ct);
    }

    private async Task<string> SanitizeOrRetryAsync(
        string raw,
        string systemPrompt,
        string script,
        CancellationToken ct)
    {
        if (LlmOutputSanitizer.TrySanitizeSpokenText(raw, out var sanitized, out var error))
        {
            return sanitized;
        }

        var retryPrompt =
            $"{script}\n\nPrevious reply rejected: {error} " +
            """Return ONLY the JSON object {"script":"…"} with the adapted spoken text (allowed speech markers included) in the script field.""";
        var retry = await llm.CompleteAsync(
            new TextGenerationRequest(systemPrompt, retryPrompt, "Directing voice delivery retry", StructuredJson.SchemaFor<ScriptDto>(), "script"),
            ct);
        if (LlmOutputSanitizer.TrySanitizeSpokenText(retry, out sanitized, out error))
        {
            return sanitized;
        }

        throw new InvalidOperationException($"The voice director returned invalid spoken text twice: {error}");
    }
}
