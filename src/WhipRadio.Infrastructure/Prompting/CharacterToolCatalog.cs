using WhipRadio.Core.Prompting;

namespace WhipRadio.Infrastructure.Prompting;

public sealed class CharacterToolCatalog(IEnumerable<ICharacterTool> tools) : ICharacterToolCatalog
{
    private readonly IReadOnlyList<ICharacterTool> tools = tools.ToList();

    public IReadOnlyList<CharacterToolDefinition> GetTools(PromptScope scope, CharacterRole role)
        => tools
            .Where(tool => tool.IsAvailable(scope, role))
            .Select(tool => tool.Definition)
            .OrderBy(tool => tool.Name)
            .ToList();

    public ICharacterTool? GetTool(string name, PromptScope scope, CharacterRole role)
        => tools.FirstOrDefault(tool =>
            tool.IsAvailable(scope, role)
            && string.Equals(tool.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
}

public abstract class CharacterToolBase(
    string name,
    string description,
    IReadOnlyList<CharacterToolArgument> arguments) : ICharacterTool
{
    public CharacterToolDefinition Definition { get; } = new(name, description, arguments);

    // Chat is opt-in: chat verbs have a dedicated executor; on-air tools like
    // Announce/Remember/NoOp have no chat handler and must not be offered there.
    public virtual bool IsAvailable(PromptScope scope, CharacterRole role)
        => role is not CharacterRole.System && scope is not PromptScope.Utility && scope is not PromptScope.Chat;
}

public sealed class AnnounceTool() : CharacterToolBase(
    "Announce",
    "Create spoken text for the current character to say on air.",
    [
        new("text", "The exact spoken text to announce."),
    ]);

public sealed class PlayTool() : CharacterToolBase(
    "Play",
    "Request a track to play by title or id when the scope allows music selection.",
    [
        new("track", "Track title or track id."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => role is CharacterRole.Host or CharacterRole.ProgramDirector
            && scope is PromptScope.CharacterDecision or PromptScope.ProgramDirector;
}

public sealed class MessageTool() : CharacterToolBase(
    "Message",
    "Send a message to another character, the Program Director, or the user.",
    [
        new("characterId", "Target character id or well-known name."),
        new("message", "Message body."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => role is not CharacterRole.System && scope is not PromptScope.Utility;
}

public sealed class AnnouncementTool() : CharacterToolBase(
    "Announcement",
    "Commission an on-air announcement in the current host voice.",
    [
        new("topic", "Topic or brief for the announcement."),
        new("priority", "normal, high, or emergency.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && role is CharacterRole.Host or CharacterRole.NewsSpecialist or CharacterRole.WeatherSpecialist;
}

public sealed class SearchMusicTool() : CharacterToolBase(
    "SearchMusic",
    "Search the music library by genre, mood, artist, title, or free text.",
    [
        new("query", "Search query."),
        new("limit", "Maximum results to return.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && role is CharacterRole.Host or CharacterRole.ProgramDirector;
}

public sealed class PlanFormatTool() : CharacterToolBase(
    "PlanFormat",
    "Plan a new show or replace what is scheduled: creates or reuses a format and writes it into the weekly schedule, overwriting overlapping slots.",
    [
        new("day", "Day name in English or German (such as Friday or Donnerstag), or today/tomorrow."),
        new("startTime", "Start time in HH:mm station-local format."),
        new("durationMinutes", "Duration in minutes, between 30 and 240."),
        new("genre", "Primary genre."),
        new("name", "Optional show format name.", IsRequired: false),
        new("description", "Optional show description.", IsRequired: false),
        new("host", "Optional host name or id.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class HireHostTool() : CharacterToolBase(
    "HireHost",
    "Create a new general radio host from a short brief.",
    [
        new("brief", "Short host brief."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class AssignHostTool() : CharacterToolBase(
    "AssignHost",
    "Assign an existing host to an existing format.",
    [
        new("format", "Format name or id."),
        new("host", "Host name or id."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class StatusReportTool() : CharacterToolBase(
    "StatusReport",
    "Summarize current station programming and operational state.",
    [])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class StartTalkBreakTool() : CharacterToolBase(
    "StartTalkBreak",
    "Plan an ordered on-air talk break from one or more parts.",
    [
        new("parts", "Ordered talk part descriptions."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => role is CharacterRole.Host or CharacterRole.ProgramDirector or CharacterRole.WeatherSpecialist
            && scope is PromptScope.CharacterDecision or PromptScope.ProgramDirector;
}

public sealed class RememberTool() : CharacterToolBase(
    "Remember",
    "Store a short memory note for continuity.",
    [
        new("note", "Short memory note."),
    ]);

public sealed class RequestBitTool() : CharacterToolBase(
    "RequestBit",
    "Ask for a reusable joke, anecdote, drop, or station bit around a premise.",
    [
        new("premise", "Desired premise or theme."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => role is CharacterRole.Host or CharacterRole.ProgramDirector
            && scope is PromptScope.CharacterDecision or PromptScope.ProgramDirector;
}

public sealed class NoOpTool() : CharacterToolBase(
    "NoOp",
    "Choose to do nothing when speaking or acting would be inappropriate.",
    [
        new("reason", "Brief reason for staying quiet.", IsRequired: false),
    ]);
