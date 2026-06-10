using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Abstractions;

/// <summary>Input for the two-stage announcement pipeline.</summary>
public sealed record AnnouncementRequest(
    AnnouncementKind Kind,
    string StationName,
    string Language,
    Track? Track = null,
    string? Facts = null);

/// <summary>Stage 1: produces the announcement content.</summary>
public interface IScriptWriter
{
    Task<string> WriteAsync(AnnouncementRequest request, CancellationToken ct);
}
