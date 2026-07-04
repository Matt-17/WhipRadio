using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    private const int MaxConversationParticipants = 5;

    private static void MapConversations(RouteGroupBuilder api)
    {
        api.MapGet("/conversations", async (RadioDbContext db, CancellationToken ct) =>
        {
            var segments = await db.ConversationSegments.AsNoTracking()
                .OrderByDescending(segment => segment.CreatedAtUtc)
                .Take(30)
                .ToListAsync(ct);
            var showNames = await db.PodcastShows.AsNoTracking()
                .ToDictionaryAsync(show => show.Id, show => show.Name, ct);
            return Results.Ok(segments
                .Select(segment => ToDto(segment, showNames, includeTranscript: false))
                .ToList());
        });

        api.MapGet("/conversations/{id:guid}", async (Guid id, RadioDbContext db, CancellationToken ct) =>
        {
            var segment = await db.ConversationSegments.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (segment is null)
            {
                return Results.NotFound();
            }

            var showNames = await db.PodcastShows.AsNoTracking()
                .ToDictionaryAsync(show => show.Id, show => show.Name, ct);
            return Results.Ok(ToDto(segment, showNames, includeTranscript: true));
        });

        api.MapPost("/conversations", async (
            CreateConversationRequestDto request,
            RadioDbContext db,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            if (!TryNormalizeParticipants(request.Participants, out var participants, out var error))
            {
                return Results.BadRequest(error);
            }

            if (string.IsNullOrWhiteSpace(request.Topic))
            {
                return Results.BadRequest("A topic is required.");
            }

            var segment = new ConversationSegment
            {
                Id = Guid.NewGuid(),
                Kind = Enum.TryParse<ConversationKind>(request.Kind, ignoreCase: true, out var kind)
                    ? kind
                    : ConversationKind.Talk,
                Structure = Enum.TryParse<ConversationStructure>(request.Structure, ignoreCase: true, out var structure)
                    ? structure
                    : ConversationStructure.Freeform,
                Topic = request.Topic.Trim(),
                Brief = (request.Brief ?? string.Empty).Trim(),
                TargetDurationMinutes = Math.Clamp(request.TargetDurationMinutes, 5, 30),
                ParticipantsJson = JsonSerializer.Serialize(participants),
                ChaptersJson = JsonSerializer.Serialize((request.Chapters ?? [])
                    .Where(chapter => !string.IsNullOrWhiteSpace(chapter.Title))
                    .Select(chapter => new ConversationChapter
                    {
                        Title = chapter.Title.Trim(),
                        Intent = chapter.Intent.Trim(),
                        TargetMinutes = Math.Clamp(chapter.TargetMinutes, 1, 15),
                    })
                    .ToList()),
                Status = ConversationStatus.Planned,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.ConversationSegments.Add(segment);
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.Ok(ToDto(segment, new Dictionary<Guid, string>(), includeTranscript: false));
        });

        api.MapPost("/conversations/{id:guid}/air-next", async (
            Guid id,
            RadioDbContext db,
            IPlayoutQueue playoutQueue,
            IProductionUpdatePublisher productionUpdates,
            ILogger<ConversationDispatcher> logger,
            CancellationToken ct) =>
        {
            var segment = await db.ConversationSegments.FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (segment is null)
            {
                return Results.NotFound();
            }

            if (segment.Status != ConversationStatus.Produced || segment.TargetUtc is not null)
            {
                return Results.BadRequest("Only produced one-off conversations can be aired manually.");
            }

            var announcement = await db.Announcements.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == segment.AnnouncementId, ct);
            if (announcement is null)
            {
                return Results.BadRequest("The produced audio is missing.");
            }

            // Referenced tracks first (reversed), then the episode on top of the
            // queue-front stack, so playback runs episode -> track A -> track B.
            await ConversationDispatcher.EnqueueReferencedTracksAsync(
                db, playoutQueue, segment, logger, ct);
            playoutQueue.EnqueueFront(new PlayoutItem(
                PlayoutItemType.Announcement,
                announcement.Id,
                announcement.FilePath,
                $"{segment.Kind}: {segment.Title ?? segment.Topic}",
                announcement.DurationSeconds,
                announcement.ModeratorId));

            segment.Status = ConversationStatus.Used;
            segment.UsedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.NoContent();
        });

        api.MapPost("/conversations/{id:guid}/retry", async (
            Guid id,
            RadioDbContext db,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            var segment = await db.ConversationSegments.FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (segment is null)
            {
                return Results.NotFound();
            }

            if (segment.Status != ConversationStatus.Failed)
            {
                return Results.BadRequest("Only failed conversations can be retried.");
            }

            // A scheduled episode whose slot already passed can never air again.
            if (segment.TargetUtc is { } target && target < DateTime.UtcNow)
            {
                return Results.BadRequest("The episode's slot has already passed.");
            }

            segment.Status = segment.TurnsJson is null ? ConversationStatus.Planned : ConversationStatus.Scripted;
            segment.FailureReason = null;
            segment.ProductionState = null;
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.NoContent();
        });

        api.MapDelete("/conversations/{id:guid}", async (
            Guid id,
            RadioDbContext db,
            IOptions<RadioOptions> radio,
            TimedPlayoutInterruptService interrupts,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            var segment = await db.ConversationSegments.FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (segment is null)
            {
                return Results.NotFound();
            }

            if (segment.StepIndex > 0
                && segment.Status is ConversationStatus.Planned or ConversationStatus.Scripted)
            {
                return Results.Conflict("The conversation is being produced right now; try again shortly.");
            }

            if (segment.AnnouncementId is { } announcementId)
            {
                interrupts.Clear(announcementId);
                await db.Announcements.Where(a => a.Id == announcementId).ExecuteDeleteAsync(ct);
            }

            if (segment.OutputFilePath is { } relativePath)
            {
                var absolutePath = Path.Combine(radio.Value.DataRoot, relativePath);
                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                }
            }

            db.ConversationSegments.Remove(segment);
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/conversations/speakers", async (RadioDbContext db, CancellationToken ct) =>
        {
            var hosts = await db.Moderators.AsNoTracking()
                .Where(moderator => moderator.IsActive)
                .OrderBy(moderator => moderator.Name)
                .Select(moderator => new ConversationSpeakerOptionDto(
                    ConversationParticipant.HostKeyPrefix + moderator.Id,
                    moderator.Name,
                    "host — " + moderator.Style,
                    moderator.VoiceId != ""))
                .ToListAsync(ct);
            var members = await db.ArtistMembers.AsNoTracking()
                .Include(member => member.Artist)
                .Where(member => member.Artist != null && !member.Artist.IsRetired)
                .OrderBy(member => member.Name)
                .Select(member => new ConversationSpeakerOptionDto(
                    ConversationParticipant.MemberKeyPrefix + member.Id.ToString(),
                    member.Name,
                    (member.Artist!.Name + " — " + member.Role),
                    member.VoiceId != null && member.VoiceId != ""))
                .ToListAsync(ct);
            var guests = await db.Guests.AsNoTracking()
                .Where(guest => !guest.IsArchived)
                .OrderBy(guest => guest.Name)
                .Select(guest => new ConversationSpeakerOptionDto(
                    ConversationParticipant.GuestKeyPrefix + guest.Id.ToString(),
                    guest.Name,
                    "guest — " + guest.Expertise,
                    guest.VoiceId != null && guest.VoiceId != ""))
                .ToListAsync(ct);
            return Results.Ok(hosts.Concat(members).Concat(guests).ToList());
        });

        api.MapGet("/podcast-shows", async (RadioDbContext db, CancellationToken ct) =>
        {
            var shows = await db.PodcastShows.AsNoTracking()
                .OrderBy(show => show.Name)
                .ToListAsync(ct);
            return Results.Ok(shows.Select(ToDto).ToList());
        });

        api.MapPost("/podcast-shows", async (
            SavePodcastShowDto request,
            RadioDbContext db,
            PodcastShowScheduleSeeder seeder,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            if (!TryNormalizeShow(request, out var error, out var normalized))
            {
                return Results.BadRequest(error);
            }

            var show = new PodcastShow
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = DateTime.UtcNow,
            };
            Apply(show, normalized);
            db.PodcastShows.Add(show);
            await db.SaveChangesAsync(ct);
            await seeder.SyncAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.Ok(ToDto(show));
        });

        api.MapPut("/podcast-shows/{id:guid}", async (
            Guid id,
            SavePodcastShowDto request,
            RadioDbContext db,
            PodcastShowScheduleSeeder seeder,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            if (!TryNormalizeShow(request, out var error, out var normalized))
            {
                return Results.BadRequest(error);
            }

            var show = await db.PodcastShows.FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (show is null)
            {
                return Results.NotFound();
            }

            Apply(show, normalized);
            await db.SaveChangesAsync(ct);
            await seeder.SyncAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.Ok(ToDto(show));
        });

        api.MapPost("/podcast-shows/{id:guid}/toggle", async (
            Guid id,
            RadioDbContext db,
            PodcastShowScheduleSeeder seeder,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            var show = await db.PodcastShows.FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (show is null)
            {
                return Results.NotFound();
            }

            show.IsEnabled = !show.IsEnabled;
            await db.SaveChangesAsync(ct);
            await seeder.SyncAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.Ok(ToDto(show));
        });

        api.MapDelete("/podcast-shows/{id:guid}", async (
            Guid id,
            RadioDbContext db,
            PodcastShowScheduleSeeder seeder,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            var show = await db.PodcastShows.FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (show is null)
            {
                return Results.NotFound();
            }

            db.PodcastShows.Remove(show);
            await db.SaveChangesAsync(ct);
            await seeder.SyncAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return Results.NoContent();
        });

        static void Apply(PodcastShow show, SavePodcastShowDto request)
        {
            show.Name = request.Name.Trim();
            show.Brief = (request.Brief ?? string.Empty).Trim();
            show.EpisodeMinutes = PodcastShowScheduler.NormalizeEpisodeMinutes(request.EpisodeMinutes);
            show.DayOfWeek = ((request.DayOfWeek % 7) + 7) % 7;
            show.StartMinute = Math.Clamp(request.StartMinute, 0, (24 * 60) - PodcastShowScheduler.MinSlotMinutes);
            show.SlotDurationMinutes = PodcastShowScheduler.NormalizeSlotMinutes(
                request.SlotDurationMinutes, request.EpisodeMinutes);
            show.ParticipantsJson = JsonSerializer.Serialize(request.Participants
                .Select(participant => new ConversationParticipant
                {
                    SpeakerKey = participant.SpeakerKey,
                    DisplayName = participant.DisplayName.Trim(),
                    ConversationRole = participant.ConversationRole,
                })
                .ToList());
            show.IsEnabled = request.IsEnabled;
        }
    }

    private static bool TryNormalizeShow(
        SavePodcastShowDto request, out string? error, out SavePodcastShowDto normalized)
    {
        normalized = request;
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "A show name is required.";
            return false;
        }

        if (!TryNormalizeParticipants(request.Participants, out _, out error))
        {
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryNormalizeParticipants(
        IReadOnlyList<ConversationParticipantDto> participants,
        out List<ConversationParticipant> normalized,
        out string? error)
    {
        normalized = [];
        if (participants.Count is < 2 or > MaxConversationParticipants)
        {
            error = $"A conversation needs 2-{MaxConversationParticipants} participants.";
            return false;
        }

        foreach (var participant in participants)
        {
            var entry = new ConversationParticipant
            {
                SpeakerKey = participant.SpeakerKey,
                DisplayName = participant.DisplayName.Trim(),
                ConversationRole = string.IsNullOrWhiteSpace(participant.ConversationRole)
                    ? "Guest"
                    : participant.ConversationRole.Trim(),
            };
            if (!entry.TryGetModeratorId(out _) && !entry.TryGetArtistMemberId(out _) && !entry.TryGetGuestId(out _))
            {
                error = $"Speaker key \"{participant.SpeakerKey}\" is not valid.";
                return false;
            }

            if (entry.DisplayName.Length == 0)
            {
                error = "Every participant needs a display name.";
                return false;
            }

            normalized.Add(entry);
        }

        if (normalized.Select(participant => participant.SpeakerKey).Distinct().Count() != normalized.Count)
        {
            error = "Each speaker can only appear once.";
            return false;
        }

        error = null;
        return true;
    }

    private static ConversationSegmentDto ToDto(
        ConversationSegment segment, IReadOnlyDictionary<Guid, string> showNames, bool includeTranscript)
        => new(
            segment.Id,
            segment.Kind.ToString(),
            segment.Structure.ToString(),
            segment.Topic,
            segment.Title,
            segment.Status.ToString(),
            segment.TargetDurationMinutes,
            segment.DurationSeconds,
            segment.ProductionState,
            segment.StepIndex,
            segment.StepTotal,
            segment.FailureReason,
            segment.AnnouncementId,
            segment.PodcastShowId,
            segment.PodcastShowId is { } showId ? showNames.GetValueOrDefault(showId) : null,
            segment.TargetUtc,
            segment.CreatedAtUtc,
            segment.ProducedAtUtc,
            segment.UsedAtUtc,
            ParseParticipants(segment.ParticipantsJson),
            includeTranscript ? segment.Transcript : null,
            segment.DegradationReason);

    private static PodcastShowDto ToDto(PodcastShow show)
        => new(
            show.Id,
            show.Name,
            show.Brief,
            show.EpisodeMinutes,
            show.DayOfWeek,
            show.StartMinute,
            show.SlotDurationMinutes,
            show.IsEnabled,
            ParseParticipants(show.ParticipantsJson),
            show.CreatedAtUtc);

    private static IReadOnlyList<ConversationParticipantDto> ParseParticipants(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<ConversationParticipant>>(json) ?? [])
                .Select(participant => new ConversationParticipantDto(
                    participant.SpeakerKey, participant.DisplayName, participant.ConversationRole))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
