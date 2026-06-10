namespace WhipRadio.Core.Api;

/// <summary>Wire contracts between the Orchestrator API and the Web app.</summary>
public sealed record NowPlayingDto(
    string ItemType,
    Guid ItemId,
    string Title,
    DateTime StartedAtUtc,
    double DurationSeconds,
    string? ModeratorName);

public sealed record TrackDto(
    Guid Id,
    string Title,
    string Genre,
    bool HasVocals,
    double DurationSeconds,
    int PlayCount,
    int UpVotes,
    int DownVotes,
    bool IsRetired,
    string Backend,
    DateTime CreatedAt);

public sealed record PlayLogEntryDto(
    DateTime PlayedAt,
    string ItemType,
    string Title,
    string? ModeratorName);

public sealed record ModeratorDto(
    int Id,
    string Name,
    string Language,
    string VoiceId,
    double SpeechRate,
    string Style,
    string PersonaPrompt,
    bool? PrefersVocals,
    string PreferredGenres,
    bool IsActive);

public sealed record StationSettingsDto(
    string StationName,
    string DefaultLanguage,
    int TargetQueueLength,
    int AnnouncementEveryNTracks);

public sealed record VoteRequestDto(Guid TrackId, int Direction);

public sealed record VoteResultDto(Guid TrackId, int UpVotes, int DownVotes, bool IsRetired);
