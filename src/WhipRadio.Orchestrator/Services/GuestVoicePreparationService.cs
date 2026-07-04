using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Designs guest speaking voices in the background (mirror of
/// <see cref="ArtistMemberVoicePreparationService"/>), including a startup
/// backfill scan for guests without a designed voice.
/// </summary>
public sealed class GuestVoicePreparationService(
    IDbContextFactory<RadioDbContext> dbFactory,
    GuestVoiceQueue queue,
    IVoiceDesignClient voiceDesign,
    IServiceScopeFactory scopeFactory,
    IOptions<RadioOptions> radioOptions,
    ILogger<GuestVoicePreparationService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueuePendingGuestsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (queue.TryDequeue() is not { } guestId)
            {
                await Task.Delay(IdleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
                continue;
            }

            var processed = await ProcessGuestAsync(guestId, stoppingToken);
            if (!processed && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ErrorDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    /// <summary>Queues every guest without a designed voice (the file check happens in ProcessGuestAsync).</summary>
    public async Task EnqueuePendingGuestsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pending = await db.Guests
                .AsNoTracking()
                .Where(g => !g.IsArchived && (g.VoiceId == null || g.VoiceReferencePath == null))
                .Select(g => g.Id)
                .ToListAsync(ct);

            if (pending.Count > 0)
            {
                queue.EnqueueMany(pending);
                logger.LogInformation("Queued {Count} guest(s) for voice design.", pending.Count);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not scan guests for pending voice design.");
        }
    }

    public async Task<bool> ProcessGuestAsync(Guid guestId, CancellationToken ct)
    {
        Guest? guest;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            guest = await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guestId, ct);
        }

        if (guest is null)
        {
            logger.LogDebug("Queued guest {GuestId} no longer exists; skipping voice preparation.", guestId);
            return true;
        }

        if (!NeedsVoice(guest))
        {
            return true;
        }

        try
        {
            var language = await GetStationLanguageAsync(ct);
            var sampleText = await BuildSampleTextAsync(guest, language, ct);
            var designed = await voiceDesign.DesignVoiceAsync(
                BuildVoiceDescription(guest),
                string.IsNullOrWhiteSpace(guest.Gender) ? "unspecified" : guest.Gender,
                language,
                sampleText,
                ct);
            var preview = await voiceDesign.GetPreviewAsync(designed.Handle, ct);
            var relativePath = Path.Combine("acestep", "voice-references", "guests", $"{guest.Id:N}.wav");
            var absolutePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, preview, ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Guests
                .Where(g => g.Id == guest.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.TtsEngine, "qwen")
                    .SetProperty(g => g.VoiceId, designed.Handle)
                    .SetProperty(g => g.VoiceReferencePath, relativePath)
                    .SetProperty(g => g.VoiceDesignedAtUtc, DateTime.UtcNow)
                    .SetProperty(g => g.VoiceDesignLastError, (string?)null), ct);

            logger.LogInformation(
                "Prepared voice for guest {Guest} ({VoiceId}, {Duration:F1}s)",
                guest.Name,
                designed.Handle,
                designed.DurationSeconds);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (VoiceDesignUnavailableException ex)
        {
            queue.Enqueue(guest.Id);
            logger.LogDebug(ex, "Voice design deferred for guest {Guest}; local voice booth is not ready.", guest.Name);
            return false;
        }
        catch (Exception ex)
        {
            var error = Truncate(ex.GetBaseException().Message, 500);
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.Guests
                .Where(g => g.Id == guest.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.VoiceDesignLastError, error), CancellationToken.None);

            logger.LogWarning(ex, "Voice design failed for guest {Guest}", guest.Name);
            return false;
        }
    }

    private bool NeedsVoice(Guest guest)
    {
        if (string.IsNullOrWhiteSpace(guest.VoiceId) || string.IsNullOrWhiteSpace(guest.VoiceReferencePath))
        {
            return true;
        }

        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, guest.VoiceReferencePath);
        return !File.Exists(absolutePath);
    }

    private async Task<string> GetStationLanguageAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        return StationLanguages.Normalize(settings.DefaultLanguage);
    }

    private static string BuildVoiceDescription(Guest guest)
    {
        var prompt = string.IsNullOrWhiteSpace(guest.VoiceCreationPrompt)
            ? guest.Biography
            : guest.VoiceCreationPrompt;
        return $"""
            Guest: {guest.Name}
            Known for: {guest.Expertise}
            Voice design: {prompt}
            """;
    }

    private async Task<string> BuildSampleTextAsync(Guest guest, string language, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<GuestProfileWriter>();
            var intro = await writer.WriteSelfIntroAsync(guest, language, ct);
            if (!string.IsNullOrWhiteSpace(intro))
            {
                return intro!;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Self-introduction generation failed for guest {Guest}; using fallback sample text.",
                guest.Name);
        }

        return FallbackSampleText(guest, language);
    }

    private static string FallbackSampleText(Guest guest, string language)
        => language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
            ? $"Hallo, ich bin {guest.Name}, schön, hier zu sein."
            : $"Hi, I'm {guest.Name}, glad to be here.";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
}
