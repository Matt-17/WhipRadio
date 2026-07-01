using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Artists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Genre = table.Column<string>(type: "text", nullable: false),
                    Subgenre = table.Column<string>(type: "text", nullable: false),
                    StyleDescriptor = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Origin = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "text", nullable: false),
                    FormationYear = table.Column<int>(type: "integer", nullable: true),
                    CreationHint = table.Column<string>(type: "text", nullable: true),
                    Biography = table.Column<string>(type: "text", nullable: true),
                    DeepBackgroundBiography = table.Column<string>(type: "text", nullable: true),
                    PromotionText = table.Column<string>(type: "text", nullable: true),
                    GenerationPrompt = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IsRetired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jingles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    Style = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    Backend = table.Column<string>(type: "text", nullable: false),
                    ModelUsed = table.Column<string>(type: "text", nullable: true),
                    SeedUsed = table.Column<string>(type: "text", nullable: true),
                    TaskId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PlayCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jingles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListenerMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderName = table.Column<string>(type: "text", nullable: false),
                    MessageText = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    RequestGenre = table.Column<string>(type: "text", nullable: true),
                    RequestMood = table.Column<string>(type: "text", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: true),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: true),
                    DismissalReason = table.Column<string>(type: "text", nullable: true),
                    FulfilledByTrackId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListenerMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemType = table.Column<string>(type: "text", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bpm = table.Column<double>(type: "double precision", nullable: true),
                    BpmConfidence = table.Column<double>(type: "double precision", nullable: false),
                    BeatGridJson = table.Column<string>(type: "text", nullable: true),
                    IntroEndSeconds = table.Column<double>(type: "double precision", nullable: true),
                    IntroConfidence = table.Column<double>(type: "double precision", nullable: false),
                    OutroStartSeconds = table.Column<double>(type: "double precision", nullable: true),
                    OutroConfidence = table.Column<double>(type: "double precision", nullable: false),
                    LeadingSilenceSeconds = table.Column<double>(type: "double precision", nullable: false),
                    TrailingSilenceSeconds = table.Column<double>(type: "double precision", nullable: false),
                    IntegratedLufs = table.Column<double>(type: "double precision", nullable: false),
                    TruePeakDb = table.Column<double>(type: "double precision", nullable: false),
                    EnergyProfileJson = table.Column<string>(type: "text", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    AnalyzerVersion = table.Column<int>(type: "integer", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAnalyses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModeratorMemories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModeratorId = table.Column<int>(type: "integer", nullable: false),
                    Layer = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModeratorMemories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Moderators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    Gender = table.Column<string>(type: "text", nullable: false),
                    TtsEngine = table.Column<string>(type: "text", nullable: false),
                    VoiceId = table.Column<string>(type: "text", nullable: false),
                    VoiceDescription = table.Column<string>(type: "text", nullable: true),
                    SpeechRate = table.Column<double>(type: "double precision", nullable: false),
                    PersonaPrompt = table.Column<string>(type: "text", nullable: false),
                    Style = table.Column<string>(type: "text", nullable: false),
                    Talkativeness = table.Column<double>(type: "double precision", nullable: false),
                    BaselineEnergy = table.Column<string>(type: "text", nullable: false),
                    BaselineFormality = table.Column<string>(type: "text", nullable: false),
                    BaselineHumorLevel = table.Column<string>(type: "text", nullable: false),
                    BaselineTalkativeness = table.Column<string>(type: "text", nullable: false),
                    BaselineWarmth = table.Column<string>(type: "text", nullable: false),
                    TalkBreakFrequencyTracks = table.Column<int>(type: "integer", nullable: false),
                    MinTalkPartsPerBreak = table.Column<int>(type: "integer", nullable: false),
                    MaxTalkPartsPerBreak = table.Column<int>(type: "integer", nullable: false),
                    AllowedTalkPartKinds = table.Column<string>(type: "text", nullable: false),
                    ExactReplayTolerance = table.Column<int>(type: "integer", nullable: false),
                    EvergreenBitTolerance = table.Column<double>(type: "double precision", nullable: false),
                    PrefersVocals = table.Column<bool>(type: "boolean", nullable: true),
                    PreferredGenres = table.Column<string>(type: "text", nullable: false),
                    IsNewsSpecialist = table.Column<bool>(type: "boolean", nullable: false),
                    IsWeatherSpecialist = table.Column<bool>(type: "boolean", nullable: false),
                    PhotoUrl = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsAutoGenerated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Moderators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsFeeds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsSeeded = table.Column<bool>(type: "boolean", nullable: false),
                    PollCadenceMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxItemsPerPoll = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastPolledAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeeds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ItemType = table.Column<string>(type: "text", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    WasFallback = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StationName = table.Column<string>(type: "text", nullable: false),
                    StationSlogan = table.Column<string>(type: "text", nullable: false),
                    StationVision = table.Column<string>(type: "text", nullable: false),
                    StationMission = table.Column<string>(type: "text", nullable: false),
                    DefaultLanguage = table.Column<string>(type: "text", nullable: false),
                    DefaultMusicProvider = table.Column<string>(type: "text", nullable: false),
                    TargetQueueLength = table.Column<int>(type: "integer", nullable: false),
                    AnnouncementEveryNTracks = table.Column<int>(type: "integer", nullable: false),
                    MusicProductionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PlayoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxLibrarySize = table.Column<int>(type: "integer", nullable: false),
                    MinTrackDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxTrackDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    EnableBreathMarkers = table.Column<bool>(type: "boolean", nullable: false),
                    FrequencyMhz = table.Column<double>(type: "double precision", nullable: false),
                    FirstDayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    TextProvider = table.Column<string>(type: "text", nullable: false),
                    OpenAiApiKey = table.Column<string>(type: "text", nullable: false),
                    OpenAiModel = table.Column<string>(type: "text", nullable: false),
                    ElevenLabsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ElevenLabsApiKey = table.Column<string>(type: "text", nullable: false),
                    GreetingsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxPendingGreetings = table.Column<int>(type: "integer", nullable: false),
                    WeatherEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    WeatherCadenceMinutes = table.Column<int>(type: "integer", nullable: false),
                    WeatherSpecialistModeratorId = table.Column<int>(type: "integer", nullable: true),
                    WeatherLocationName = table.Column<string>(type: "text", nullable: false),
                    WeatherLatitude = table.Column<double>(type: "double precision", nullable: false),
                    WeatherLongitude = table.Column<double>(type: "double precision", nullable: false),
                    WeatherFullHandoverEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    NewsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    NewsExtractionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    NewsPackageCadenceMinutes = table.Column<int>(type: "integer", nullable: false),
                    NewsPackageMaxDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    NewsPresenterModeratorId = table.Column<int>(type: "integer", nullable: true),
                    NewsSeedFeedsCreated = table.Column<bool>(type: "boolean", nullable: false),
                    NewsCategoryOrder = table.Column<string>(type: "text", nullable: false),
                    TopOfHourFadeOutSeconds = table.Column<double>(type: "double precision", nullable: false),
                    TopOfHourIntroGraceSeconds = table.Column<int>(type: "integer", nullable: false),
                    MixerEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TargetLufs = table.Column<double>(type: "double precision", nullable: false),
                    MaxMakeupGainDb = table.Column<double>(type: "double precision", nullable: false),
                    DuckLevelDb = table.Column<double>(type: "double precision", nullable: false),
                    DuckRampMs = table.Column<int>(type: "integer", nullable: false),
                    DefaultCrossfadeSeconds = table.Column<double>(type: "double precision", nullable: false),
                    BeatAlignBpmTolerancePct = table.Column<double>(type: "double precision", nullable: false),
                    HardCutGapAfterTalkMsMin = table.Column<int>(type: "integer", nullable: false),
                    HardCutGapAfterTalkMsMax = table.Column<int>(type: "integer", nullable: false),
                    HardCutGapSongMsMin = table.Column<int>(type: "integer", nullable: false),
                    HardCutGapSongMsMax = table.Column<int>(type: "integer", nullable: false),
                    PostHitSafetyMs = table.Column<int>(type: "integer", nullable: false),
                    StrategyWeightsJson = table.Column<string>(type: "text", nullable: false),
                    AnalysisRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SelectionDiversityEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RecentExclusionCount = table.Column<int>(type: "integer", nullable: false),
                    DefaultMaxArtistPlaysPerHour = table.Column<int>(type: "integer", nullable: false),
                    DefaultArtistLookbackTracks = table.Column<int>(type: "integer", nullable: false),
                    FatigueFactor = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Studios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ApiKey = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    JobsCompleted = table.Column<int>(type: "integer", nullable: false),
                    JobsFailed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Studios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TalkBits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: false),
                    Premise = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CooldownDays = table.Column<int>(type: "integer", nullable: false),
                    PlayCount = table.Column<int>(type: "integer", nullable: false),
                    ExactReplayCount = table.Column<int>(type: "integer", nullable: false),
                    FreshRetellCount = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RetirementReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkBits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TalkBreaks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: true),
                    ModeratorId = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    TargetWindowStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    TargetWindowEndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RenderedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PlayedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkBreaks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransitionLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    OutgoingType = table.Column<string>(type: "text", nullable: false),
                    OutgoingId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncomingType = table.Column<string>(type: "text", nullable: false),
                    IncomingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Strategy = table.Column<string>(type: "text", nullable: false),
                    OverlapSeconds = table.Column<double>(type: "double precision", nullable: false),
                    GapMs = table.Column<int>(type: "integer", nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    ClipCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransitionLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArtistMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Biography = table.Column<string>(type: "text", nullable: false),
                    VoiceCreationPrompt = table.Column<string>(type: "text", nullable: false),
                    TtsEngine = table.Column<string>(type: "text", nullable: false, defaultValue: "qwen"),
                    VoiceId = table.Column<string>(type: "text", nullable: true),
                    VoiceReferencePath = table.Column<string>(type: "text", nullable: true),
                    VoiceDesignedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VoiceDesignLastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistMembers_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Genre = table.Column<string>(type: "text", nullable: false),
                    Subgenre = table.Column<string>(type: "text", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: true),
                    Style = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    HasVocals = table.Column<bool>(type: "boolean", nullable: false),
                    Lyrics = table.Column<string>(type: "text", nullable: true),
                    SongStory = table.Column<string>(type: "text", nullable: true),
                    TargetDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    GenerationPrompt = table.Column<string>(type: "text", nullable: false),
                    Backend = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PlayCount = table.Column<int>(type: "integer", nullable: false),
                    UpVotes = table.Column<int>(type: "integer", nullable: false),
                    DownVotes = table.Column<int>(type: "integer", nullable: false),
                    IsRetired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Announcements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ScriptText = table.Column<string>(type: "text", nullable: false),
                    VoicedText = table.Column<string>(type: "text", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    RelatedTrackId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    WasPlayed = table.Column<bool>(type: "boolean", nullable: false),
                    PlayoutIntent = table.Column<string>(type: "text", nullable: false, defaultValue: "Immediate")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Announcements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Announcements_Moderators_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Formats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Genre = table.Column<string>(type: "text", nullable: false),
                    Subgenre = table.Column<string>(type: "text", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Talkativeness = table.Column<double>(type: "double precision", nullable: false),
                    TalkDepth = table.Column<string>(type: "text", nullable: false),
                    TalkDensity = table.Column<double>(type: "double precision", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpVotes = table.Column<int>(type: "integer", nullable: false),
                    DownVotes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SelectionRules_Mode = table.Column<string>(type: "text", nullable: false, defaultValue: "StandardRotation"),
                    SelectionRules_FeaturedArtistId = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectionRules_MaxArtistPlaysPerHour = table.Column<int>(type: "integer", nullable: true),
                    SelectionRules_ArtistLookbackTracks = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                    SelectionRules_SubgenreRotation = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SelectionRules_PreferHostGenres = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SelectionRules_TargetBpm = table.Column<double>(type: "double precision", nullable: true),
                    SelectionRules_BpmTolerancePct = table.Column<double>(type: "double precision", nullable: true),
                    SelectionRules_Theme = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Formats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Formats_Moderators_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "NewsItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    ExtractedSummary = table.Column<string>(type: "text", nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SelectionReason = table.Column<string>(type: "text", nullable: true),
                    ProducedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsItems_NewsFeeds_FeedId",
                        column: x => x.FeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudioHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudioId = table.Column<Guid>(type: "uuid", nullable: true),
                    StudioName = table.Column<string>(type: "text", nullable: false),
                    StudioKind = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudioHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudioHistory_Studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "Studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TalkBitRenditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TalkBitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    CreatedFromRetelling = table.Column<bool>(type: "boolean", nullable: false),
                    PlayCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastPlayedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkBitRenditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TalkBitRenditions_TalkBits_TalkBitId",
                        column: x => x.TalkBitId,
                        principalTable: "TalkBits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TalkParts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TalkBreakId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedTrackId = table.Column<Guid>(type: "uuid", nullable: true),
                    TalkBitId = table.Column<Guid>(type: "uuid", nullable: true),
                    JingleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DesiredDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    WordBudget = table.Column<int>(type: "integer", nullable: true),
                    TargetWindowStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    TargetWindowEndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TalkParts_TalkBreaks_TalkBreakId",
                        column: x => x.TalkBreakId,
                        principalTable: "TalkBreaks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtistPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistPosts_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtistPosts_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Votes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ClientHint = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Votes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Votes_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TargetUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TargetDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ProducedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    QueuedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PlayedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    ProductionState = table.Column<string>(type: "text", nullable: true),
                    SourceSummary = table.Column<string>(type: "text", nullable: true),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    StepTotal = table.Column<int>(type: "integer", nullable: false),
                    ProducedSegmentsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsPackages_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProgramSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    FormatId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgramSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProgramSlots_Formats_FormatId",
                        column: x => x.FormatId,
                        principalTable: "Formats",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_ModeratorId",
                table: "Announcements",
                column: "ModeratorId");

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_PlayoutIntent",
                table: "Announcements",
                column: "PlayoutIntent");

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_WasPlayed",
                table: "Announcements",
                column: "WasPlayed");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistMembers_ArtistId_SortOrder",
                table: "ArtistMembers",
                columns: new[] { "ArtistId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistPosts_ArtistId",
                table: "ArtistPosts",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistPosts_CreatedAtUtc",
                table: "ArtistPosts",
                column: "CreatedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ArtistPosts_TrackId",
                table: "ArtistPosts",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Genre",
                table: "Artists",
                column: "Genre");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Name",
                table: "Artists",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Slug",
                table: "Artists",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Formats_ModeratorId",
                table: "Formats",
                column: "ModeratorId");

            migrationBuilder.CreateIndex(
                name: "IX_Jingles_CreatedAtUtc",
                table: "Jingles",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jingles_IsActive",
                table: "Jingles",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ListenerMessages_Status",
                table: "ListenerMessages",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAnalyses_ItemType_ItemId",
                table: "MediaAnalyses",
                columns: new[] { "ItemType", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModeratorMemories_ModeratorId_Layer_Date",
                table: "ModeratorMemories",
                columns: new[] { "ModeratorId", "Layer", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Moderators_Slug",
                table: "Moderators",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeeds_IsEnabled_LastPolledAtUtc",
                table: "NewsFeeds",
                columns: new[] { "IsEnabled", "LastPolledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeeds_Url",
                table: "NewsFeeds",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_ContentHash",
                table: "NewsItems",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_FeedId_Url",
                table: "NewsItems",
                columns: new[] { "FeedId", "Url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_Status_PublishedAtUtc_FirstSeenAtUtc",
                table: "NewsItems",
                columns: new[] { "Status", "PublishedAtUtc", "FirstSeenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsPackages_AnnouncementId",
                table: "NewsPackages",
                column: "AnnouncementId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsPackages_Kind_TargetUtc",
                table: "NewsPackages",
                columns: new[] { "Kind", "TargetUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsPackages_Status_TargetUtc",
                table: "NewsPackages",
                columns: new[] { "Status", "TargetUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayLog_ItemType_PlayedAt",
                table: "PlayLog",
                columns: new[] { "ItemType", "PlayedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayLog_PlayedAt",
                table: "PlayLog",
                column: "PlayedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProgramSlots_DayOfWeek_StartMinute",
                table: "ProgramSlots",
                columns: new[] { "DayOfWeek", "StartMinute" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProgramSlots_FormatId",
                table: "ProgramSlots",
                column: "FormatId");

            migrationBuilder.CreateIndex(
                name: "IX_StudioHistory_Status_StartedAtUtc",
                table: "StudioHistory",
                columns: new[] { "Status", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StudioHistory_StudioId_StartedAtUtc",
                table: "StudioHistory",
                columns: new[] { "StudioId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StudioHistory_StudioKind_StartedAtUtc",
                table: "StudioHistory",
                columns: new[] { "StudioKind", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Studios_Kind_IsActive",
                table: "Studios",
                columns: new[] { "Kind", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_TalkBitRenditions_TalkBitId",
                table: "TalkBitRenditions",
                column: "TalkBitId");

            migrationBuilder.CreateIndex(
                name: "IX_TalkBits_LastUsedAtUtc",
                table: "TalkBits",
                column: "LastUsedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TalkBits_ModeratorId_Status",
                table: "TalkBits",
                columns: new[] { "ModeratorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TalkBreaks_AnnouncementId",
                table: "TalkBreaks",
                column: "AnnouncementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TalkBreaks_Status_ExpiresAtUtc",
                table: "TalkBreaks",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TalkParts_Status_ExpiresAtUtc",
                table: "TalkParts",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TalkParts_TalkBreakId_SortOrder",
                table: "TalkParts",
                columns: new[] { "TalkBreakId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_ArtistId",
                table: "Tracks",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Genre",
                table: "Tracks",
                column: "Genre");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_IsRetired",
                table: "Tracks",
                column: "IsRetired");

            migrationBuilder.CreateIndex(
                name: "IX_TransitionLog_OccurredAt",
                table: "TransitionLog",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Votes_TrackId",
                table: "Votes",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistMembers");

            migrationBuilder.DropTable(
                name: "ArtistPosts");

            migrationBuilder.DropTable(
                name: "Jingles");

            migrationBuilder.DropTable(
                name: "ListenerMessages");

            migrationBuilder.DropTable(
                name: "MediaAnalyses");

            migrationBuilder.DropTable(
                name: "ModeratorMemories");

            migrationBuilder.DropTable(
                name: "NewsItems");

            migrationBuilder.DropTable(
                name: "NewsPackages");

            migrationBuilder.DropTable(
                name: "PlayLog");

            migrationBuilder.DropTable(
                name: "ProgramSlots");

            migrationBuilder.DropTable(
                name: "StationSettings");

            migrationBuilder.DropTable(
                name: "StudioHistory");

            migrationBuilder.DropTable(
                name: "TalkBitRenditions");

            migrationBuilder.DropTable(
                name: "TalkParts");

            migrationBuilder.DropTable(
                name: "TransitionLog");

            migrationBuilder.DropTable(
                name: "Votes");

            migrationBuilder.DropTable(
                name: "NewsFeeds");

            migrationBuilder.DropTable(
                name: "Announcements");

            migrationBuilder.DropTable(
                name: "Formats");

            migrationBuilder.DropTable(
                name: "Studios");

            migrationBuilder.DropTable(
                name: "TalkBits");

            migrationBuilder.DropTable(
                name: "TalkBreaks");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Moderators");

            migrationBuilder.DropTable(
                name: "Artists");
        }
    }
}
