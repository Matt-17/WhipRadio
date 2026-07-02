using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed record ChatActionContext(
    ChatChannel Channel,
    ChatMessageDto? AgentMessage,
    Moderator? Sender,
    ChatSenderKind SenderKind,
    CharacterRole SenderRole,
    Guid CorrelationId,
    int HopCount);

public sealed class ChatActionExecutor(
    IDbContextFactory<RadioDbContext> dbFactory,
    ICharacterToolCatalog toolCatalog,
    ChatService chat,
    ChatTurnQueue turnQueue,
    TrackQueryService tracks,
    PriorityTalkBreakDispatcher priorityDispatcher,
    ScheduleService schedule,
    DirectorPlanningService director,
    INotificationBus notifications,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ChatActionExecutor> logger)
{
    public async Task<ChatActionRecord> ExecuteAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        if (toolCatalog.GetTool(call.Name, PromptScope.Chat, context.SenderRole) is null)
        {
            return Failed(call, $"Tool '{call.Name}' is not available to {context.SenderRole}.");
        }

        try
        {
            ChatActionRecord result = call.Name switch
            {
                "Message" => await ExecuteMessageAsync(call, context, ct),
                "Announcement" => await ExecuteAnnouncementAsync(call, context, ct),
                "SearchMusic" => await ExecuteSearchMusicAsync(call, ct),
                "PlanFormat" => await ExecutePlanFormatAsync(call, ct),
                "HireHost" => await ExecuteHireHostAsync(call, ct),
                "AssignHost" => await ExecuteAssignHostAsync(call, ct),
                "StatusReport" => await ExecuteStatusReportAsync(call, ct),
                _ => Failed(call, $"Tool '{call.Name}' has no chat executor."),
            };
            logger.LogInformation(
                "Chat action {Verb} by {Sender} in {Channel}: {Outcome}",
                call.Name,
                context.Sender?.Name ?? context.SenderKind.ToString(),
                context.Channel.Name,
                result.ResultSummary);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Log only: the agentic loop feeds the failure back to the agent,
            // which answers the admin itself. Station-channel notifications are
            // reserved for real production failures, not argument mistakes.
            logger.LogWarning(ex, "Chat action {Verb} failed", call.Name);
            return Failed(call, ex.GetBaseException().Message);
        }
    }

    private async Task<ChatActionRecord> ExecuteMessageAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string target = Require(call, "characterId");
        string message = Require(call, "message");
        if (IsAdminTarget(target))
        {
            Guid channelId = context.SenderKind == ChatSenderKind.Director
                ? await chat.GetDirectorChannelIdAsync(ct)
                : context.Sender is { } adminTargetSender
                    ? await chat.GetHostDmChannelIdAsync(adminTargetSender.Id, ct) ?? context.Channel.Id
                    : context.Channel.Id;
            await chat.PostAsync(channelId, context.SenderKind, context.Sender?.Id, message, null, context.CorrelationId, context.HopCount, ct);
            Guid? plannedBreakId = context.Channel.Kind == ChatChannelKind.HostToHost && context.Sender is not null
                ? await CreatePlannedConversationTalkBreakAsync(context, message, ct)
                : null;
            return Succeeded(
                call,
                plannedBreakId is null
                    ? "Message sent to Admin."
                    : $"Message sent to Admin; planned segment {plannedBreakId:N} was created.");
        }

        if (IsDirectorTarget(target))
        {
            if (context.SenderKind == ChatSenderKind.Director)
            {
                return Failed(
                    call,
                    "You are the Program Director yourself - do not forward requests to yourself. "
                    + "Handle the request directly with your own tools.");
            }

            Guid directorChannelId = await chat.GetDirectorChannelIdAsync(ct);
            ChatMessageDto posted = await chat.PostAsync(
                directorChannelId,
                context.SenderKind,
                context.Sender?.Id,
                message,
                null,
                context.CorrelationId,
                context.HopCount + 1,
                ct);
            await TryEnqueueAsync(directorChannelId, null, posted.Id, context, call);
            return Succeeded(call, "Message sent to Program Director.");
        }

        Moderator targetHost = await ResolveHostAsync(target, ct);
        if (context.Sender is { } sender && sender.Id == targetHost.Id)
        {
            return Failed(call, "You cannot send a chat message to yourself.");
        }

        Guid targetChannelId;
        if (context.Sender is { } senderHost)
        {
            targetChannelId = await chat.GetOrCreateHostToHostChannelAsync(senderHost.Id, targetHost.Id, ct);
        }
        else
        {
            targetChannelId = await chat.GetHostDmChannelIdAsync(targetHost.Id, ct)
                ?? throw new InvalidOperationException("Target host DM was not found.");
        }

        ChatMessageDto hostMessage = await chat.PostAsync(
            targetChannelId,
            context.SenderKind,
            context.Sender?.Id,
            message,
            null,
            context.CorrelationId,
            context.HopCount + 1,
            ct);
        await TryEnqueueAsync(targetChannelId, targetHost.Id, hostMessage.Id, context, call);
        return Succeeded(call, $"Message sent to {targetHost.Name}.");
    }

    private async Task<ChatActionRecord> ExecuteAnnouncementAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        Moderator moderator = context.Sender
            ?? throw new InvalidOperationException("Announcement requires a host sender.");
        string topic = Require(call, "topic");
        TalkBreakPriority priority = ParsePriority(Optional(call, "priority"));

        ShowContext show = await schedule.GetCurrentAsync(ct);
        if (show.Moderator.Id != moderator.Id)
        {
            return Failed(
                call,
                $"You are currently not in the studio - {show.Moderator.Name} is on air right now. "
                + "You can only make announcements during your own show.");
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);

        // Production takes minutes; run it detached so the chat reply is instant.
        // Failures land in the log and the station notification channel, never in
        // the consumer-facing chat.
        ProduceAnnouncementInBackgroundAsync(moderator, topic, priority, settings.StationName).Forget();
        return Succeeded(
            call,
            $"Announcement about '{topic}' is in production ({priority}) and will air in your next talk break.");
    }

    private async Task ProduceAnnouncementInBackgroundAsync(
        Moderator moderator,
        string topic,
        TalkBreakPriority priority,
        string stationName)
    {
        try
        {
            // Fresh scope: the chat turn's scope (and its scoped AnnouncementFactory)
            // is gone long before production finishes.
            using IServiceScope scope = scopeFactory.CreateScope();
            AnnouncementFactory factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            Announcement announcement = await factory.ProduceAsync(
                AnnouncementKind.Banter,
                moderator,
                relatedTrack: null,
                facts: $"The station admin asked for an announcement about: {topic}",
                stationName,
                CancellationToken.None,
                purpose: "chat-requested announcement");

            if (priority is TalkBreakPriority.High or TalkBreakPriority.Emergency)
            {
                await using RadioDbContext db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                TalkBreak? talkBreak = await db.TalkBreaks
                    .Include(item => item.Parts)
                    .FirstOrDefaultAsync(item => item.AnnouncementId == announcement.Id);
                if (talkBreak is not null)
                {
                    talkBreak.Priority = priority;
                    talkBreak.ExpiresAtUtc = priority == TalkBreakPriority.Emergency
                        ? timeProvider.GetUtcNow().UtcDateTime.AddHours(1)
                        : timeProvider.GetUtcNow().UtcDateTime.AddHours(24);
                    foreach (TalkPart part in talkBreak.Parts)
                    {
                        part.Priority = priority;
                        part.ExpiresAtUtc = talkBreak.ExpiresAtUtc;
                    }

                    await db.SaveChangesAsync();
                }

                await priorityDispatcher.PushReadyAsync(CancellationToken.None);
            }

            logger.LogInformation(
                "Chat-requested announcement for {Host} produced ({Priority}, {Duration:0}s)",
                moderator.Name,
                priority,
                announcement.DurationSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-requested announcement for {Host} could not be produced", moderator.Name);
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:Announcement",
                $"Announcement for {moderator.Name} could not be produced: {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    private async Task<ChatActionRecord> ExecuteSearchMusicAsync(CharacterToolCall call, CancellationToken ct)
    {
        string query = Require(call, "query");
        int limit = int.TryParse(Optional(call, "limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 5;
        IReadOnlyList<TrackSearchResult> results = await tracks.SearchAsync(query, limit, ct);
        if (results.Count == 0)
        {
            return Succeeded(call, "No matching tracks found.");
        }

        string summary = string.Join("; ", results.Select(result =>
            $"{result.ArtistName} - {result.Title} ({result.Genre}, {TimeSpan.FromSeconds(result.DurationSeconds):m\\:ss})"));
        return Succeeded(call, $"{results.Count} track(s): {summary}");
    }

    private async Task<ChatActionRecord> ExecutePlanFormatAsync(CharacterToolCall call, CancellationToken ct)
    {
        DayOfWeek day = ParseDay(Require(call, "day"));
        int startMinute = ParseClock(Require(call, "startTime"));
        int duration = int.TryParse(Require(call, "durationMinutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException("durationMinutes must be a number.");
        int? hostId = null;
        string? hostArg = Optional(call, "host");
        if (!string.IsNullOrWhiteSpace(hostArg))
        {
            hostId = (await director.ResolveHostAsync(hostArg, ct)).Id;
        }

        SlotPlanResult result = await director.PlanSlotAsync(
            day,
            startMinute,
            duration,
            Require(call, "genre"),
            Optional(call, "name"),
            Optional(call, "description"),
            hostId,
            "planned by director chat",
            ct);
        return Succeeded(call, result.Summary);
    }

    private async Task<ChatActionRecord> ExecuteHireHostAsync(CharacterToolCall call, CancellationToken ct)
    {
        Moderator moderator = await director.HireHostAsync(Require(call, "brief"), ct);
        return Succeeded(call, $"Hired {moderator.Name}; voice is ready.");
    }

    private async Task<ChatActionRecord> ExecuteAssignHostAsync(CharacterToolCall call, CancellationToken ct)
    {
        Format format = await director.ResolveFormatAsync(Require(call, "format"), ct);
        Moderator moderator = await director.ResolveHostAsync(Require(call, "host"), ct);
        await director.AssignHostAsync(format.Id, moderator.Id, ct);
        return Succeeded(call, $"Assigned {moderator.Name} to {format.Name}.");
    }

    private async Task<ChatActionRecord> ExecuteStatusReportAsync(CharacterToolCall call, CancellationToken ct)
        => Succeeded(call, await director.BuildStatusReportAsync(ct));

    private async Task TryEnqueueAsync(
        Guid channelId,
        int? responderModeratorId,
        Guid triggerMessageId,
        ChatActionContext context,
        CharacterToolCall call)
    {
        try
        {
            await using RadioDbContext db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(CancellationToken.None);
            if (context.HopCount + 1 > settings.ChatMaxAgentHops)
            {
                await chat.PostAsync(
                    channelId,
                    ChatSenderKind.System,
                    null,
                    $"Agent exchange stopped at hop cap ({settings.ChatMaxAgentHops}).",
                    null,
                    context.CorrelationId,
                    context.HopCount + 1,
                    CancellationToken.None);
                return;
            }

            turnQueue.TryEnqueue(new ChatTurnRequest(
                channelId,
                responderModeratorId,
                triggerMessageId,
                context.CorrelationId,
                context.HopCount + 1));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue chat response for tool {Tool}", call.Name);
        }
    }

    private async Task<Moderator> ResolveHostAsync(string value, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            Moderator? byId = await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(host => host.Id == id && host.IsActive, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await db.Moderators.AsNoTracking()
            .Where(host => host.IsActive)
            .OrderBy(host => host.Name)
            .FirstOrDefaultAsync(host => host.Name.ToLower() == value.Trim().ToLower(), ct)
            ?? throw new InvalidOperationException($"Active host '{value}' was not found.");
    }

    private async Task<Guid> CreatePlannedConversationTalkBreakAsync(
        ChatActionContext context,
        string report,
        CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel channel = await db.ChatChannels.AsNoTracking()
            .Include(item => item.Moderator)
            .Include(item => item.CounterpartModerator)
            .FirstAsync(item => item.Id == context.Channel.Id, ct);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime expiresAt = now.AddDays(7);
        string left = channel.Moderator?.Name ?? "Host";
        string right = channel.CounterpartModerator?.Name ?? "Host";
        string topic = NormalizeTopic(report);
        string purpose = $"planned two-host segment: {topic}";

        TalkBreak talkBreak = new()
        {
            Id = Guid.NewGuid(),
            ModeratorId = context.Sender!.Id,
            Priority = TalkBreakPriority.Scheduled,
            Status = TalkBreakStatus.Pending,
            Purpose = purpose,
            Title = $"Planned two-host segment: {left} + {right}",
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            Parts =
            [
                new TalkPart
                {
                    SortOrder = 0,
                    Kind = TalkPartKind.Banter,
                    Status = TalkPartStatus.Pending,
                    Priority = TalkBreakPriority.Scheduled,
                    Purpose = purpose,
                    DesiredDurationSeconds = 180,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = expiresAt,
                },
            ],
        };
        db.TalkBreaks.Add(talkBreak);
        await db.SaveChangesAsync(ct);
        return talkBreak.Id;
    }

    private static ChatActionRecord Succeeded(CharacterToolCall call, string summary)
        => new(call.Name, call.Arguments, ChatActionState.Succeeded, summary, DateTime.UtcNow);

    private static ChatActionRecord Failed(CharacterToolCall call, string summary)
        => new(call.Name, call.Arguments, ChatActionState.Failed, summary, DateTime.UtcNow);

    private static string Require(CharacterToolCall call, string name)
        => call.Arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"Tool '{call.Name}' is missing required argument '{name}'.");

    private static string? Optional(CharacterToolCall call, string name)
        => call.Arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string NormalizeTopic(string value)
    {
        string oneLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(oneLine))
        {
            return "topic agreed in host chat";
        }

        return oneLine.Length <= 180 ? oneLine : $"{oneLine[..177]}...";
    }

    private static bool IsAdminTarget(string value)
        => value.Equals("Admin", StringComparison.OrdinalIgnoreCase)
            || value.Equals("User", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectorTarget(string value)
        => value.Equals("Director", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Program Director", StringComparison.OrdinalIgnoreCase);

    private static TalkBreakPriority ParsePriority(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "high" => TalkBreakPriority.High,
            "emergency" => TalkBreakPriority.Emergency,
            _ => TalkBreakPriority.Normal,
        };

    // The agents mirror the admin's language (per D4), so day arguments arrive
    // in German just as often as English.
    private DayOfWeek ParseDay(string value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out DayOfWeek day))
        {
            return day;
        }

        string trimmed = value.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "mon" or "montag" or "mo" => DayOfWeek.Monday,
            "tue" or "tues" or "dienstag" or "di" => DayOfWeek.Tuesday,
            "wed" or "mittwoch" or "mi" => DayOfWeek.Wednesday,
            "thu" or "thur" or "thurs" or "donnerstag" or "do" => DayOfWeek.Thursday,
            "fri" or "freitag" or "fr" => DayOfWeek.Friday,
            "sat" or "samstag" or "sonnabend" or "sa" => DayOfWeek.Saturday,
            "sun" or "sonntag" or "so" => DayOfWeek.Sunday,
            "today" or "heute" => timeProvider.GetLocalNow().DayOfWeek,
            "tomorrow" or "morgen" => timeProvider.GetLocalNow().AddDays(1).DayOfWeek,
            _ => throw new InvalidOperationException(
                $"Day '{value}' is not valid. Use an English or German day name, 'today', or 'tomorrow'."),
        };
    }

    private static int ParseClock(string value)
    {
        string[] parts = value.Trim().Split(':', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute))
        {
            throw new InvalidOperationException("startTime must be HH:mm.");
        }

        return Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59);
    }
}
