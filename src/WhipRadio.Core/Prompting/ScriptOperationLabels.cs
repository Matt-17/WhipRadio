using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Prompting;

/// <summary>
/// Single source of truth for the human-readable name of a script-writing operation. Both the
/// Writers Room / studio history (which shows the LLM operation label) and the top-of-hour
/// package production progress derive their wording from here, so the same operation never reads
/// under two different names.
/// <para>
/// Keyed on the announcement <c>Purpose</c> — the most specific descriptor — because several
/// distinct operations (news/weather handovers, returns, the show close) all share
/// <see cref="AnnouncementKind.StationId"/> and would otherwise collapse to "station ID". Falls
/// back to the <see cref="AnnouncementKind"/> for free-standing announcements whose purpose is
/// just the kind name (song intros, jokes, banter, …).
/// </para>
/// </summary>
public static class ScriptOperationLabels
{
    /// <summary>The bare noun phrase for an operation, e.g. "news intro", "weather script".</summary>
    public static string Describe(AnnouncementKind kind, string? purpose) => purpose switch
    {
        "NewsHandover" => "news intro",
        "NewsReport" => "news script",
        "WeatherHandoff" => "weather handoff",
        "WeatherReport" => "weather script",
        "WeatherReturn" => "weather return",
        "ShowReturn" => "show return",
        _ => DescribeKind(kind),
    };

    /// <summary>The "Writing {phrase}" form used as the script-writing operation/progress label.</summary>
    public static string Writing(AnnouncementKind kind, string? purpose) => $"Writing {Describe(kind, purpose)}";

    /// <summary>The "Recording {phrase}" form used as the voice-booth operation label.</summary>
    public static string Recording(AnnouncementKind kind, string? purpose) => $"Recording {Describe(kind, purpose)}";

    private static string DescribeKind(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.SongIntro => "song intro",
        AnnouncementKind.SongOutro => "song outro",
        AnnouncementKind.Weather => "weather report",
        AnnouncementKind.News => "news bulletin",
        AnnouncementKind.Joke => "joke",
        AnnouncementKind.Banter => "banter",
        AnnouncementKind.PersonalNote => "personal note",
        AnnouncementKind.TalkBit => "talk bit",
        AnnouncementKind.EmergencyMessage => "emergency message",
        AnnouncementKind.HostChange => "host handover",
        AnnouncementKind.ListenerGreeting => "listener greeting",
        AnnouncementKind.RequestDedication => "song dedication",
        AnnouncementKind.StationId => "station ID",
        _ => "announcement",
    };
}
