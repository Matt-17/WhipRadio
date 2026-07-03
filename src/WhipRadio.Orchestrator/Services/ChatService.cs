using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public sealed class ChatService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IHubContext<RadioHub> hub,
    TimeProvider timeProvider,
    ILogger<ChatService> logger)
{
    private const int MaxTake = 100;
    private const int PreviewLength = 80;

    public async Task<IReadOnlyList<ChatChannelDto>> GetChannelsAsync(CancellationToken ct)
    {
        await EnsureChannelsAsync(ct);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<ChatChannel> channels = await db.ChatChannels.AsNoTracking()
            .Include(channel => channel.Moderator)
            .Include(channel => channel.CounterpartModerator)
            .ToListAsync(ct);

        // Stable rail order (Station, Director, hosts A-Z, exchanges, archived
        // last) so the list never jumps around while the admin is clicking.
        channels = channels
            .OrderBy(channel => channel.IsArchived)
            .ThenBy(channel => KindRank(channel.Kind))
            .ThenBy(ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<Guid> channelIds = channels.Select(channel => channel.Id).ToList();
        Dictionary<Guid, ChatMessage> latestMessages = await db.ChatMessages.AsNoTracking()
            .Where(message => channelIds.Contains(message.ChannelId))
            .GroupBy(message => message.ChannelId)
            .Select(group => group.OrderByDescending(message => message.CreatedAtUtc).First())
            .ToDictionaryAsync(message => message.ChannelId, ct);

        Dictionary<Guid, int> unreadCounts = [];
        foreach (ChatChannel channel in channels)
        {
            DateTime? readAt = channel.AdminLastReadAtUtc;
            int unread = await db.ChatMessages.AsNoTracking()
                .Where(message => message.ChannelId == channel.Id
                    && message.SenderKind != ChatSenderKind.Admin
                    && (readAt == null || message.CreatedAtUtc > readAt))
                .CountAsync(ct);
            unreadCounts[channel.Id] = unread;
        }

        return channels
            .Select(channel => ToChannelDto(
                channel,
                latestMessages.GetValueOrDefault(channel.Id),
                unreadCounts.GetValueOrDefault(channel.Id)))
            .ToList();
    }

    public async Task<PagedChatMessagesDto> GetMessagesAsync(
        Guid channelId,
        DateTime? beforeUtc,
        int take,
        CancellationToken ct)
    {
        await EnsureChannelsAsync(ct);
        int pageSize = Math.Clamp(take <= 0 ? 50 : take, 1, MaxTake);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        bool exists = await db.ChatChannels.AsNoTracking().AnyAsync(channel => channel.Id == channelId, ct);
        if (!exists)
        {
            throw new KeyNotFoundException($"Chat channel {channelId} was not found.");
        }

        IQueryable<ChatMessage> query = db.ChatMessages.AsNoTracking()
            .Include(message => message.SenderModerator)
            .Where(message => message.ChannelId == channelId);

        if (beforeUtc is { } before)
        {
            // Query-string binding yields Local/Unspecified kinds; timestamptz
            // parameters must be Kind=Utc (the value already represents UTC).
            var beforeAsUtc = before.Kind switch
            {
                DateTimeKind.Utc => before,
                DateTimeKind.Local => before.ToUniversalTime(),
                _ => DateTime.SpecifyKind(before, DateTimeKind.Utc),
            };
            query = query.Where(message => message.CreatedAtUtc < beforeAsUtc);
        }

        List<ChatMessage> page = await query
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        bool hasMore = page.Count > pageSize;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        page.Reverse();
        return new PagedChatMessagesDto(page.Select(ToMessageDto).ToList(), hasMore);
    }

    public async Task<ChatMessageDto> PostAsync(
        Guid channelId,
        ChatSenderKind kind,
        int? moderatorId,
        string text,
        string? actionsJson,
        Guid? correlationId,
        int hopCount,
        CancellationToken ct)
    {
        string trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) && string.IsNullOrWhiteSpace(actionsJson))
        {
            throw new ArgumentException("Chat message text is required.", nameof(text));
        }

        if (kind == ChatSenderKind.System && !string.IsNullOrWhiteSpace(actionsJson))
        {
            throw new InvalidOperationException("System chat messages cannot carry actions.");
        }

        await EnsureChannelsAsync(ct);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel channel = await db.ChatChannels.FirstOrDefaultAsync(item => item.Id == channelId, ct)
            ?? throw new KeyNotFoundException($"Chat channel {channelId} was not found.");
        if (channel.IsArchived && kind == ChatSenderKind.Admin)
        {
            throw new InvalidOperationException("Cannot post to an archived chat channel.");
        }

        ChatMessage message = new()
        {
            Id = Guid.NewGuid(),
            ChannelId = channelId,
            SenderKind = kind,
            SenderModeratorId = moderatorId,
            Text = trimmed,
            ActionsJson = actionsJson,
            CorrelationId = correlationId,
            HopCount = Math.Max(0, hopCount),
            CreatedAtUtc = now,
        };

        channel.LastMessageAtUtc = now;
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);

        ChatMessage persisted = await db.ChatMessages.AsNoTracking()
            .Include(item => item.SenderModerator)
            .FirstAsync(item => item.Id == message.Id, ct);
        ChatMessageDto messageDto = ToMessageDto(persisted);
        ChatChannelDto channelDto = await BuildChannelDtoAsync(channelId, ct);

        await BroadcastMessageAsync(messageDto, ct);
        await BroadcastChannelAsync(channelDto, ct);
        return messageDto;
    }

    public async Task MarkReadAsync(Guid channelId, CancellationToken ct)
    {
        await EnsureChannelsAsync(ct);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel channel = await db.ChatChannels.FirstOrDefaultAsync(item => item.Id == channelId, ct)
            ?? throw new KeyNotFoundException($"Chat channel {channelId} was not found.");
        channel.AdminLastReadAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await BroadcastChannelAsync(await BuildChannelDtoAsync(channelId, ct), ct);
    }

    public async Task UpdateActionsAsync(Guid messageId, string actionsJson, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatMessage message = await db.ChatMessages.FirstOrDefaultAsync(item => item.Id == messageId, ct)
            ?? throw new KeyNotFoundException($"Chat message {messageId} was not found.");
        message.ActionsJson = actionsJson;
        await db.SaveChangesAsync(ct);

        ChatMessage persisted = await db.ChatMessages.AsNoTracking()
            .Include(item => item.SenderModerator)
            .FirstAsync(item => item.Id == messageId, ct);
        await BroadcastMessageAsync(ToMessageDto(persisted), ct);
        await BroadcastChannelAsync(await BuildChannelDtoAsync(persisted.ChannelId, ct), ct);
    }

    public async Task DismissActionAsync(Guid messageId, int actionIndex, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatMessage message = await db.ChatMessages.FirstOrDefaultAsync(item => item.Id == messageId, ct)
            ?? throw new KeyNotFoundException($"Chat message {messageId} was not found.");
        List<ChatActionRecord> actions = ChatActionJson.Deserialize(message.ActionsJson).ToList();
        if (actionIndex < 0 || actionIndex >= actions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(actionIndex), "Action index is out of range.");
        }

        ChatActionRecord current = actions[actionIndex];
        actions[actionIndex] = current with
        {
            State = ChatActionState.Dismissed,
            CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            ResultSummary = current.ResultSummary ?? "Dismissed by admin.",
        };
        message.ActionsJson = ChatActionJson.Serialize(actions);
        await db.SaveChangesAsync(ct);

        ChatMessage persisted = await db.ChatMessages.AsNoTracking()
            .Include(item => item.SenderModerator)
            .FirstAsync(item => item.Id == messageId, ct);
        await BroadcastMessageAsync(ToMessageDto(persisted), ct);
    }

    public async Task<Guid> GetStationChannelIdAsync(CancellationToken ct)
    {
        await EnsureChannelsAsync(ct);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ChatChannels.AsNoTracking()
            .Where(channel => channel.Kind == ChatChannelKind.Station)
            .Select(channel => channel.Id)
            .FirstAsync(ct);
    }

    public async Task<Guid> GetDirectorChannelIdAsync(CancellationToken ct)
    {
        await EnsureChannelsAsync(ct);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ChatChannels.AsNoTracking()
            .Where(channel => channel.Kind == ChatChannelKind.DirectorDm)
            .Select(channel => channel.Id)
            .FirstAsync(ct);
    }

    public async Task<Guid?> GetHostDmChannelIdAsync(int moderatorId, CancellationToken ct)
    {
        await EnsureChannelsAsync(ct);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ChatChannels.AsNoTracking()
            .Where(channel => channel.Kind == ChatChannelKind.HostDm
                && channel.ModeratorId == moderatorId
                && !channel.IsArchived)
            .Select(channel => (Guid?)channel.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Guid> GetOrCreateHostToHostChannelAsync(
        int firstModeratorId,
        int secondModeratorId,
        CancellationToken ct)
    {
        if (firstModeratorId == secondModeratorId)
        {
            throw new InvalidOperationException("A host-to-host channel needs two different hosts.");
        }

        int left = Math.Min(firstModeratorId, secondModeratorId);
        int right = Math.Max(firstModeratorId, secondModeratorId);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel? existing = await db.ChatChannels
            .FirstOrDefaultAsync(channel => channel.Kind == ChatChannelKind.HostToHost
                && channel.ModeratorId == left
                && channel.CounterpartModeratorId == right, ct);
        if (existing is not null)
        {
            if (existing.IsArchived)
            {
                existing.IsArchived = false;
                await db.SaveChangesAsync(ct);
                await BroadcastChannelAsync(await BuildChannelDtoAsync(existing.Id, ct), ct);
            }

            return existing.Id;
        }

        Dictionary<int, Moderator> moderators = await db.Moderators.AsNoTracking()
            .Where(moderator => moderator.Id == left || moderator.Id == right)
            .ToDictionaryAsync(moderator => moderator.Id, ct);
        if (!moderators.TryGetValue(left, out Moderator? first)
            || !moderators.TryGetValue(right, out Moderator? second)
            || !first.IsActive
            || !second.IsActive)
        {
            throw new InvalidOperationException("Both hosts must be active to create a host-to-host channel.");
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        ChatChannel channel = new()
        {
            Id = Guid.NewGuid(),
            Kind = ChatChannelKind.HostToHost,
            Name = $"{first.Name} <-> {second.Name}",
            ModeratorId = left,
            CounterpartModeratorId = right,
            CreatedAtUtc = now,
            LastMessageAtUtc = now,
        };
        db.ChatChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        await BroadcastChannelAsync(await BuildChannelDtoAsync(channel.Id, ct), ct);
        return channel.Id;
    }

    public async Task EnsureChannelsAsync(CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        bool changed = false;

        if (!await db.ChatChannels.AnyAsync(channel => channel.Kind == ChatChannelKind.Station, ct))
        {
            db.ChatChannels.Add(new ChatChannel
            {
                Kind = ChatChannelKind.Station,
                Name = "Station",
                CreatedAtUtc = now,
                LastMessageAtUtc = now,
            });
            changed = true;
        }

        if (!await db.ChatChannels.AnyAsync(channel => channel.Kind == ChatChannelKind.DirectorDm, ct))
        {
            db.ChatChannels.Add(new ChatChannel
            {
                Kind = ChatChannelKind.DirectorDm,
                Name = "Program Director",
                CreatedAtUtc = now,
                LastMessageAtUtc = now,
            });
            changed = true;
        }

        List<Moderator> moderators = await db.Moderators.AsNoTracking()
            .OrderBy(moderator => moderator.Name)
            .ToListAsync(ct);
        HashSet<int> activeIds = moderators.Where(moderator => moderator.IsActive)
            .Select(moderator => moderator.Id)
            .ToHashSet();
        List<ChatChannel> hostChannels = await db.ChatChannels
            .Where(channel => channel.Kind == ChatChannelKind.HostDm)
            .ToListAsync(ct);

        foreach (Moderator moderator in moderators.Where(moderator => moderator.IsActive))
        {
            ChatChannel? channel = hostChannels.FirstOrDefault(item => item.ModeratorId == moderator.Id);
            if (channel is null)
            {
                db.ChatChannels.Add(new ChatChannel
                {
                    Kind = ChatChannelKind.HostDm,
                    Name = moderator.Name,
                    ModeratorId = moderator.Id,
                    CreatedAtUtc = now,
                    LastMessageAtUtc = now,
                });
                changed = true;
                continue;
            }

            if (channel.Name != moderator.Name || channel.IsArchived)
            {
                channel.Name = moderator.Name;
                channel.IsArchived = false;
                changed = true;
            }
        }

        foreach (ChatChannel channel in hostChannels.Where(channel =>
            channel.ModeratorId is not int id || !activeIds.Contains(id)))
        {
            if (!channel.IsArchived)
            {
                channel.IsArchived = true;
                changed = true;
            }
        }

        List<ChatChannel> hostToHostChannels = await db.ChatChannels
            .Where(channel => channel.Kind == ChatChannelKind.HostToHost)
            .ToListAsync(ct);
        foreach (ChatChannel channel in hostToHostChannels.Where(channel =>
            channel.ModeratorId is not int first
            || channel.CounterpartModeratorId is not int second
            || !activeIds.Contains(first)
            || !activeIds.Contains(second)))
        {
            if (!channel.IsArchived)
            {
                channel.IsArchived = true;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<ChatChannelDto> BuildChannelDtoAsync(Guid channelId, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel channel = await db.ChatChannels.AsNoTracking()
            .Include(item => item.Moderator)
            .Include(item => item.CounterpartModerator)
            .FirstAsync(item => item.Id == channelId, ct);
        ChatMessage? latest = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ChannelId == channelId)
            .OrderByDescending(message => message.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        DateTime? readAt = channel.AdminLastReadAtUtc;
        int unread = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ChannelId == channelId
                && message.SenderKind != ChatSenderKind.Admin
                && (readAt == null || message.CreatedAtUtc > readAt))
            .CountAsync(ct);
        return ToChannelDto(channel, latest, unread);
    }

    private static ChatChannelDto ToChannelDto(ChatChannel channel, ChatMessage? latest, int unreadCount)
        => new(
            channel.Id,
            channel.Kind.ToString(),
            ChannelName(channel),
            channel.ModeratorId,
            channel.Kind == ChatChannelKind.HostDm ? channel.Moderator?.PhotoUrl : null,
            channel.LastMessageAtUtc,
            latest is null ? null : Preview(latest.Text),
            unreadCount,
            channel.IsArchived);

    private static ChatMessageDto ToMessageDto(ChatMessage message)
        => new(
            message.Id,
            message.ChannelId,
            message.SenderKind.ToString(),
            message.SenderModeratorId,
            SenderName(message),
            message.SenderModerator?.PhotoUrl,
            message.Text,
            ChatActionJson.Deserialize(message.ActionsJson)
                .Select(action => new ChatActionDto(
                    action.Tool,
                    action.Arguments,
                    action.State.ToString(),
                    action.ResultSummary))
                .ToList(),
            message.CreatedAtUtc,
            message.CorrelationId,
            message.HopCount);

    private static string SenderName(ChatMessage message)
        => message.SenderKind switch
        {
            ChatSenderKind.Admin => "Admin",
            ChatSenderKind.Host => message.SenderModerator?.Name ?? "Host",
            ChatSenderKind.Director => "Program Director",
            ChatSenderKind.System => "System",
            _ => message.SenderKind.ToString(),
        };

    private static int KindRank(ChatChannelKind kind)
        => kind switch
        {
            ChatChannelKind.Station => 0,
            ChatChannelKind.DirectorDm => 1,
            ChatChannelKind.HostDm => 2,
            _ => 3,
        };

    private static string ChannelName(ChatChannel channel)
        => channel.Kind == ChatChannelKind.HostToHost
            ? $"{channel.Moderator?.Name ?? "Host"} <-> {channel.CounterpartModerator?.Name ?? "Host"}"
            : channel.Name;

    private static string Preview(string text)
    {
        string oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= PreviewLength ? oneLine : $"{oneLine[..(PreviewLength - 3)]}...";
    }

    private async Task BroadcastMessageAsync(ChatMessageDto message, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ChatMessageAdded", message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to broadcast chat message {MessageId}", message.Id);
        }
    }

    private async Task BroadcastChannelAsync(ChatChannelDto channel, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ChatChannelUpdated", channel, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to broadcast chat channel {ChannelId}", channel.Id);
        }
    }
}
