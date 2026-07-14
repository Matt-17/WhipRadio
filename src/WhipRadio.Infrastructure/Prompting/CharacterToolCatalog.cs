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

    // Chat is the only scope with a tool executor (ChatActionExecutor). Every
    // other scope parses a fixed structured-JSON schema and never runs a tool,
    // so the base default hides every tool everywhere. Chat-capable tools opt in
    // explicitly with `scope is PromptScope.Chat && <roles>`.
    public virtual bool IsAvailable(PromptScope scope, CharacterRole role)
        => false;
}

public sealed class MessageTool() : CharacterToolBase(
    "Message",
    "Send a message to another character, the Program Director, or the user.",
    [
        new("characterId", "Target character id or well-known name."),
        new("message", "Message body."),
    ])
{
    // Artists and guests speak in their own channel only — no DM hopping.
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && role is not CharacterRole.System and not CharacterRole.Artist and not CharacterRole.Guest;
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

public sealed class LookupKnowledgeTool() : CharacterToolBase(
    "LookupKnowledge",
    "Look up gathered background facts about a real artist or an imported track (open-data digests). Paraphrase the facts; never present them as quotes.",
    [
        new("query", "Artist name or track title to look up."),
    ])
{
    // Offered in chat only when the station's knowledge setting is on —
    // PromptContextBuilder filters it out of the tool list when disabled.
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && role is CharacterRole.Host or CharacterRole.ProgramDirector or CharacterRole.NewsSpecialist;
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

public sealed class MakeSongTool() : CharacterToolBase(
    "MakeSong",
    "Commission a new song: artists record one themselves; the Program Director names the artist.",
    [
        new("hint", "Optional topic, mood, or style direction for the song.", IsRequired: false),
        new("artist", "Artist or band name (Program Director only; artists record their own).", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.Artist or CharacterRole.ProgramDirector;
}

public sealed class BriefPodcastTool() : CharacterToolBase(
    "BriefPodcast",
    "Brief a one-off podcast/talk: names the speakers, the topic, and optionally songs to reference and play around it.",
    [
        new("participants", "Comma-separated speaker names (hosts, band members, guests; a band name expands to its voiced members)."),
        new("topic", "Episode topic."),
        new("brief", "What the conversation should cover.", IsRequired: false),
        new("tracks", "Comma-separated track titles to reference and schedule around the episode.", IsRequired: false),
        new("durationMinutes", "Target duration in minutes (10-30).", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class InviteTool() : CharacterToolBase(
    "Invite",
    "Invite a host, band member, or guest into a group chat channel.",
    [
        new("participant", "Name of the host, band member, or guest to invite."),
        new("channel", "Target group channel name; defaults to the current group channel.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class RemoveFromChannelTool() : CharacterToolBase(
    "RemoveFromChannel",
    "Remove a participant from a group chat channel.",
    [
        new("participant", "Name of the participant to remove."),
        new("channel", "Target group channel name; defaults to the current group channel.", IsRequired: false),
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

