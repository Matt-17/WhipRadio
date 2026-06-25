using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Abstractions;

/// <summary>Input for the single-run announcement pipeline.</summary>
public sealed record AnnouncementRequest(
    AnnouncementKind Kind,
    string StationName,
    string Language,
    Track? Track = null,
    string? Facts = null,
    string? LengthHint = null,
    PromptContext? PromptContext = null);

/// <summary>
/// Result of the combined run: the clean transcript, the speech-marked delivery, and the
/// per-delivery voice direction the host model chose.
/// </summary>
public sealed record SpokenAnnouncement(
    string Script,           // clean transcript shown to the user
    string Delivery,         // words + speech markers, before normalization
    string? DeliveryPrompt,  // per-delivery TTS hint (null = none)
    double? Rate);           // per-delivery rate (null = use moderator default)

/// <summary>
/// Writes a spoken announcement in one LLM run: content, delivery, and voice direction
/// together. Replaces the old two-stage <c>IScriptWriter</c> + <c>IVoiceDirector</c>.
/// </summary>
public interface IAnnouncementWriter
{
    Task<SpokenAnnouncement> WriteAsync(AnnouncementRequest request, Moderator moderator, CancellationToken ct);
}
