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

internal static class CharacterRoleToolRules
{
    /// <summary>Hosts and the two specialist host roles: the on-air voices.</summary>
    public static bool IsOnAirVoice(this CharacterRole role)
        => role is CharacterRole.Host or CharacterRole.NewsSpecialist or CharacterRole.WeatherSpecialist;

    /// <summary>On-air voices plus the Program Director.</summary>
    public static bool IsOnAirVoiceOrDirector(this CharacterRole role)
        => role.IsOnAirVoice() || role is CharacterRole.ProgramDirector;
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
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
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
    [
        new("scope", "Optional focus: station (default), schedule, music, or production.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

public sealed class SearchArtistTool() : CharacterToolBase(
    "SearchArtist",
    "Find an artist matching a style; when none fits, the station writes a new one (creation takes a while, so do not re-request).",
    [
        new("style", "Desired sound or identity brief."),
        new("genre", "Preferred genre.", IsRequired: false),
        new("subgenre", "Preferred subgenre.", IsRequired: false),
        new("createIfMissing", "Create a new artist when no good match exists (default true).", IsRequired: false),
        new("limit", "Maximum existing matches to return.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

public sealed class GetArtistProfileTool() : CharacterToolBase(
    "GetArtistProfile",
    "Read an artist's public profile, members, and recent songs before an interview or a request.",
    [
        new("artist", "Artist name or id."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && (role.IsOnAirVoiceOrDirector() || role is CharacterRole.Artist);
}

public sealed class QueueTrackTool() : CharacterToolBase(
    "QueueTrack",
    "Request an existing library track for playout. Hosts can queue only during their own show.",
    [
        new("track", "Track title or id (search first if unsure)."),
        new("priority", "normal (default) or next; only the Program Director may jump the line.", IsRequired: false),
        new("reason", "Why the track should play.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

public sealed class PlanTalkBreakTool() : CharacterToolBase(
    "PlanTalkBreak",
    "Plan an ordered on-air talk break. Hosts plan for their own show; the Program Director can plan for any host.",
    [
        new("parts", "Ordered parts as semicolon-separated 'kind: purpose' entries (kinds: Banter, Intro, Outro, Weather, News, Ad, Bit)."),
        new("title", "Optional operator-visible title.", IsRequired: false),
        new("host", "Target host name or id (Program Director only; defaults to yourself).", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

public sealed class CreateTalkBitTool() : CharacterToolBase(
    "CreateTalkBit",
    "Create a reusable joke, anecdote, drop, or station bit around a premise. Hosts create for themselves; the Program Director for any host.",
    [
        new("premise", "Desired premise or theme."),
        new("kind", "joke, anecdote, drop, station_bit, or personal_note.", IsRequired: false),
        new("host", "Target host name or id (Program Director only; defaults to yourself).", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

public sealed class RememberTool() : CharacterToolBase(
    "Remember",
    "Store a short memory note about yourself for continuity.",
    [
        new("note", "Short factual memory note."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && (role.IsOnAirVoice() || role is CharacterRole.Artist);
}

public sealed class ProduceNewsPackageTool() : CharacterToolBase(
    "ProduceNewsPackage",
    "Produce the next news package, or recreate an existing one.",
    [
        new("mode", "next (default) or recreate.", IsRequired: false),
        new("packageId", "Package id to recreate (required for recreate).", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && role is CharacterRole.ProgramDirector or CharacterRole.NewsSpecialist;
}

public sealed class ProduceWeatherReportTool() : CharacterToolBase(
    "ProduceWeatherReport",
    "Produce a weather segment for the configured station location.",
    [
        new("presenter", "Weather specialist name or id (Program Director only; defaults to the configured presenter).", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat
            && role is CharacterRole.ProgramDirector or CharacterRole.WeatherSpecialist;
}

public sealed class CreateJingleTool() : CharacterToolBase(
    "CreateJingle",
    "Generate a new station jingle (music generation takes a while).",
    [
        new("label", "Operator-visible label."),
        new("style", "Musical style prompt."),
        new("durationSeconds", "Target duration in seconds.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetJingleActiveTool() : CharacterToolBase(
    "SetJingleActive",
    "Enable or disable an existing jingle.",
    [
        new("jingle", "Jingle label or id."),
        new("isActive", "true to enable, false to disable."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetNewsPresenterTool() : CharacterToolBase(
    "SetNewsPresenter",
    "Assign the active news specialist used for news packages.",
    [
        new("host", "Active news specialist name or id."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetWeatherPresenterTool() : CharacterToolBase(
    "SetWeatherPresenter",
    "Assign the active weather specialist used for weather segments.",
    [
        new("host", "Active weather specialist name or id."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class RetireTrackTool() : CharacterToolBase(
    "RetireTrack",
    "Remove a track from future rotation without deleting its file or history.",
    [
        new("track", "Track title or id."),
        new("reason", "Why the track should leave rotation."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class PostArtistFeedTool() : CharacterToolBase(
    "PostArtistFeed",
    "Post an update to your own artist feed in your own voice.",
    [
        new("body", "Post body."),
        new("track", "Optional own track title or id to link.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.Artist;
}

public sealed class RequestSongFromArtistTool() : CharacterToolBase(
    "RequestSongFromArtist",
    "Ask an artist or band to write a new song. They decide whether to record it.",
    [
        new("artist", "Artist, band, or band-member name."),
        new("brief", "Song request or creative direction."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

