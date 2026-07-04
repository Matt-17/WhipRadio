using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Write path of the thin retrieval memory (Phase 5): talk takeaways after an
/// episode is produced, plain fact snippets when artists/guests are created,
/// and mirrored host day summaries. Everything here is failure-soft — memory
/// is a quality boost, never a production dependency.
/// </summary>
public sealed class ParticipantMemoryWriter(
    IDbContextFactory<RadioDbContext> dbFactory,
    IEmbeddingService embeddings,
    IServiceScopeFactory scopeFactory,
    IOptions<LlmOptions> llmOptions,
    ILogger<ParticipantMemoryWriter> logger)
{
    /// <summary>Newest memories kept per participant; older rows are pruned on write.</summary>
    public const int MaxMemoriesPerParticipant = 300;

    private const int MaxContentChars = 800;
    private const int MaxTranscriptChars = 8000;

    /// <summary>
    /// One LLM call distills a per-speaker takeaway from a produced episode;
    /// each takeaway is embedded and stored under that speaker's key.
    /// </summary>
    public async Task StoreTalkSummariesAsync(Guid segmentId, CancellationToken ct)
    {
        try
        {
            ConversationSegment? segment;
            StationSettings settings;
            await using (RadioDbContext db = await dbFactory.CreateDbContextAsync(ct))
            {
                segment = await db.ConversationSegments.AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == segmentId, ct);
                settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            }

            if (segment?.Transcript is not { Length: > 0 } transcript)
            {
                return;
            }

            List<ConversationParticipant> participants =
                System.Text.Json.JsonSerializer.Deserialize<List<ConversationParticipant>>(segment.ParticipantsJson) ?? [];
            if (participants.Count == 0)
            {
                return;
            }

            var roster = new StringBuilder();
            foreach (ConversationParticipant participant in participants)
            {
                roster.AppendLine($"- {participant.DisplayName} ({participant.ConversationRole})");
            }

            string prompt = PromptTemplates.Render("ConversationMemorySummarizer", new Dictionary<string, string>
            {
                ["StationName"] = string.IsNullOrWhiteSpace(settings.StationName) ? "WhipRadio" : settings.StationName,
                ["Title"] = segment.Title ?? segment.Topic,
                ["Topic"] = segment.Topic,
                ["SpeakerRoster"] = roster.ToString().TrimEnd(),
                ["Transcript"] = transcript.Length <= MaxTranscriptChars
                    ? transcript
                    : transcript[..MaxTranscriptChars],
            });

            using IServiceScope scope = scopeFactory.CreateScope();
            ITextGenerationService llm = scope.ServiceProvider.GetRequiredService<ITextGenerationService>();
            string raw = await llm.CompleteAsync(
                new TextGenerationRequest(
                    "You distill radio conversations into per-speaker memories. Return only valid JSON.",
                    prompt,
                    "Distilling conversation memory",
                    StructuredJson.SchemaFor<ConversationMemoryJson>(),
                    "conversationMemory"),
                ct);
            StructuredJsonResult<ConversationMemoryJson> parsed = StructuredJson.Parse<ConversationMemoryJson>(raw);
            if (!parsed.IsValid)
            {
                logger.LogDebug("Conversation memory summary was not valid JSON: {Error}", parsed.Error);
                return;
            }

            Dictionary<string, string> keysByName = participants.ToDictionary(
                participant => participant.DisplayName.Trim(),
                participant => participant.SpeakerKey,
                StringComparer.OrdinalIgnoreCase);
            foreach (ParticipantTakeawayJson takeaway in parsed.Value!.Takeaways)
            {
                if (string.IsNullOrWhiteSpace(takeaway.Takeaway)
                    || !keysByName.TryGetValue((takeaway.Speaker ?? string.Empty).Trim(), out string? key))
                {
                    continue;
                }

                await StoreAsync(
                    key,
                    ParticipantMemoryKind.TalkSummary,
                    $"From \"{segment.Title ?? segment.Topic}\": {takeaway.Takeaway.Trim()}",
                    $"conversation:{segment.Id}",
                    ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Talk memory distillation failed for segment {SegmentId}; skipping.", segmentId);
        }
    }

    /// <summary>Stores plain fact snippets (no LLM call) — artist/guest creation.</summary>
    public async Task StoreFactsAsync(
        string participantKey,
        IEnumerable<string> facts,
        string? sourceRef,
        CancellationToken ct)
    {
        try
        {
            foreach (string fact in facts.Where(fact => !string.IsNullOrWhiteSpace(fact)))
            {
                await StoreAsync(participantKey, ParticipantMemoryKind.ArtistFact, fact.Trim(), sourceRef, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fact memory write failed for {Key}; skipping.", participantKey);
        }
    }

    /// <summary>Mirrors a host's distilled day summary into retrievable memory.</summary>
    public async Task StoreHostSummaryAsync(int moderatorId, string content, CancellationToken ct)
    {
        try
        {
            await StoreAsync(
                ConversationParticipant.HostKey(moderatorId),
                ParticipantMemoryKind.ChatSummary,
                content,
                sourceRef: "nightly-distillation",
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Host summary memory write failed for moderator {ModeratorId}; skipping.", moderatorId);
        }
    }

    private async Task StoreAsync(
        string participantKey,
        ParticipantMemoryKind kind,
        string content,
        string? sourceRef,
        CancellationToken ct)
    {
        string trimmed = content.Length <= MaxContentChars ? content : content[..MaxContentChars].TrimEnd();
        float[] embedding = await embeddings.EmbedAsync(trimmed, ct);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.ParticipantMemories.Add(new ParticipantMemory
        {
            ParticipantKey = participantKey,
            Kind = kind,
            Content = trimmed,
            Embedding = embedding,
            EmbeddingModel = llmOptions.Value.EmbeddingModel,
            SourceRef = sourceRef,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        // Prune to the newest N so per-participant scans stay tiny.
        List<Guid> stale = await db.ParticipantMemories.AsNoTracking()
            .Where(memory => memory.ParticipantKey == participantKey)
            .OrderByDescending(memory => memory.CreatedAtUtc)
            .Skip(MaxMemoriesPerParticipant)
            .Select(memory => memory.Id)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            await db.ParticipantMemories.Where(memory => stale.Contains(memory.Id)).ExecuteDeleteAsync(ct);
        }
    }

    internal sealed record ConversationMemoryJson(
        [property: JsonRequired] IReadOnlyList<ParticipantTakeawayJson> Takeaways);

    internal sealed record ParticipantTakeawayJson(
        [property: JsonRequired] string Speaker,
        [property: JsonRequired] string Takeaway);
}
