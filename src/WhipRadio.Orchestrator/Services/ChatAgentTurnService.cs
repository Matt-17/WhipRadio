using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public sealed class ChatAgentTurnService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IPromptContextBuilder promptContextBuilder,
    ITextGenerationService llm,
    IChatReplyParser parser,
    ChatService chat,
    ChatActionExecutor actionExecutor,
    AgentActionLogService agentLog,
    IHubContext<RadioHub> hub,
    ILogger<ChatAgentTurnService> logger)
{
    private const int MaxRetries = 2;
    private const int MaxAgentRounds = 4;

    public async Task RunTurnAsync(ChatTurnRequest request, CancellationToken ct)
    {
        ChatChannel channel;
        ChatMessage trigger;
        Moderator? responder;
        await using (RadioDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            channel = await db.ChatChannels.AsNoTracking()
                .Include(item => item.Moderator)
                .Include(item => item.CounterpartModerator)
                .FirstOrDefaultAsync(item => item.Id == request.ChannelId, ct)
                ?? throw new KeyNotFoundException($"Chat channel {request.ChannelId} was not found.");
            trigger = await db.ChatMessages.AsNoTracking()
                .Include(item => item.SenderModerator)
                .FirstOrDefaultAsync(item => item.Id == request.TriggerMessageId, ct)
                ?? throw new KeyNotFoundException($"Trigger message {request.TriggerMessageId} was not found.");
            responder = request.ResponderModeratorId is int moderatorId
                ? await db.Moderators.AsNoTracking().FirstOrDefaultAsync(host => host.Id == moderatorId && host.IsActive, ct)
                : null;
        }

        string senderName = responder?.Name ?? "Program Director";
        await PublishThinkingAsync(channel.Id, senderName, isThinking: true, ct);
        try
        {
            CharacterRole role = ResolveRole(responder);
            PromptContext context = await promptContextBuilder.BuildAsync(
                new PromptContextInput(
                    PromptScope.Chat,
                    Moderator: responder,
                    Purpose: "chat conversation",
                    ChatChannelId: channel.Id,
                    ChatCounterpartName: ResolveCounterpartName(channel, trigger, responder),
                    ChatAudience: trigger.SenderKind),
                ct);

            ChatSenderKind senderKind = responder is null ? ChatSenderKind.Director : ChatSenderKind.Host;
            (ChatReply reply, List<ChatActionRecord> records) = await RunAgenticLoopAsync(
                context,
                trigger.Text,
                channel,
                responder,
                senderKind,
                role,
                request,
                senderName,
                ct);

            if (reply.Errors.Count > 0)
            {
                logger.LogWarning(
                    "Chat turn for {Sender} finished with {Count} output error(s): {Errors}",
                    senderName,
                    reply.Errors.Count,
                    string.Join("; ", reply.Errors));
                await agentLog.LogAsync(
                    senderName,
                    responder?.Id,
                    "chat",
                    request.CorrelationId,
                    round: 0,
                    AgentLogEventKind.Error,
                    tool: null,
                    string.Join("; ", reply.Errors),
                    outcome: null,
                    ct);
            }

            await chat.PostAsync(
                channel.Id,
                senderKind,
                responder?.Id,
                LlmOutputSanitizer.Sanitize(reply.Prose),
                records.Count == 0 ? null : ChatActionJson.Serialize(records),
                request.CorrelationId,
                request.HopCount,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Chat turn failed for {Sender} in channel {ChannelId}", senderName, channel.Id);
            await agentLog.LogAsync(
                senderName,
                responder?.Id,
                "chat",
                request.CorrelationId,
                round: 0,
                AgentLogEventKind.Error,
                tool: null,
                $"Turn failed: {ex.GetBaseException().Message}",
                outcome: null,
                CancellationToken.None);
            await chat.PostAsync(
                channel.Id,
                ChatSenderKind.System,
                moderatorId: null,
                $"{senderName} could not answer because the writer room is unavailable.",
                actionsJson: null,
                request.CorrelationId,
                request.HopCount,
                ct);
        }
        finally
        {
            await PublishThinkingAsync(channel.Id, senderName, isThinking: false, CancellationToken.None);
        }
    }

    /// <summary>
    /// Agentic turn loop: the model replies, its actions execute immediately, and
    /// every result — successes, lookup data, and failures alike — is fed back so
    /// the agent can react (retry differently or admit it is not possible) until
    /// it is done or the round cap is reached. Only the final reply is posted to
    /// chat; the back-and-forth lives in the logs.
    /// </summary>
    private async Task<(ChatReply Reply, List<ChatActionRecord> Records)> RunAgenticLoopAsync(
        PromptContext context,
        string triggerText,
        ChatChannel channel,
        Moderator? responder,
        ChatSenderKind senderKind,
        CharacterRole role,
        ChatTurnRequest request,
        string senderName,
        CancellationToken ct)
    {
        List<string> feedback = [];
        ChatReply reply = new(string.Empty, [], []);
        List<ChatActionRecord> records = [];

        for (int round = 1; round <= MaxAgentRounds; round++)
        {
            bool finalRound = round == MaxAgentRounds;
            reply = await CompleteWithRetryAsync(context, role, BuildUserPrompt(triggerText, feedback, finalRound), ct);
            logger.LogInformation(
                "Chat agent {Sender} round {Round}/{Max}: \"{Prose}\" with {Count} action(s) [{Actions}]",
                senderName,
                round,
                MaxAgentRounds,
                reply.Prose,
                reply.Actions.Count,
                string.Join(", ", reply.Actions.Select(action => action.Name)));
            await agentLog.LogAsync(
                senderName,
                responder?.Id,
                "chat",
                request.CorrelationId,
                round,
                AgentLogEventKind.Reply,
                tool: null,
                string.IsNullOrWhiteSpace(reply.Prose) ? "(no prose)" : reply.Prose,
                outcome: null,
                ct);

            if (reply.Actions.Count == 0)
            {
                return (reply, []);
            }

            records = await ExecuteActionsAsync(reply.Actions, channel, responder, senderKind, role, request, senderName, round, feedback, ct);

            bool needsFeedbackRound = records.Any(record => record.State == ChatActionState.Failed)
                || reply.Actions.Any(ChatActionPolicy.IsInTurnLookup);
            if (!needsFeedbackRound || finalRound)
            {
                return (reply, records);
            }
        }

        return (reply, records);
    }

    private async Task<List<ChatActionRecord>> ExecuteActionsAsync(
        IReadOnlyList<CharacterToolCall> actions,
        ChatChannel channel,
        Moderator? responder,
        ChatSenderKind senderKind,
        CharacterRole role,
        ChatTurnRequest request,
        string senderName,
        int round,
        List<string> feedback,
        CancellationToken ct)
    {
        ChatActionContext actionContext = new(
            channel,
            AgentMessage: null,
            responder,
            senderKind,
            role,
            request.CorrelationId,
            request.HopCount);

        List<ChatActionRecord> records = [];
        bool hasTerminalAdminReport = actions.Any(ChatActionPolicy.IsTerminalAdminReport);
        foreach (CharacterToolCall action in actions)
        {
            ChatActionRecord record;
            if (hasTerminalAdminReport
                && !ChatActionPolicy.IsTerminalAdminReport(action)
                && ChatActionPolicy.WouldEnqueueAgentTurn(action))
            {
                record = new ChatActionRecord(
                    action.Name,
                    action.Arguments,
                    ChatActionState.Dismissed,
                    "Skipped because a terminal Admin report ended the exchange.",
                    DateTime.UtcNow);
            }
            else
            {
                record = await actionExecutor.ExecuteAsync(action, actionContext, ct);
            }

            records.Add(record);
            feedback.Add($"{record.Tool} -> {record.State}: {record.ResultSummary ?? "no result"}");
            logger.LogInformation(
                "Chat agent {Sender} action {Verb} -> {State}: {Summary}",
                senderName,
                record.Tool,
                record.State,
                record.ResultSummary);
            string arguments = action.Arguments.Count == 0
                ? "(no arguments)"
                : string.Join(", ", action.Arguments.Select(pair => $"{pair.Key}={pair.Value}"));
            await agentLog.LogAsync(
                senderName,
                responder?.Id,
                "chat",
                request.CorrelationId,
                round,
                AgentLogEventKind.Action,
                record.Tool,
                $"{arguments} => {record.ResultSummary ?? "no result"}",
                record.State.ToString(),
                ct);
        }

        return records;
    }

    private async Task<ChatReply> CompleteWithRetryAsync(
        PromptContext context,
        CharacterRole role,
        string triggerText,
        CancellationToken ct)
    {
        string systemPrompt = BuildSystemPrompt(context, role);
        string userPrompt = triggerText;
        ChatReply? lastReply = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            string raw = await llm.CompleteAsync(
                new TextGenerationRequest(
                    systemPrompt,
                    userPrompt,
                    "chat agent turn",
                    ChatReplySchema.Build(context.Tools),
                    "chatReply"),
                ct);
            ChatReply reply = parser.Parse(raw, context.Tools);
            lastReply = reply;
            if (reply.Errors.Count == 0)
            {
                return reply;
            }

            logger.LogWarning("Chat reply parse attempt {Attempt} had {Count} error(s): {Errors}",
                attempt + 1,
                reply.Errors.Count,
                string.Join("; ", reply.Errors));
            userPrompt = triggerText
                + "\n\nSystem correction: Your previous output had errors: "
                + string.Join("; ", reply.Errors)
                + ". Respond again with exactly the required JSON shape and only available tools.";
        }

        return lastReply ?? new ChatReply(string.Empty, [], ["The model did not return a usable reply."]);
    }

    private static string BuildUserPrompt(string triggerText, IReadOnlyList<string> feedback, bool finalRound)
    {
        if (feedback.Count == 0)
        {
            return triggerText;
        }

        string instruction = finalRound
            ? "This is your final reply: do not call any more tools. Answer your chat partner now, "
              + "honestly reflecting the results above. If something failed, say so plainly."
            : "Continue: if the results settle the request, reply to your chat partner now without repeating tools. "
              + "If a tool failed, either try a corrected approach or tell your chat partner honestly that it is not possible. "
              + "Never claim something worked when its result says otherwise.";

        return triggerText
            + "\n\nSystem tool results for this chat turn so far:\n- "
            + string.Join("\n- ", feedback)
            + "\n\n"
            + instruction;
    }

    private static string BuildSystemPrompt(PromptContext context, CharacterRole role)
    {
        string roleGuidance = role == CharacterRole.ProgramDirector
            ? "You are the Program Director with full authority over programming. When someone asks for a "
              + "schedule or programming change, apply it with your tools (PlanFormat, AssignHost, HireHost) - "
              + "agreeing in words alone changes nothing. Use StatusReport first when you need the current schedule. "
            : "You are a radio host. You cannot change the schedule or programming; that is the Program Director's "
              + "call. If asked for such a change, say so and forward the request with Message to the Program "
              + "Director instead of promising it yourself. ";

        return "You are a WhipRadio character chatting off-air in the station messenger. Stay in character and be "
            + "concise like a colleague on a phone chat. Reply in the language of the last user message. "
            + "On-air content produced by actions must follow the station language. "
            + roleGuidance
            + "Use English tool names exactly as listed. Never invent tools. "
            + "Tool results are reported back to you before your reply reaches your chat partner: never claim "
            + "success before you see the result, and if a tool reports a failure, admit it honestly. "
            + "Return only the JSON envelope.\n\n"
            + context.RenderSituation();
    }

    private static CharacterRole ResolveRole(Moderator? moderator)
    {
        if (moderator is null)
        {
            return CharacterRole.ProgramDirector;
        }

        if (moderator.IsNewsSpecialist)
        {
            return CharacterRole.NewsSpecialist;
        }

        if (moderator.IsWeatherSpecialist)
        {
            return CharacterRole.WeatherSpecialist;
        }

        return CharacterRole.Host;
    }

    private static string ResolveCounterpartName(ChatChannel channel, ChatMessage trigger, Moderator? responder)
    {
        if (trigger.SenderKind == ChatSenderKind.Host && trigger.SenderModerator is { } sender)
        {
            return sender.Name;
        }

        if (trigger.SenderKind == ChatSenderKind.Director)
        {
            return "Program Director";
        }

        if (channel.Kind == ChatChannelKind.HostToHost && responder is not null)
        {
            if (channel.ModeratorId == responder.Id)
            {
                return channel.CounterpartModerator?.Name ?? "another host";
            }

            if (channel.CounterpartModeratorId == responder.Id)
            {
                return channel.Moderator?.Name ?? "another host";
            }
        }

        return "Admin";
    }

    private async Task PublishThinkingAsync(Guid channelId, string senderName, bool isThinking, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ChatAgentThinking", new ChatAgentThinkingDto(channelId, senderName, isThinking), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Failed to publish chat thinking state");
        }
    }
}
