using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public sealed class ArtistMemberVoicePreparationService(
    IDbContextFactory<RadioDbContext> dbFactory,
    ArtistMemberVoiceQueue queue,
    IVoiceDesignClient voiceDesign,
    IServiceScopeFactory scopeFactory,
    IOptions<RadioOptions> radioOptions,
    ILogger<ArtistMemberVoicePreparationService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueuePendingMembersAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (queue.TryDequeue() is not { } memberId)
            {
                await Task.Delay(IdleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
                continue;
            }

            var processed = await ProcessMemberAsync(memberId, stoppingToken);
            if (!processed && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ErrorDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    /// <summary>Queues every member without a designed voice (the file check happens in ProcessMemberAsync).</summary>
    public async Task EnqueuePendingMembersAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pending = await db.ArtistMembers
                .AsNoTracking()
                .Where(m => m.Artist!.IsRetired == false
                    && (m.VoiceId == null || m.VoiceReferencePath == null))
                .Select(m => m.Id)
                .ToListAsync(ct);

            if (pending.Count > 0)
            {
                queue.EnqueueMany(pending);
                logger.LogInformation("Queued {Count} artist member(s) for voice design.", pending.Count);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not scan artist members for pending voice design.");
        }
    }

    public async Task<bool> ProcessMemberAsync(Guid memberId, CancellationToken ct)
    {
        ArtistMember? member;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            member = await db.ArtistMembers
                .AsNoTracking()
                .Include(m => m.Artist)
                .FirstOrDefaultAsync(m => m.Id == memberId, ct);
        }

        if (member is null)
        {
            logger.LogDebug("Queued artist member {MemberId} no longer exists; skipping voice preparation.", memberId);
            return true;
        }

        if (!NeedsVoice(member))
        {
            return true;
        }

        try
        {
            var gender = InferMemberGender(member);
            var language = string.IsNullOrWhiteSpace(member.Artist?.Language)
                ? "en"
                : member.Artist.Language;
            var sampleText = await BuildSampleTextAsync(member, language, ct);
            var designed = await voiceDesign.DesignVoiceAsync(
                BuildVoiceDescription(member),
                gender,
                language,
                sampleText,
                ct);
            var preview = await voiceDesign.GetPreviewAsync(designed.Handle, ct);
            var relativePath = Path.Combine(
                "acestep",
                "voice-references",
                member.ArtistId.ToString("N"),
                $"{member.Id:N}.wav");
            var absolutePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, preview, ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.ArtistMembers
                .Where(m => m.Id == member.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.TtsEngine, "qwen")
                    .SetProperty(m => m.VoiceId, designed.Handle)
                    .SetProperty(m => m.VoiceReferencePath, relativePath)
                    .SetProperty(m => m.VoiceDesignedAtUtc, DateTime.UtcNow)
                    .SetProperty(m => m.VoiceDesignLastError, (string?)null), ct);

            logger.LogInformation(
                "Prepared hidden voice reference for artist member {Member} ({VoiceId}, {Duration:F1}s)",
                member.Name,
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
            queue.Enqueue(member.Id);
            logger.LogDebug(ex, "Voice design deferred for artist member {Member}; local voice booth is not ready.", member.Name);
            return false;
        }
        catch (Exception ex)
        {
            var error = Truncate(ex.GetBaseException().Message, 500);
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.ArtistMembers
                .Where(m => m.Id == member.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.VoiceDesignLastError, error), CancellationToken.None);

            logger.LogWarning(ex, "Voice design failed for artist member {Member}", member.Name);
            return false;
        }
    }

    private bool NeedsVoice(ArtistMember member)
    {
        if (string.IsNullOrWhiteSpace(member.VoiceId) || string.IsNullOrWhiteSpace(member.VoiceReferencePath))
        {
            return true;
        }

        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, member.VoiceReferencePath);
        return !File.Exists(absolutePath);
    }

    private static string BuildVoiceDescription(ArtistMember member)
    {
        var prompt = string.IsNullOrWhiteSpace(member.VoiceCreationPrompt)
            ? member.Biography
            : member.VoiceCreationPrompt;
        return $"""
            Artist: {member.Artist?.Name ?? "unknown"}
            Member: {member.Name}
            Role: {member.Role}
            Voice design: {prompt}
            """;
    }

    /// <summary>
    /// The audible voice sample. We ask the writer room for a short, natural
    /// first-person self-introduction drawn from the member's biography; if that is
    /// unavailable we fall back to a plain greeting (never a station promo).
    /// </summary>
    private async Task<string> BuildSampleTextAsync(ArtistMember member, string language, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var copywriter = scope.ServiceProvider.GetRequiredService<MusicCopywriter>();
            var intro = await copywriter.WriteMemberSelfIntroAsync(member, language, ct);
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
                "Self-introduction generation failed for artist member {Member}; using fallback sample text.",
                member.Name);
        }

        return FallbackSampleText(member, language);
    }

    private static string FallbackSampleText(ArtistMember member, string language)
        => language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
            ? $"Hi, ich bin {member.Name}, und ich liebe das, was ich tue."
            : $"Hi, I'm {member.Name}, and I love what I do.";

    private static string InferMemberGender(ArtistMember member)
    {
        if (!string.IsNullOrWhiteSpace(member.Gender))
        {
            return member.Gender;
        }

        var text = $"{member.Role} {member.Biography} {member.VoiceCreationPrompt}".ToLowerInvariant();
        if (ContainsAny(text, "female", "woman", "women", "soprano", "mezzo", "alto", "contralto"))
        {
            return "female";
        }

        if (ContainsAny(text, "male", "man", "men", "tenor", "baritone", "basso", "deep bass voice"))
        {
            return "male";
        }

        return "unspecified";
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
}
