using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Abstractions;

/// <summary>Input for the two-stage announcement pipeline.</summary>
public sealed record AnnouncementRequest(
    AnnouncementKind Kind,
    string StationName,
    string Language,
    Track? Track = null,
    string? Facts = null,
    string? LengthHint = null,
    PromptContext? PromptContext = null);

/// <summary>Stage 1: produces the announcement content.</summary>
public interface IScriptWriter
{
    Task<string> WriteAsync(AnnouncementRequest request, CancellationToken ct);
}
