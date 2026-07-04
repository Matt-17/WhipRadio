using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhipRadio.Core.Entities;

public enum ChatSenderKind
{
    Admin = 0,
    Host = 1,
    Director = 2,
    System = 3,
    ArtistMember = 4,
    Guest = 5,
}

public enum ChatActionState
{
    Parsed = 0,
    PendingConfirmation = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Dismissed = 5,
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public ChatChannel? Channel { get; set; }

    public ChatSenderKind SenderKind { get; set; }

    public int? SenderModeratorId { get; set; }

    public Moderator? SenderModerator { get; set; }

    public Guid? SenderArtistMemberId { get; set; }

    public ArtistMember? SenderArtistMember { get; set; }

    public Guid? SenderGuestId { get; set; }

    public Guest? SenderGuest { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? ActionsJson { get; set; }

    public Guid? CorrelationId { get; set; }

    public int HopCount { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record ChatActionRecord(
    string Tool,
    IReadOnlyDictionary<string, string> Arguments,
    ChatActionState State,
    string? ResultSummary,
    DateTime? CompletedAtUtc);

public static class ChatActionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IReadOnlyList<ChatActionRecord> actions)
        => JsonSerializer.Serialize(actions, Options);

    public static IReadOnlyList<ChatActionRecord> Deserialize(string? actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<ChatActionRecord>>(actionsJson, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
