using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Slugs;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    private static void MapModerators(RouteGroupBuilder api)
    {
        api.MapGet("/moderators", async (RadioDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var moderators = await db.Moderators.AsNoTracking()
                .OrderBy(m => m.Name)
                .ThenBy(m => m.Id)
                .ToListAsync(ct);
            var now = time.GetLocalNow();
            return Results.Ok(moderators.Select(m => ToDto(m, now)).ToList());
        });

        api.MapPost("/moderators", async (CreateModeratorDto request, RadioDbContext db,
            IVoiceDesignClient voiceDesigner, IProductionUpdatePublisher productionUpdates, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            // Hosts always speak the station language (the main language).
            var stationLanguage = StationLanguages.Normalize(
                (await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct)).DefaultLanguage);
            var baselineTraits = ParseBaselineTraits(request.BaselineTraits, request.Style, request.Talkativeness);
            var existingSlugs = await db.Moderators.AsNoTracking()
                .Select(m => m.Slug)
                .ToListAsync(ct);

            var moderator = new Moderator
            {
                Name = request.Name.Trim(),
                Slug = SlugGenerator.UniqueFromName(request.Name, existingSlugs),
                Language = stationLanguage,
                Gender = request.Gender == ModeratorGenders.Male ? ModeratorGenders.Male : ModeratorGenders.Female,
                TtsEngine = TtsEngines.Qwen,
                Style = request.Style,
                PersonaPrompt = request.PersonaPrompt,
                PrefersVocals = request.PrefersVocals,
                PreferredGenres = request.PreferredGenres,
                Talkativeness = Math.Clamp(request.Talkativeness, 0, 1),
                IsWeatherSpecialist = request.IsWeatherSpecialist,
                IsNewsSpecialist = request.IsNewsSpecialist,
                BaselineEnergy = baselineTraits.Energy,
                BaselineFormality = baselineTraits.Formality,
                BaselineHumorLevel = baselineTraits.HumorLevel,
                BaselineTalkativeness = baselineTraits.Talkativeness,
                BaselineWarmth = baselineTraits.Warmth,
                PhotoUrl = string.IsNullOrWhiteSpace(request.PhotoUrl) ? null : request.PhotoUrl.Trim(),
                VoiceDescription = SanitizeOptional(
                    request.VoiceDescription,
                    BuildModeratorVoiceDescription(request.Name, request.Gender, request.Style, request.PersonaPrompt)),
                IsActive = true,
                SpeechRate = 1.0,
            };
            ApplyTalkProfile(moderator, request.TalkProfile);

            try
            {
                var voice = await voiceDesigner.DesignVoiceAsync(
                    moderator.VoiceDescription,
                    moderator.Gender,
                    moderator.Language,
                    BuildVoiceIntroSample(moderator.Name, moderator.Language),
                    ct);
                moderator.VoiceId = voice.Handle;
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(
                    "Voice design booth is unreachable. Check the active Voice Booth on the Studios page.",
                    statusCode: 503);
            }

            db.Moderators.Add(moderator);
            await db.SaveChangesAsync(ct);
            if (moderator.IsNewsSpecialist)
            {
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            if (moderator.IsWeatherSpecialist)
            {
                await productionUpdates.PublishWeatherChangedAsync(ct);
            }

            return Results.Ok(ToDto(moderator, time.GetLocalNow()));
        });

        api.MapPost("/moderators/specialist", async (
            CreateSpecialistHostRequestDto request,
            SpecialistHostCreationService specialistHosts,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<SpecialistHostRole>(request.Role, ignoreCase: true, out var role)
                || role is not (SpecialistHostRole.News or SpecialistHostRole.Weather))
            {
                return Results.BadRequest("Role must be News or Weather.");
            }

            try
            {
                var moderator = await specialistHosts.CreateAsync(role, request.Hint, ct);
                return Results.Ok(ToDto(moderator, time.GetLocalNow()));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(
                    "Host creation timed out or the writer room / voice booth is unreachable.",
                    statusCode: 503);
            }
        });

        api.MapPost("/moderators/{id:int}/toggle", async (int id, RadioDbContext db,
            IProductionUpdatePublisher productionUpdates, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            moderator.IsActive = !moderator.IsActive;
            await db.SaveChangesAsync(ct);
            if (moderator.IsNewsSpecialist)
            {
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            if (moderator.IsWeatherSpecialist)
            {
                await productionUpdates.PublishWeatherChangedAsync(ct);
            }

            return Results.Ok(new { moderator.Id, moderator.IsActive });
        });

        api.MapGet("/moderators/{id:int}/usage", async (int id, RadioDbContext db, CancellationToken ct) =>
        {
            if (!await db.Moderators.AsNoTracking().AnyAsync(m => m.Id == id, ct))
            {
                return Results.NotFound();
            }

            return Results.Ok(await BuildModeratorUsageAsync(db, id, ct));
        });

        api.MapPost("/moderators/{id:int}/fire", async (int id, RadioDbContext db,
            IProductionUpdatePublisher productionUpdates, TimeProvider time, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            var usage = await BuildModeratorUsageAsync(db, id, ct);
            var now = DateTime.UtcNow;
            moderator.IsActive = false;

            var settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is not null)
            {
                if (settings.NewsPresenterModeratorId == id)
                {
                    settings.NewsPresenterModeratorId = null;
                }

                if (settings.WeatherSpecialistModeratorId == id)
                {
                    settings.WeatherSpecialistModeratorId = null;
                }
            }

            await db.Formats
                .Where(format => format.ModeratorId == id)
                .ExecuteUpdateAsync(update => update.SetProperty(format => format.ModeratorId, (int?)null), ct);

            await db.ListenerMessages
                .Where(message => message.ModeratorId == id
                    && (message.Status == ListenerMessageStatus.Pending || message.Status == ListenerMessageStatus.Queued))
                .ExecuteUpdateAsync(update => update.SetProperty(message => message.ModeratorId, (int?)null), ct);

            await db.TalkBits
                .Where(bit => bit.ModeratorId == id && bit.Status == TalkBitStatus.Active)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(bit => bit.Status, TalkBitStatus.Retired)
                    .SetProperty(bit => bit.RetiredAtUtc, now)
                    .SetProperty(bit => bit.RetirementReason, "Host fired"), ct);

            await db.TalkParts
                .Where(part => db.TalkBreaks.Any(talkBreak => talkBreak.Id == part.TalkBreakId
                    && talkBreak.ModeratorId == id
                    && (talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered)))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(part => part.Status, TalkPartStatus.Expired)
                    .SetProperty(part => part.ExpiresAtUtc, now), ct);

            await db.TalkBreaks
                .Where(talkBreak => talkBreak.ModeratorId == id
                    && (talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(talkBreak => talkBreak.Status, TalkBreakStatus.Expired)
                    .SetProperty(talkBreak => talkBreak.ExpiresAtUtc, now), ct);

            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            await productionUpdates.PublishWeatherChangedAsync(ct);

            return Results.Ok(new FireModeratorResultDto(ToDto(moderator, time.GetLocalNow()), usage));
        });

        api.MapPut("/moderators/{id:int}/photo", async (int id, ModeratorPhotoDto request,
            RadioDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            moderator.PhotoUrl = string.IsNullOrWhiteSpace(request.PhotoUrl) ? null : request.PhotoUrl.Trim();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(moderator, time.GetLocalNow()));
        });

        api.MapGet("/moderators/{id:int}/talks", async (int id, RadioDbContext db, CancellationToken ct) =>
        {
            var talks = await db.Announcements.AsNoTracking()
                .Where(a => a.ModeratorId == id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .ToListAsync(ct);

            return Results.Ok(talks.Select(a => new PlayLogEntryDto(
                a.CreatedAt, "Announcement", a.Id, a.Kind.ToString(), null, a.DurationSeconds,
                TranscriptOf(a))).ToList());
        });
    }

    private static void MapVoices(RouteGroupBuilder api)
    {
        // Mint a reproducible voice from a text description (Qwen Voice-Design).
        api.MapPost("/voices/design", async (
            DesignVoiceDto request, IVoiceDesignClient designer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Description))
            {
                return Results.BadRequest("A voice description is required.");
            }

            try
            {
                var voice = await designer.DesignVoiceAsync(
                    request.Description, request.Gender, request.Language,
                    BuildVoiceIntroSample(request.Name, request.Language), ct);
                return Results.Ok(new DesignedVoiceDto(voice.Handle, request.Description, voice.DurationSeconds));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        });

        api.MapGet("/voices/{handle}/preview", async (
            string handle, IVoiceDesignClient designer, CancellationToken ct) =>
        {
            try
            {
                var wav = await designer.GetPreviewAsync(handle, ct);
                return Results.File(wav, "audio/wav");
            }
            catch (HttpRequestException)
            {
                return Results.NotFound();
            }
        });

        // One-click upgrade: mint a Qwen voice from the host's persona. Returns
        // the handle for preview — applying it is a separate, explicit step.
        api.MapPost("/moderators/{id:int}/redesign-voice", async (
            int id, RadioDbContext db, IVoiceDesignClient designer, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            var description = !string.IsNullOrWhiteSpace(moderator.VoiceDescription)
                ? moderator.VoiceDescription
                : $"{moderator.Style} radio host. "
                    + moderator.PersonaPrompt[..Math.Min(160, moderator.PersonaPrompt.Length)];

            try
            {
                var voice = await designer.DesignVoiceAsync(
                    description, moderator.Gender, moderator.Language,
                    BuildVoiceIntroSample(moderator.Name, moderator.Language), ct);
                return Results.Ok(new DesignedVoiceDto(voice.Handle, description, voice.DurationSeconds));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        });

        // Applies a designed voice to a host (reversible: old engine/voice are
        // simply overwritten; redesign again or re-pick a preset to revert).
        api.MapPost("/moderators/{id:int}/apply-voice", async (
            int id, ApplyVoiceDto request, RadioDbContext db, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            moderator.TtsEngine = TtsEngines.Qwen;
            moderator.VoiceId = request.Handle;
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                moderator.VoiceDescription = request.Description;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });
    }

    /// <summary>The preview introduces the host by name — what you hear is what
    /// goes on air, including how the voice pronounces its own name.</summary>
    private static string? BuildVoiceIntroSample(string? name, string language)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : $"Hi, I'm {name.Trim()}! You're listening to WhipRadio — where every song is made just for you. Stay tuned!";

    private static string BuildModeratorVoiceDescription(
        string name,
        string gender,
        string style,
        string persona)
    {
        var genderWord = gender == ModeratorGenders.Male ? "male" : "female";
        var description = $"A {genderWord} English radio host voice. Style: {style}. {persona}";
        return description.Length <= 500 ? description : description[..500];
    }
}
