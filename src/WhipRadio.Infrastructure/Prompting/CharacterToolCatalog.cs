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
    "Create a new radio host from a short brief. Set role to news or weather to create a specialist presenter.",
    [
        new("brief", "Short host brief."),
        new("role", "general (default), news, or weather.", IsRequired: false),
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

// ── Approval flow ────────────────────────────────────────────────────────────

public sealed class RequestBossApprovalTool() : CharacterToolBase(
    "RequestBossApproval",
    "Ask the Boss to confirm a pending destructive or authority-sensitive action before it runs.",
    [
        new("actionTool", "The tool name that needs approval."),
        new("summary", "Human-readable summary of what will happen.", IsRequired: false),
        new("argumentsJson", "JSON object of the pending action's arguments.", IsRequired: false),
        new("risk", "schedule, personnel, library, external, settings, or cost.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

// ── Library (destructive) ────────────────────────────────────────────────────

public sealed class RetireArtistTool() : CharacterToolBase(
    "RetireArtist",
    "Stop an artist from getting new material, without deleting their songs or history.",
    [
        new("artist", "Artist name or id."),
        new("reason", "Why the artist is retired."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class DeleteArtistTool() : CharacterToolBase(
    "DeleteArtist",
    "Delete an artist that has no songs (needs Boss approval). Retire artists that have released tracks instead.",
    [
        new("artist", "Exact artist name or id."),
        new("reason", "Why the artist is deleted."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class DeleteTrackTool() : CharacterToolBase(
    "DeleteTrack",
    "Delete a track's row and audio file (needs Boss approval). Use RetireTrack to just stop rotation.",
    [
        new("track", "Exact track title or id."),
        new("reason", "Why the track is deleted."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class DeleteJingleTool() : CharacterToolBase(
    "DeleteJingle",
    "Delete a jingle and its audio file (needs Boss approval).",
    [
        new("jingle", "Exact jingle label or id."),
        new("reason", "Why the jingle is deleted."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class RedefineArtistProfileTool() : CharacterToolBase(
    "RedefineArtistProfile",
    "Rewrite an artist's persona/biography while keeping their name and songs. Needs Boss approval when they have released tracks.",
    [
        new("artist", "Artist name or id."),
        new("hint", "What to repair or emphasise.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class CancelSongProductionTool() : CharacterToolBase(
    "CancelSongProduction",
    "Cancel the song currently being produced. Artists cancel their own; the director cancels any (needs Boss approval).",
    [
        new("reason", "Why production is cancelled."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && (role is CharacterRole.Artist or CharacterRole.ProgramDirector);
}

// ── Schedule / personnel (destructive) ───────────────────────────────────────

public sealed class RemoveShowTool() : CharacterToolBase(
    "RemoveShow",
    "Remove a scheduled slot or disable a whole format (needs Boss approval).",
    [
        new("scope", "slot_only (default) or disable_format.", IsRequired: false),
        new("slot", "Slot id (for slot_only).", IsRequired: false),
        new("format", "Format name or id (for disable_format).", IsRequired: false),
        new("reason", "Why the show is removed."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class FireHostTool() : CharacterToolBase(
    "FireHost",
    "Deactivate a host and clean up their assignments (needs Boss approval).",
    [
        new("host", "Host name or id."),
        new("reason", "Why the host is fired."),
        new("replacement", "Optional active host to take over their formats.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

// ── Production ────────────────────────────────────────────────────────────────

public sealed class EmergencyAnnouncementTool() : CharacterToolBase(
    "EmergencyAnnouncement",
    "Air an urgent station message at the front of playout. Emergency priority needs Boss approval unless the Boss triggers it.",
    [
        new("text", "Exact emergency message content."),
        new("priority", "high (default) or emergency.", IsRequired: false),
        new("moderator", "Host voice to use (defaults to the current host).", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

public sealed class AnswerListenerMessageTool() : CharacterToolBase(
    "AnswerListenerMessage",
    "Handle a listener greeting or request: queue it on air, or dismiss it.",
    [
        new("messageId", "Listener message id."),
        new("action", "queue_greeting, queue_dedication, dismiss, or reply_in_chat."),
        new("reason", "Dismissal or routing reason.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

// ── News / weather / settings (director) ─────────────────────────────────────

public sealed class ManageNewsFeedTool() : CharacterToolBase(
    "ManageNewsFeed",
    "Add, update, toggle, or delete a station news feed. Add/update/delete need Boss approval.",
    [
        new("operation", "add, update, toggle, or delete."),
        new("feedId", "Feed id (for update/toggle/delete).", IsRequired: false),
        new("label", "Feed label.", IsRequired: false),
        new("url", "RSS/feed URL (for add/update).", IsRequired: false),
        new("language", "Feed language.", IsRequired: false),
        new("region", "Region key.", IsRequired: false),
        new("category", "Category key.", IsRequired: false),
        new("reason", "Why the feed changes."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetNewsProductionSettingsTool() : CharacterToolBase(
    "SetNewsProductionSettings",
    "Change news production settings (needs Boss approval). settingsJson keys: newsEnabled, newsLongFormatEnabled, cadenceMinutes, maxDurationSeconds.",
    [
        new("settingsJson", "JSON object of allowed news settings."),
        new("reason", "Why settings change."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetWeatherSettingsTool() : CharacterToolBase(
    "SetWeatherSettings",
    "Change weather settings (location changes need Boss approval). settingsJson keys: weatherEnabled, cadenceMinutes, weatherFullHandoverEnabled, weatherLocationName, weatherLatitude, weatherLongitude.",
    [
        new("settingsJson", "JSON object of allowed weather settings."),
        new("reason", "Why settings change."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetStationSettingsTool() : CharacterToolBase(
    "SetStationSettings",
    "Change non-secret station settings (needs Boss approval). settingsJson keys: stationName, stationSlogan, stationVision, stationMission, defaultLanguage, targetQueueLength.",
    [
        new("settingsJson", "JSON object of allowed station settings."),
        new("reason", "Why settings change."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetProductionSwitchTool() : CharacterToolBase(
    "SetProductionSwitch",
    "Enable or disable a station production switch. Turning playout off needs Boss approval.",
    [
        new("switch", "musicProduction, playout, news, weather, or greetings."),
        new("enabled", "true to enable, false to disable."),
        new("reason", "Why the switch changes."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class SetProviderSettingsTool() : CharacterToolBase(
    "SetProviderSettings",
    "Change non-secret model/provider defaults (needs Boss approval). Never sets API keys. Areas: text (textProvider, openAiModel), music (defaultMusicProvider).",
    [
        new("providerArea", "text or music."),
        new("settingsJson", "JSON object of allowed non-secret provider settings."),
        new("reason", "Why settings change."),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

// ── Operations / diagnostics (read-only) ─────────────────────────────────────

public sealed class StudioStatusTool() : CharacterToolBase(
    "StudioStatus",
    "Read studio runtime status to explain why generation or announcements are slow.",
    [
        new("kind", "writer, recording, voice, analysis, or all.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role.IsOnAirVoiceOrDirector();
}

public sealed class ServerStatusTool() : CharacterToolBase(
    "ServerStatus",
    "Read host CPU, memory, disk, and GPU diagnostics.",
    [
        new("detail", "brief or full.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class PrivacyReportTool() : CharacterToolBase(
    "PrivacyReport",
    "Read which external services the station recently contacted (no secrets).",
    [
        new("classification", "all, local, external, or cloud.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class MediaCleanupPreviewTool() : CharacterToolBase(
    "MediaCleanupPreview",
    "Preview how many unreferenced media files could be cleaned up. Read-only; returns a token for RunMediaCleanup.",
    [
        new("area", "tracks, announcements, or all.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

public sealed class RunMediaCleanupTool() : CharacterToolBase(
    "RunMediaCleanup",
    "Delete unreferenced media files after a preview (needs a fresh preview token and Boss approval).",
    [
        new("previewToken", "Token from a recent MediaCleanupPreview."),
        new("reason", "Cleanup reason."),
        new("area", "tracks, announcements, or all.", IsRequired: false),
    ])
{
    public override bool IsAvailable(PromptScope scope, CharacterRole role)
        => scope is PromptScope.Chat && role is CharacterRole.ProgramDirector;
}

