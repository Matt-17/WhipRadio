namespace WhipRadio.Core.Entities;

public enum ChatChannelKind
{
    Station = 0,
    HostDm = 1,
    DirectorDm = 2,
    HostToHost = 3,
}

public class ChatChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ChatChannelKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? ModeratorId { get; set; }

    public Moderator? Moderator { get; set; }

    public int? CounterpartModeratorId { get; set; }

    public Moderator? CounterpartModerator { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastMessageAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? AdminLastReadAtUtc { get; set; }

    public bool IsArchived { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];
}
