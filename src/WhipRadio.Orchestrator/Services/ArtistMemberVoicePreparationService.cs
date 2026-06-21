using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public sealed class ArtistMemberVoicePreparationService(
    IDbContextFactory<RadioDbContext> dbFactory,
    ArtistMemberVoiceQueue queue,
    IVoiceDesignClient voiceDesign,
    IOptions<RadioOptions> radioOptions,
    ILogger<ArtistMemberVoicePreparationService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            var designed = await voiceDesign.DesignVoiceAsync(
                BuildVoiceDescription(member),
                gender,
                language,
                BuildSampleText(member, language),
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

    private static string BuildSampleText(ArtistMember member, string language)
        => language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
            ? $"Ich bin {member.Name}, und diese Stimme gehoert zu unserer naechsten WhipRadio-Session."
            : $"I am {member.Name}, and this is the voice that belongs to our next WhipRadio session.";

    private static string InferMemberGender(ArtistMember member)
    {
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
