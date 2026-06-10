using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Abstractions;

/// <summary>Stage 2: rewrites a script in the moderator's persona and injects speech markers.</summary>
public interface IVoiceDirector
{
    Task<string> DirectAsync(string script, Moderator moderator, CancellationToken ct);
}
