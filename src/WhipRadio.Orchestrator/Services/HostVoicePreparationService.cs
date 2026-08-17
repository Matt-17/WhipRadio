using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Every host speaks with a designed Qwen voice. This background worker mints
/// that <c>qv-…</c> voice from the host's persona/description, so host creation
/// never blocks on the voice booth. On startup it also migrates any legacy host
/// still left on a preset voice (kokoro/piper) over to a designed Qwen voice.
/// </summary>
public sealed class HostVoicePreparationService(
    IDbContextFactory<RadioDbContext> dbFactory,
    HostVoiceQueue queue,
    IVoiceDesignClient voiceDesign,
    ILogger<HostVoicePreparationService> logger) : BackgroundService
{
    public const string DesignedVoicePrefix = "qv-";

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueuePendingHostsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (queue.TryDequeue() is not { } moderatorId)
            {
                await stoppingToken.DelayNoThrow(IdleDelay);
                continue;
            }

            var processed = await ProcessHostAsync(moderatorId, stoppingToken);
            if (!processed && !stoppingToken.IsCancellationRequested)
            {
                await stoppingToken.DelayNoThrow(ErrorDelay);
            }
        }
    }

    /// <summary>Queues every host that does not yet have a designed Qwen voice.</summary>
    private async Task EnqueuePendingHostsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pending = await db.Moderators
                .AsNoTracking()
                .Where(m => m.TtsEngine != TtsEngines.Qwen
                    || m.VoiceId == null
                    || !m.VoiceId.StartsWith(DesignedVoicePrefix))
                .Select(m => m.Id)
                .ToListAsync(ct);

            if (pending.Count > 0)
            {
                queue.EnqueueMany(pending);
                logger.LogInformation("Queued {Count} host(s) for Qwen voice design.", pending.Count);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not scan hosts for pending voice design.");
        }
    }

    public async Task<bool> ProcessHostAsync(int moderatorId, CancellationToken ct)
    {
        Moderator? moderator;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            moderator = await db.Moderators.AsNoTracking().FirstOrDefaultAsync(m => m.Id == moderatorId, ct);
        }

        if (moderator is null)
        {
            logger.LogDebug("Queued host {Id} no longer exists; skipping voice design.", moderatorId);
            return true;
        }

        if (!NeedsVoice(moderator))
        {
            return true;
        }

        try
        {
            var description = BuildVoiceDescription(moderator);
            var gender = moderator.Gender == ModeratorGenders.Male ? "male" : "female";
            var designed = await voiceDesign.DesignVoiceAsync(
                description,
                gender,
                moderator.Language,
                BuildVoiceIntroSample(moderator.Name),
                ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Moderators
                .Where(m => m.Id == moderator.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.TtsEngine, TtsEngines.Qwen)
                    .SetProperty(m => m.VoiceId, designed.Handle)
                    .SetProperty(m => m.VoiceDescription, description), ct);

            logger.LogInformation(
                "Designed Qwen voice {VoiceId} for host {Name} ({Duration:F1}s preview).",
                designed.Handle, moderator.Name, designed.DurationSeconds);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (VoiceDesignUnavailableException ex)
        {
            queue.Enqueue(moderator.Id);
            logger.LogDebug(ex, "Voice design deferred for host {Name}; voice booth is not ready.", moderator.Name);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Voice design failed for host {Name}; will retry.", moderator.Name);
            queue.Enqueue(moderator.Id);
            return false;
        }
    }

    private static bool NeedsVoice(Moderator moderator)
        => moderator.TtsEngine != TtsEngines.Qwen
            || string.IsNullOrWhiteSpace(moderator.VoiceId)
            || !moderator.VoiceId.StartsWith(DesignedVoicePrefix, StringComparison.Ordinal);

    private static string BuildVoiceDescription(Moderator moderator)
    {
        if (!string.IsNullOrWhiteSpace(moderator.VoiceDescription))
        {
            return moderator.VoiceDescription.Length <= 500
                ? moderator.VoiceDescription
                : moderator.VoiceDescription[..500];
        }

        var genderWord = moderator.Gender == ModeratorGenders.Male ? "male" : "female";
        var persona = moderator.PersonaPrompt ?? string.Empty;
        var description = $"A {genderWord} radio host voice. Style: {moderator.Style}. {persona}".Trim();
        return description.Length <= 500 ? description : description[..500];
    }

    private static string BuildVoiceIntroSample(string name)
        => string.IsNullOrWhiteSpace(name)
            ? "Hi, you're listening to WhipRadio — where every song is made just for you. Stay tuned!"
            : $"Hi, I'm {name.Trim()}! You're listening to WhipRadio — where every song is made just for you. Stay tuned!";
}
