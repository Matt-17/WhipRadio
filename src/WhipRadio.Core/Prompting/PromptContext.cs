using System.Text;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Personality;

namespace WhipRadio.Core.Prompting;

public enum PromptScope
{
    AnnouncementScript,
    VoiceDirection,
    ProgramDirector,
    MessageModeration,
    CharacterDecision,
    Chat,
    Utility,
}

public enum PromptPriority
{
    Low,
    Normal,
    High,
    Emergency,
    Scheduled,
}

public enum CharacterRole
{
    Host,
    ProgramDirector,
    Guest,
    Artist,
    User,
    NewsSpecialist,
    WeatherSpecialist,
    System,
}

public sealed class PromptContext
{
    public PromptScope Scope { get; init; }

    public PromptPriority Priority { get; init; } = PromptPriority.Normal;

    public string Purpose { get; init; } = string.Empty;

    public string StationName { get; init; } = string.Empty;

    public double FrequencyMhz { get; init; }

    public string? StationSlogan { get; init; }

    public string? StationVision { get; init; }

    public string? StationMission { get; init; }

    public DateTimeOffset LocalNow { get; init; }

    public string Language { get; init; } = "en";

    public string? FormatName { get; init; }

    public string? FormatPurpose { get; init; }

    public TalkDepth? FormatTalkDepth { get; init; }

    public double? FormatTalkDensity { get; init; }

    public int? RemainingSlotMinutes { get; init; }

    public string? NextFormatName { get; init; }

    public string? HostName { get; init; }

    public string? PersonaSummary { get; init; }

    public HostPersonalityTraits? BaselineTraits { get; init; }

    public HostPersonalityTraits? CurrentTraits { get; init; }

    public HostTalkProfile? TalkProfile { get; init; }

    public string? RelatedTrack { get; init; }

    public string? AlreadySpokenContext { get; init; }

    public double SpeechRate { get; init; } = 1.0;

    public double WordsPerSecond { get; init; }

    public int? AvailableSeconds { get; init; }

    public int? WordBudget { get; init; }

    public IReadOnlyList<string> RecentTracks { get; init; } = [];

    /// <summary>Tracks aired since the current show started (Artist - Title (Genre, HH:mm)).</summary>
    public IReadOnlyList<string> CurrentShowTracks { get; init; } = [];

    /// <summary>Tracks aired during the previous show (Artist - Title (Genre, HH:mm)).</summary>
    public IReadOnlyList<string> PreviousShowTracks { get; init; } = [];

    public IReadOnlyList<string> RecentTalkTopics { get; init; } = [];

    public IReadOnlyList<string> RecurringBits { get; init; } = [];

    public IReadOnlyList<string> QueuedListenerMessages { get; init; } = [];

    public IReadOnlyList<string> MemorySlices { get; init; } = [];

    public IReadOnlyList<string> ChatHistory { get; init; } = [];

    public string? ChatAudience { get; init; }

    public IReadOnlyList<CharacterToolDefinition> Tools { get; init; } = [];

    public string RenderSituation()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Current situation:");
        builder.AppendLine($"- Station: {StationName} ({FrequencyMhz:0.0} MHz)");

        if (!string.IsNullOrWhiteSpace(StationSlogan))
        {
            builder.AppendLine($"- Slogan: {StationSlogan}");
        }

        if (!string.IsNullOrWhiteSpace(StationVision))
        {
            builder.AppendLine($"- Vision: {StationVision}");
        }

        if (!string.IsNullOrWhiteSpace(StationMission))
        {
            builder.AppendLine($"- Mission: {StationMission}");
        }

        builder.AppendLine($"- Local time: {LocalNow:dddd, yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- Purpose: {Purpose}");
        builder.AppendLine($"- Priority: {Priority}");

        if (!string.IsNullOrWhiteSpace(FormatName))
        {
            builder.AppendLine($"- Active format: {FormatName}");
        }

        if (!string.IsNullOrWhiteSpace(FormatPurpose))
        {
            builder.AppendLine($"- Format purpose: {FormatPurpose}");
        }

        if (FormatTalkDepth is not null)
        {
            builder.AppendLine($"- Format talk depth: {FormatTalkDepth}");
        }

        if (FormatTalkDensity is not null)
        {
            builder.AppendLine($"- Format talk density: {FormatTalkDensity:0.##}");
        }

        if (RemainingSlotMinutes is not null)
        {
            builder.AppendLine($"- Remaining slot time: {RemainingSlotMinutes} minutes");
        }

        if (!string.IsNullOrWhiteSpace(NextFormatName))
        {
            builder.AppendLine($"- Next format: {NextFormatName}");
        }

        if (!string.IsNullOrWhiteSpace(HostName))
        {
            builder.AppendLine($"- Host: {HostName}");
        }

        if (!string.IsNullOrWhiteSpace(PersonaSummary))
        {
            builder.AppendLine($"- Host persona: {PersonaSummary}");
        }

        if (BaselineTraits is not null)
        {
            builder.AppendLine($"- Host baseline traits: {BaselineTraits}");
        }

        if (CurrentTraits is not null)
        {
            builder.AppendLine($"- Current mood traits: {CurrentTraits}");
        }

        if (TalkProfile is not null)
        {
            builder.AppendLine(
                $"- Host talk profile: break frequency every {TalkProfile.BreakFrequencyTracks} track(s); " +
                $"parts {TalkProfile.MinPartsPerBreak}-{TalkProfile.MaxPartsPerBreak}; " +
                $"exact replay tolerance {TalkProfile.ExactReplayTolerance}; " +
                $"evergreen tolerance {TalkProfile.EvergreenBitTolerance:0.##}");
            builder.AppendLine($"- Allowed talk kinds: {string.Join(", ", TalkProfile.AllowedKinds)}");
        }

        if (!string.IsNullOrWhiteSpace(RelatedTrack))
        {
            builder.AppendLine($"- Related track: {RelatedTrack}");
        }

        if (!string.IsNullOrWhiteSpace(AlreadySpokenContext))
        {
            builder.AppendLine(
                "- Already aired immediately before this segment: " +
                $"{AlreadySpokenContext.Trim()} Do not repeat or reintroduce that information.");
        }

        builder.AppendLine($"- Language: {Language}");
        builder.AppendLine($"- Speech rate: {SpeechRate:0.##}x");

        if (AvailableSeconds is not null && WordBudget is not null)
        {
            builder.AppendLine(
                $"- Time-on-air math: you have {AvailableSeconds} seconds before the next scheduled item; " +
                $"that is roughly {WordBudget} words at this speaking rate.");
        }

        AppendAiredTracks(builder, "Tracks already aired in this show", CurrentShowTracks);
        AppendAiredTracks(builder, "Tracks aired in the previous show", PreviousShowTracks);
        AppendList(builder, "Recent tracks", RecentTracks);
        AppendList(builder, "Recent talk topics", RecentTalkTopics);
        AppendList(builder, "Recurring bits", RecurringBits);
        AppendList(builder, "Queued listener messages", QueuedListenerMessages);
        AppendList(builder, "Memory", MemorySlices);

        if (!string.IsNullOrWhiteSpace(ChatAudience))
        {
            builder.AppendLine($"- Chat audience: {ChatAudience}");
        }

        AppendList(builder, "Chat conversation", ChatHistory);

        if (Tools.Count > 0)
        {
            if (Scope == PromptScope.Chat)
            {
                builder.AppendLine(
                    "For chat, respond with exactly one JSON object and nothing else, " +
                    """in the form {"reply":"<message prose>","actions":[{"tool":"<ToolName>","arguments":{"<argument>":"<value>"}}]}. """ +
                    "Use an empty actions array when no action is needed. Available tools:");
            }
            else
            {
                builder.AppendLine(
                    "If you are choosing an action, respond with exactly one JSON object and nothing else, " +
                    """in the form {"tool":"<ToolName>","arguments":{"<argument>":"<value>"}}. """ +
                    "Available tools:");
            }

            foreach (var tool in Tools)
            {
                var args = tool.Arguments.Count == 0
                    ? "(no arguments)"
                    : string.Join(", ", tool.Arguments.Select(a =>
                        $"{a.Name} ({a.JsonType}{(a.IsRequired ? ", required" : ", optional")})"));
                builder.AppendLine($"- {tool.Name}: {tool.Description} — arguments: {args}");
            }
        }

        return builder.ToString().Trim();
    }

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.AppendLine($"- {label}:");
        foreach (var value in values)
        {
            builder.AppendLine($"  - {value}");
        }
    }

    /// <summary>
    /// Lists aired tracks with an explicit anti-repeat instruction, mirroring the
    /// existing AlreadySpokenContext pattern. The host must not reintroduce or
    /// back-announce these as if they were new.
    /// </summary>
    private static void AppendAiredTracks(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.AppendLine($"- {label} (do NOT reintroduce or back-announce these as if new):");
        foreach (var value in values)
        {
            builder.AppendLine($"  - {value}");
        }
    }
}
