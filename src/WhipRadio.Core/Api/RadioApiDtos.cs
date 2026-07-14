namespace WhipRadio.Core.Api;

public static class RadioDisplayNames
{
    public static string AnnouncementTitle(string? announcementKind)
        => announcementKind switch
        {
            "News" => "News",
            "Conversation" => "Podcast",
            _ => "Announcement",
        };
}

/// <summary>Wire contracts between the Orchestrator API/hub and the Web app.</summary>
public sealed record NowPlayingDto(
    string ItemType,
    Guid ItemId,
    string Title,
    DateTime StartedAtUtc,
    double DurationSeconds,
    string? ModeratorName,
    string? ArtistName = null,
    string? Transcript = null,
    int UpVotes = 0,
    int DownVotes = 0,
    string? FormatName = null,
    string? Lyrics = null,
    string? AnnouncementKind = null);

public sealed record QueueItemDto(
    string ItemType,
    Guid ItemId,
    string Title,
    double DurationSeconds);

/// <summary>
/// Encoder/stream health surfaced to the console lamp: "Online", "Reconnecting"
/// (encoder crashed, backing off before restart, <see cref="NextAttemptUtc"/> set),
/// or "Offline" (circuit breaker tripped — station parked until On Air re-enabled).
/// <see cref="PlayoutEnabled"/> is the orthogonal operator On Air intent so the
/// header lamp can show "off air" without a separate settings fetch; defaults to
/// true so an older snapshot reads as on air.
/// </summary>
public sealed record StationStatusDto(string Status, string? Reason, DateTime? NextAttemptUtc, bool PlayoutEnabled = true);

public sealed record TrackDto(
    Guid Id,
    string Title,
    string Genre,
    string Subgenre,
    string ArtistName,
    Guid? ArtistId,
    bool HasVocals,
    double DurationSeconds,
    int PlayCount,
    int UpVotes,
    int DownVotes,
    bool IsRetired,
    string Backend,
    DateTime CreatedAt,
    string Language = "en",
    string? SongStory = null,
    string? Lyrics = null,
    int? TargetDurationSeconds = null,
    string? Style = null,
    bool DeletionPending = false);

public sealed record ArchiveTrackDto(
    Guid Id,
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    string Genre,
    double DurationSeconds,
    string Source,
    string MetadataStatus,
    double? MetadataConfidence,
    int PlayCount,
    int UpVotes,
    int DownVotes,
    bool IsRetired,
    DateTime CreatedAt,
    int CandidateCount = 0,
    bool DeletionPending = false);

public sealed record MetadataCandidateDto(
    Guid Id,
    string Title,
    string Artist,
    string? Album,
    int? Year,
    double Score,
    IReadOnlyList<string> Reasons,
    string Status);

public sealed record ArchiveStatusDto(
    int ExternalTracks,
    int UploadedTracks,
    int RetiredTracks,
    int LocalOnly,
    int Matched,
    int NeedsReview,
    int Verified,
    DateTime? LastScanUtc,
    int ConfiguredFolders,
    bool ScanRunning,
    bool UploadEnabled,
    long MaxUploadBytes);

public sealed record ArtistDto(
    Guid Id,
    string Name,
    string Slug,
    string Genre,
    string Subgenre,
    string StyleDescriptor,
    int TrackCount,
    int UpVotes,
    int DownVotes,
    bool IsRetired,
    string? Biography = null,
    string? Type = null,
    string? Origin = null,
    int? FormationYear = null,
    string? PromotionText = null,
    IReadOnlyList<ArtistMemberDto>? Members = null,
    string Language = "en",
    string? DeepBackground = null);

public sealed record ArtistMemberDto(
    Guid Id,
    string Name,
    string Role,
    string Biography,
    bool HasVoiceReference = false,
    string? VoiceError = null,
    string Gender = "",
    int? Age = null,
    string Interests = "",
    string Personality = "");

public sealed record GuestDto(
    Guid Id,
    string Name,
    string Slug,
    string Expertise,
    string Gender,
    int? Age,
    string Interests,
    string Personality,
    string Biography,
    bool HasVoice,
    bool HasVoiceReference,
    string? VoiceError,
    bool IsArchived,
    DateTime CreatedAtUtc,
    string? VoiceFx = null);

public sealed record ArtistPostDto(
    Guid Id,
    Guid ArtistId,
    string ArtistName,
    string ArtistSlug,
    Guid? TrackId,
    string? TrackTitle,
    string Kind,
    string Body,
    DateTime CreatedAtUtc);

public sealed record ChatChannelDto(
    Guid Id,
    string Kind,
    string Name,
    int? ModeratorId,
    string? PhotoUrl,
    DateTime LastMessageAtUtc,
    string? LastMessagePreview,
    int UnreadCount,
    bool IsArchived,
    IReadOnlyList<ChatChannelMemberDto>? Members = null);

public sealed record ChatChannelMemberDto(
    Guid Id,
    string Kind,
    string DisplayName,
    int? ModeratorId,
    Guid? EntityId,
    string? PhotoUrl);

public sealed record ChatParticipantOptionDto(
    string Kind,
    int? ModeratorId,
    Guid? EntityId,
    string Name,
    string Subtitle);

public sealed record ChatParticipantSelectionDto(
    string Kind,
    int? ModeratorId,
    Guid? EntityId);

public sealed record CreateGroupChannelRequestDto(
    string? Name,
    IReadOnlyList<ChatParticipantSelectionDto> Members);

public sealed record ChatActionDto(
    string Tool,
    IReadOnlyDictionary<string, string> Arguments,
    string State,
    string? ResultSummary);

public sealed record ChatMessageDto(
    Guid Id,
    Guid ChannelId,
    string SenderKind,
    int? SenderModeratorId,
    string SenderName,
    string? SenderPhotoUrl,
    string Text,
    IReadOnlyList<ChatActionDto> Actions,
    DateTime CreatedAtUtc,
    Guid? CorrelationId,
    int HopCount);

public sealed record PagedChatMessagesDto(
    IReadOnlyList<ChatMessageDto> Messages,
    bool HasMore);

public sealed record PostChatMessageRequest(string Text);

public sealed record ChatAgentThinkingDto(
    Guid ChannelId,
    string SenderName,
    bool IsThinking);

public sealed record AgentLogEntryDto(
    Guid Id,
    DateTime CreatedAtUtc,
    string AgentName,
    int? ModeratorId,
    string Source,
    Guid? CorrelationId,
    int Round,
    string Kind,
    string? Tool,
    string Content,
    string? Outcome);

public sealed record PagedArtistPostsDto(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<ArtistPostDto> Items);

public sealed record CreateArtistRequestDto(string? Hint);

public sealed record CreateGuestRequestDto(string? Hint);

public sealed record RedefineGuestRequestDto(string? Hint);

public sealed record UpdateGuestVoiceFxRequestDto(string? VoiceFx);

public sealed record RedefineArtistRequestDto(string? Hint);

public sealed record MusicProductionStatusDto(
    Guid? ArtistId,
    string? ArtistName,
    string? TrackTitle,
    DateTime? StartedAtUtc,
    IReadOnlyList<Guid> QueuedArtistIds);

public sealed record PlayLogEntryDto(
    DateTime PlayedAt,
    string ItemType,
    Guid ItemId,
    string Title,
    IReadOnlyList<PlayLogHostDto>? Hosts,
    double DurationSeconds,
    string? Transcript,
    IReadOnlyList<TalkPartDto>? TalkParts = null,
    string? ArtistName = null,
    string? ArtistSlug = null,
    bool IsNews = false,
    bool IsDeleted = false,
    bool WasFallback = false);

public sealed record PlayLogHostDto(string Name, string Slug);

public sealed record TalkPartDto(
    int SortOrder,
    string Kind,
    string Purpose,
    string Priority,
    int? DesiredDurationSeconds,
    int? WordBudget);

public sealed record EmergencyTalkBreakRequestDto(
    string Text,
    int? ModeratorId = null,
    string Priority = "Emergency",
    int? ExpiresInMinutes = 60);

public sealed record EmergencyTalkBreakDto(
    Guid AnnouncementId,
    Guid TalkBreakId,
    string Priority,
    string Status);

public sealed record ModeratorDto(
    int Id,
    string Name,
    string Slug,
    string Language,
    string Gender,
    string TtsEngine,
    string VoiceId,
    double SpeechRate,
    string Style,
    string PersonaPrompt,
    bool? PrefersVocals,
    string PreferredGenres,
    bool IsActive,
    bool IsAutoGenerated,
    double Talkativeness = 0.5,
    bool IsWeatherSpecialist = false,
    bool IsNewsSpecialist = false,
    string? PhotoUrl = null,
    ModeratorTraitsDto? BaselineTraits = null,
    ModeratorTraitsDto? CurrentTraits = null,
    HostTalkProfileDto? TalkProfile = null);

public sealed record CreateModeratorDto(
    string Name,
    string Language,
    string Gender,
    string TtsEngine,
    string Style,
    string PersonaPrompt,
    bool? PrefersVocals,
    string PreferredGenres,
    double Talkativeness = 0.5,
    bool IsWeatherSpecialist = false,
    string? PhotoUrl = null,
    ModeratorTraitsDto? BaselineTraits = null,
    HostTalkProfileDto? TalkProfile = null,
    bool IsNewsSpecialist = false,
    string? VoiceDescription = null);

public sealed record CreateSpecialistHostRequestDto(
    string Role,
    string? Hint = null);

public sealed record ModeratorTraitsDto(
    string Energy,
    string Formality,
    string HumorLevel,
    string Talkativeness,
    string Warmth);

public sealed record HostTalkProfileDto(
    int BreakFrequencyTracks = 1,
    int MinPartsPerBreak = 1,
    int MaxPartsPerBreak = 3,
    string AllowedTalkPartKinds = "SongIntro,SongOutro,Banter,PersonalNote,Joke,TalkBit,Jingle,ListenerGreeting,RequestDedication,StationId,Weather,News,HostChange",
    int ExactReplayTolerance = 2,
    double EvergreenBitTolerance = 0.5);

public sealed record ModeratorPhotoDto(string? PhotoUrl);

public sealed record ModeratorUsageDto(
    bool IsNewsPresenter,
    bool IsWeatherSpecialist,
    int AssignedFormatCount,
    int ActiveTalkBitCount,
    int PendingTalkBreakCount,
    int AssignedListenerMessageCount,
    int HistoricalAnnouncementCount,
    int HistoricalPlayCount);

public sealed record FireModeratorResultDto(
    ModeratorDto Moderator,
    ModeratorUsageDto Usage);

public sealed record StationSettingsDto(
    string StationName,
    string StationSlogan,
    string StationVision,
    string StationMission,
    string DefaultLanguage,
    int TargetQueueLength,
    int AnnouncementEveryNTracks,
    bool MusicProductionEnabled,
    bool PlayoutEnabled,
    int MaxLibrarySize,
    int MinTrackDurationSeconds,
    int MaxTrackDurationSeconds,
    bool EnableBreathMarkers,
    double FrequencyMhz,
    int FirstDayOfWeek,
    string DefaultMusicProvider,
    string TextProvider,
    string OpenAiApiKey,
    string OpenAiModel,
    bool ElevenLabsEnabled,
    string ElevenLabsApiKey,
    bool GreetingsEnabled = true,
    bool WeatherEnabled = true,
    int WeatherCadenceMinutes = 60,
    int? WeatherSpecialistModeratorId = null,
    bool WeatherFullHandoverEnabled = false,
    string WeatherLocationName = "New York, US",
    double WeatherLatitude = 40.7128,
    double WeatherLongitude = -74.0060,
    bool ArchiveUploadEnabled = true,
    bool ArchivePlayoutEnabled = true,
    bool ArchiveEnrichmentEnabled = true,
    bool PodcastKnowledgeEnabled = true);

public sealed record NewsFeedDto(
    Guid Id,
    string Label,
    string Url,
    string Language,
    string Region,
    string Category,
    bool IsEnabled,
    bool IsSeeded,
    int PollCadenceMinutes,
    int MaxItemsPerPoll,
    DateTime CreatedAtUtc,
    DateTime? LastPolledAtUtc,
    string? LastError,
    int ItemCount);

public sealed record SaveNewsFeedDto(
    string Label,
    string Url,
    string Language = "en",
    string Region = "global",
    string Category = "general",
    bool IsEnabled = true,
    int PollCadenceMinutes = 30,
    int MaxItemsPerPoll = 20);

public sealed record NewsPackageDto(
    Guid Id,
    string Kind,
    string Status,
    DateTime TargetUtc,
    int TargetDurationSeconds,
    Guid? AnnouncementId,
    DateTime CreatedAtUtc,
    DateTime? ProducedAtUtc,
    DateTime? QueuedAtUtc,
    DateTime? PlayedAtUtc,
    string? FailureReason,
    string? ProductionState,
    string? SourceSummary,
    int StepIndex,
    int StepTotal,
    string? Transcript = null);

public sealed record NewsProductionDto(
    bool NewsEnabled,
    bool NewsExtractionEnabled,
    int NewsPackageCadenceMinutes,
    int NewsPackageMaxDurationSeconds,
    int? NewsPresenterModeratorId,
    double TopOfHourFadeOutSeconds,
    int TopOfHourIntroGraceSeconds,
    DateTime NextPackageTargetUtc,
    string? NextPackageStatus,
    IReadOnlyList<string> NewsCategoryOrder,
    string? WarningText,
    IReadOnlyList<NewsFeedDto> Feeds,
    IReadOnlyList<NewsPackageDto> RecentPackages,
    bool NewsLongFormatEnabled = false,
    string NewsLongFormatAirTimes = "",
    int NewsLongFormatDurationMinutes = 30);

public sealed record SaveNewsProductionSettingsDto(
    bool NewsEnabled,
    bool NewsExtractionEnabled,
    int NewsPackageCadenceMinutes,
    int NewsPackageMaxDurationSeconds,
    int? NewsPresenterModeratorId,
    double TopOfHourFadeOutSeconds,
    int TopOfHourIntroGraceSeconds,
    IReadOnlyList<string> NewsCategoryOrder,
    bool NewsLongFormatEnabled = false,
    string NewsLongFormatAirTimes = "",
    int NewsLongFormatDurationMinutes = 30);

public sealed record WeatherProductionDto(
    bool WeatherEnabled,
    int WeatherCadenceMinutes,
    int? WeatherSpecialistModeratorId,
    bool WeatherFullHandoverEnabled,
    string WeatherLocationName,
    double WeatherLatitude,
    double WeatherLongitude,
    string? WarningText);

public sealed record SaveWeatherProductionSettingsDto(
    bool WeatherEnabled,
    int WeatherCadenceMinutes,
    int? WeatherSpecialistModeratorId,
    bool WeatherFullHandoverEnabled,
    string WeatherLocationName,
    double WeatherLatitude,
    double WeatherLongitude);

public sealed record BrandingDto(
    string StationName,
    string StationSlogan,
    string StationVision,
    string StationMission,
    IReadOnlyList<JingleDto> Jingles);

public sealed record SaveBrandingDto(
    string StationName,
    string StationSlogan,
    string StationVision,
    string StationMission);

public sealed record JingleDto(
    Guid Id,
    string Label,
    string Prompt,
    string Style,
    string Language,
    double DurationSeconds,
    string Backend,
    string Status,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? LastUsedAtUtc,
    int PlayCount,
    string Kind = "StationId");

public sealed record CreateJingleDto(
    string Label,
    string Style,
    int DurationSeconds = 10,
    string Kind = "StationId");

public sealed record MusicProviderStatusDto(
    string Id,
    string DisplayName,
    bool IsAvailable,
    string? Model);

public sealed record StudioDto(
    Guid Id,
    string Name,
    string Kind,
    string Url,
    string Provider,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    int JobsCompleted,
    int JobsFailed,
    string? CurrentJob = null,
    DateTime? JobStartedAtUtc = null,
    string? CurrentJobProgress = null,
    string RuntimeStatus = "unknown",
    string? RuntimeDetail = null);

public sealed record StudioPendingOperationDto(
    Guid Id,
    string Kind,
    string Label,
    DateTime StartedAtUtc,
    string Status,
    string? Detail = null,
    string? Progress = null,
    string? ResourceGroup = null,
    Guid? StudioId = null);

public sealed record StudioOverviewDto(
    IReadOnlyList<StudioDto> Studios,
    IReadOnlyList<StudioPendingOperationDto> PendingOperations);

/// <summary>Source "local" needs Url; source "api" needs Provider + ApiKey.</summary>
public sealed record SaveStudioDto(
    string? Name, string Kind, string Source, string? Url, string? Provider, string? ApiKey);

public sealed record TestStudioDto(
    string Kind, string Source, string? Url, string? Provider, string? ApiKey);

public sealed record StudioTestResultDto(bool Ok, string? Provider, string? Detail);

public sealed record StudioRestartResultDto(bool Ok, string Detail);

public sealed record StudioHistoryEntryDto(
    Guid Id,
    Guid? StudioId,
    string StudioName,
    string StudioKind,
    string Provider,
    string Operation,
    string Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    double? DurationSeconds,
    string PromptPreview,
    string? ResultPreview,
    string Prompt,
    string? Result,
    string? Detail,
    string? Error);

public sealed record PagedStudioHistoryDto(
    int Total,
    List<StudioHistoryEntryDto> Entries);

public sealed record MixerSettingsDto(
    bool MixerEnabled,
    double TargetLufs,
    double MaxMakeupGainDb,
    double DuckLevelDb,
    int DuckRampMs,
    double DefaultCrossfadeSeconds,
    double BeatAlignBpmTolerancePct,
    int HardCutGapAfterTalkMsMin,
    int HardCutGapAfterTalkMsMax,
    int HardCutGapSongMsMin,
    int HardCutGapSongMsMax,
    int PostHitSafetyMs,
    string StrategyWeightsJson,
    bool AnalysisRequired);

public sealed record TransitionLogEntryDto(
    DateTime OccurredAt,
    string Strategy,
    string OutgoingTitle,
    string IncomingTitle,
    double OverlapSeconds,
    int GapMs,
    int ClipCount,
    string? ReasonTrace);

public sealed record MixerStatusDto(
    int AnalyzedTracks,
    int TotalTracks,
    int AnalyzedAnnouncements,
    IReadOnlyDictionary<string, int> TransitionsByStrategy,
    int TotalClips,
    IReadOnlyList<TransitionLogEntryDto> RecentTransitions);

public sealed record MixerLiveDto(
    bool SessionActive,
    DateTime? EngagedAtUtc,
    double MasterSeconds,
    IReadOnlyList<string> ActiveItems,
    string? LastDecision,
    DateTime? LastDecisionAtUtc,
    int TransitionsThisSession);

public sealed record MixerOverviewDto(MixerSettingsDto Settings, MixerStatusDto Status, MixerLiveDto Live);

public sealed record DesignVoiceDto(string Description, string Gender, string Language, string? Name = null);

public sealed record DesignedVoiceDto(string Handle, string Description, double DurationSeconds);

public sealed record ApplyVoiceDto(string Handle, string? Description);

/// <summary>
/// Net change to a track's tally from one listener toggling feedback. The client
/// remembers its own current vote, so taking a vote back sends a -1 delta (and a
/// switch sends -1 on one side, +1 on the other). Each delta is -1, 0, or +1.
/// </summary>
public sealed record VoteRequestDto(Guid TrackId, int UpDelta, int DownDelta);

public sealed record VoteResultDto(Guid TrackId, int UpVotes, int DownVotes, bool IsRetired);

public sealed record FormatDto(
    Guid Id,
    string Name,
    string Description,
    string Genre,
    string Subgenre,
    string? ModeratorName,
    int? ModeratorId,
    string Reason,
    bool IsEnabled,
    int UpVotes,
    int DownVotes,
    string? NextOnAir,
    double Talkativeness = 0.5,
    string TalkDepth = "Light",
    double TalkDensity = 0.5);

public sealed record ProgramSlotDto(
    int Id,
    int DayOfWeek,
    int StartMinute,
    int DurationMinutes,
    Guid? FormatId,
    string? FormatName,
    string? ModeratorName,
    string? Genre,
    bool IsNewsShow = false,
    bool IsPodcastShow = false);

// --- Conversations / podcasts (Phase 3c.2) ---------------------------------

public sealed record ConversationSpeakerOptionDto(
    string SpeakerKey,
    string DisplayName,
    string Subtitle,
    bool VoiceReady);

public sealed record ConversationParticipantDto(
    string SpeakerKey,
    string DisplayName,
    string ConversationRole);

public sealed record ConversationChapterDto(
    string Title,
    string Intent,
    int TargetMinutes);

public sealed record ConversationSegmentDto(
    Guid Id,
    string Kind,
    string Structure,
    string Topic,
    string? Title,
    string Status,
    int TargetDurationMinutes,
    double DurationSeconds,
    string? ProductionState,
    int StepIndex,
    int StepTotal,
    string? FailureReason,
    Guid? AnnouncementId,
    Guid? PodcastShowId,
    string? ShowName,
    DateTime? TargetUtc,
    DateTime CreatedAtUtc,
    DateTime? ProducedAtUtc,
    DateTime? UsedAtUtc,
    IReadOnlyList<ConversationParticipantDto> Participants,
    string? Transcript = null,
    string? DegradationReason = null);

public sealed record CreateConversationRequestDto(
    string Kind,
    string Structure,
    string Topic,
    string Brief,
    int TargetDurationMinutes,
    IReadOnlyList<ConversationParticipantDto> Participants,
    IReadOnlyList<ConversationChapterDto>? Chapters = null);

public sealed record PodcastShowDto(
    Guid Id,
    string Name,
    string Brief,
    int EpisodeMinutes,
    int DayOfWeek,
    int StartMinute,
    int SlotDurationMinutes,
    bool IsEnabled,
    IReadOnlyList<ConversationParticipantDto> Participants,
    DateTime CreatedAtUtc);

public sealed record SavePodcastShowDto(
    string Name,
    string Brief,
    int EpisodeMinutes,
    int DayOfWeek,
    int StartMinute,
    int SlotDurationMinutes,
    IReadOnlyList<ConversationParticipantDto> Participants,
    bool IsEnabled = true);

public sealed record StatsDto(
    int CurrentListeners,
    int ListenerPeak,
    int TotalTracks,
    int TotalArtists,
    int TotalAnnouncements,
    int TotalPlays,
    int PlaysLastHour,
    int TotalVotes,
    double TotalMusicHours,
    IReadOnlyList<NameCountDto> TopArtists,
    IReadOnlyList<NameCountDto> HostAirtimeMinutes,
    IReadOnlyList<NameCountDto> TracksPerGenre);

public sealed record NameCountDto(string Name, double Value);

public sealed record ConsoleLineDto(
    DateTime TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? SourceKind = null,
    string? SourceName = null);

public sealed record PrivacyRequestDto(
    DateTime TimestampUtc,
    string Method,
    string Target,
    string Host,
    string Source,
    int? StatusCode,
    bool Succeeded,
    double DurationMs,
    string Classification,
    string? Error);

public sealed record PrivacyServiceDto(
    string Name,
    string Target,
    string Classification,
    string Status,
    string Detail);

public sealed record PrivacyReportDto(
    DateTime GeneratedAtUtc,
    int RequestCapacity,
    IReadOnlyList<PrivacyServiceDto> Services,
    IReadOnlyList<PrivacyRequestDto> Requests,
    IReadOnlyList<string> Notes);

public sealed record GpuStatsDto(
    string Name,
    double UtilizationPercent,
    double? MemoryUsedMb,
    double MemoryTotalMb,
    double TemperatureC);

public sealed record StorageAreaDto(string Name, double SizeMb, int FileCount);

public sealed record ServerStatsDto(
    string OsDescription,
    int ProcessorCount,
    double CpuUsagePercent,
    double MemoryTotalMb,
    double MemoryUsedMb,
    double ProcessMemoryMb,
    double ProcessUptimeSeconds,
    GpuStatsDto? Gpu,
    string DataRootPath,
    double DiskTotalGb,
    double DiskFreeGb,
    IReadOnlyList<StorageAreaDto> StorageAreas);

public sealed record MediaCleanupResultDto(
    int AnnouncementFilesDeleted,
    int TrackFilesDeleted,
    double BytesDeleted,
    IReadOnlyList<string> FailedFiles);

public sealed record MediaCleanupPlanDto(
    int AnnouncementFiles,
    int TrackFiles,
    double BytesToDelete);

public sealed record MediaCleanupStatusDto(
    string Status,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    MediaCleanupPlanDto? Plan,
    MediaCleanupResultDto? Result,
    string? Error);

public sealed record SubmitGreetingDto(
    string SenderName,
    string MessageText,
    string Kind,
    string? RequestGenre = null,
    string? RequestMood = null);

public sealed record ListenerMessageDto(
    Guid Id,
    string SenderName,
    string MessageText,
    string Kind,
    string? RequestGenre,
    string? RequestMood,
    DateTime SubmittedAt,
    string Status,
    string? DismissalReason = null,
    DateTime? AiredAt = null);

public sealed record PagedListenerMessagesDto(int TotalCount, List<ListenerMessageDto> Items);
