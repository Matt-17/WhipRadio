using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Abstractions;

/// <summary>Stage 2: rewrites a script in the moderator's persona and injects speech markers.</summary>
public interface IVoiceDirector
{
    Task<string> DirectAsync(string script, Moderator moderator, CancellationToken ct, PromptContext? context = null);
}
