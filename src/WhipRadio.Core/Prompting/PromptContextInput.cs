using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Prompting;

public sealed record PromptContextInput(
    PromptScope Scope,
    Moderator? Moderator = null,
    Format? Format = null,
    AnnouncementKind? AnnouncementKind = null,
    Track? RelatedTrack = null,
    string? Facts = null,
    string? LengthHint = null,
    string? Purpose = null,
    PromptPriority Priority = PromptPriority.Normal,
    int? TargetSeconds = null,
    string? AlreadySpokenContext = null,
    DateTimeOffset? LocalNowOverride = null);

public static class PromptWordBudget
{
    public static double BaseWordsPerSecond(string language)
        => 2.8;

    public static double WordsPerSecond(string language, double speechRate)
        => BaseWordsPerSecond(language) * Math.Clamp(speechRate, 0.5, 2.0);

    public static int EstimateWordBudget(string language, double speechRate, int seconds)
        => Math.Max(1, (int)Math.Round(WordsPerSecond(language, speechRate) * Math.Max(0, seconds)));

    public static int CountWords(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
}
