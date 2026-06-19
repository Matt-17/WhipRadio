using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class ModeratorMemoryService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IPromptContextBuilder promptContextBuilder,
    ITextGenerationService llm,
    ILogger<ModeratorMemoryService> logger)
{
    public const int DayMemoryMaxChars = 2_000;
    public const int LongTermMemoryMaxChars = 3_000;

    public async Task RememberAsync(
        int moderatorId,
        ModeratorMemoryLayer layer,
        DateOnly date,
        string content,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ModeratorMemories.Add(new ModeratorMemory
        {
            ModeratorId = moderatorId,
            Layer = layer,
            Date = date,
            Content = Trim(content, MaxChars(layer)),
            CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        await TrimLayerAsync(db, moderatorId, layer, date, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> DistillDayAsync(DateOnly day, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moderatorIds = await db.ModeratorMemories.AsNoTracking()
            .Where(memory => memory.Layer == ModeratorMemoryLayer.DayMemory && memory.Date == day)
            .Select(memory => memory.ModeratorId)
            .Distinct()
            .ToListAsync(ct);

        var count = 0;
        foreach (var moderatorId in moderatorIds)
        {
            if (await DistillModeratorDayAsync(moderatorId, day, ct))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<bool> DistillModeratorDayAsync(int moderatorId, DateOnly day, CancellationToken ct)
    {
        Moderator? moderator;
        List<string> dayMemories;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var alreadyDistilled = await db.ModeratorMemories.AsNoTracking()
                .AnyAsync(memory => memory.ModeratorId == moderatorId
                    && memory.Layer == ModeratorMemoryLayer.LongTermMemory
                    && memory.Date == day,
                    ct);
            if (alreadyDistilled)
            {
                return false;
            }

            moderator = await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(host => host.Id == moderatorId, ct);
            dayMemories = await db.ModeratorMemories.AsNoTracking()
                .Where(memory => memory.ModeratorId == moderatorId
                    && memory.Layer == ModeratorMemoryLayer.DayMemory
                    && memory.Date == day)
                .OrderBy(memory => memory.CreatedAt)
                .Select(memory => memory.Content)
                .ToListAsync(ct);
        }

        if (moderator is null || dayMemories.Count == 0)
        {
            return false;
        }

        var summary = await SummarizeAsync(moderator, day, dayMemories, ct);
        await RememberAsync(moderatorId, ModeratorMemoryLayer.LongTermMemory, day, summary, ct);
        logger.LogInformation("Distilled {Count} day memories for {Moderator} on {Date}",
            dayMemories.Count, moderator.Name, day);
        return true;
    }

    private async Task<string> SummarizeAsync(
        Moderator moderator,
        DateOnly day,
        IReadOnlyList<string> memories,
        CancellationToken ct)
    {
        var fallback = Trim(string.Join(" | ", memories), LongTermMemoryMaxChars);
        try
        {
            var context = await promptContextBuilder.BuildAsync(
                new PromptContextInput(
                    PromptScope.Utility,
                    Moderator: moderator,
                    Facts: $"Distill day memory for {day:yyyy-MM-dd}",
                    Purpose: "Distill host day memory"),
                ct);

            var summary = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(
                "Condense the host's day memory into durable continuity notes. " +
                "Keep only facts, recurring jokes, promises, callbacks, and useful relationship context. " +
                "Output 1-3 concise sentences, no bullet list.\n\n" + context.RenderSituation(),
                string.Join("\n", memories),
                "Distilling host memory",
                ct));

            return string.IsNullOrWhiteSpace(summary)
                ? fallback
                : Trim(summary, LongTermMemoryMaxChars);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory distillation failed for {Moderator}; using deterministic fallback",
                moderator.Name);
            return fallback;
        }
    }

    private static int MaxChars(ModeratorMemoryLayer layer)
        => layer == ModeratorMemoryLayer.LongTermMemory ? LongTermMemoryMaxChars : DayMemoryMaxChars;

    private static async Task TrimLayerAsync(
        RadioDbContext db,
        int moderatorId,
        ModeratorMemoryLayer layer,
        DateOnly date,
        CancellationToken ct)
    {
        var maxChars = layer switch
        {
            ModeratorMemoryLayer.DayMemory => DayMemoryMaxChars,
            ModeratorMemoryLayer.LongTermMemory => LongTermMemoryMaxChars,
            _ => 0,
        };
        if (maxChars <= 0)
        {
            return;
        }

        var query = db.ModeratorMemories
            .Where(memory => memory.ModeratorId == moderatorId && memory.Layer == layer);
        if (layer == ModeratorMemoryLayer.DayMemory)
        {
            query = query.Where(memory => memory.Date == date);
        }

        var rows = await query
            .OrderBy(memory => memory.CreatedAt)
            .ThenBy(memory => memory.Id)
            .ToListAsync(ct);
        var totalChars = rows.Sum(memory => memory.Content.Length);

        foreach (var row in rows)
        {
            if (totalChars <= maxChars)
            {
                break;
            }

            totalChars -= row.Content.Length;
            db.ModeratorMemories.Remove(row);
        }
    }

    private static string Trim(string value, int maxChars)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }
}
