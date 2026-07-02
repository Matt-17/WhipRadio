using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Persists every agentic loop event (round replies, executed actions with
/// results, errors) and broadcasts it live so the Agent Log page shows the
/// back-and-forth the consumer chat hides. Writing must never throw — a
/// broken log line may not break an agent turn.
/// </summary>
public sealed class AgentActionLogService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IHubContext<RadioHub> hub,
    TimeProvider timeProvider,
    ILogger<AgentActionLogService> logger)
{
    private const int MaxContentLength = 4000;

    public async Task LogAsync(
        string agentName,
        int? moderatorId,
        string source,
        Guid? correlationId,
        int round,
        AgentLogEventKind kind,
        string? tool,
        string content,
        string? outcome,
        CancellationToken ct)
    {
        try
        {
            AgentActionLog entry = new()
            {
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                AgentName = agentName,
                ModeratorId = moderatorId,
                Source = source,
                CorrelationId = correlationId,
                Round = round,
                Kind = kind,
                Tool = tool,
                Content = Truncate(content),
                Outcome = outcome,
            };

            await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
            db.AgentActionLogs.Add(entry);
            await db.SaveChangesAsync(ct);

            await hub.Clients.All.SendAsync("AgentActionLogged", ToDto(entry), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to write agent action log entry for {Agent}", agentName);
        }
    }

    public async Task<IReadOnlyList<AgentLogEntryDto>> GetAsync(
        string? agent,
        int take,
        CancellationToken ct)
    {
        int pageSize = Math.Clamp(take <= 0 ? 200 : take, 1, 500);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<AgentActionLog> query = db.AgentActionLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(agent))
        {
            string normalized = agent.Trim().ToLower();
            query = query.Where(entry => entry.AgentName.ToLower() == normalized);
        }

        List<AgentActionLog> entries = await query
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .Take(pageSize)
            .ToListAsync(ct);
        return entries.Select(ToDto).ToList();
    }

    private static AgentLogEntryDto ToDto(AgentActionLog entry)
        => new(
            entry.Id,
            entry.CreatedAtUtc,
            entry.AgentName,
            entry.ModeratorId,
            entry.Source,
            entry.CorrelationId,
            entry.Round,
            entry.Kind.ToString(),
            entry.Tool,
            entry.Content,
            entry.Outcome);

    private static string Truncate(string value)
        => value.Length <= MaxContentLength ? value : $"{value[..(MaxContentLength - 3)]}...";
}
