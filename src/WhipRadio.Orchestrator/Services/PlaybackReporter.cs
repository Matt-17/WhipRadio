using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Callback target for the PlayoutService: play log, counters, now-playing,
/// SignalR push to the web app, and Icecast stream metadata (Winamp/VLC titles).</summary>
public interface IPlaybackReporter
{
    Task ReportStartedAsync(PlayoutItem item, CancellationToken ct);

    void ReportIdle();
}

public class PlaybackReporter(
    IDbContextFactory<RadioDbContext> dbFactory,
    INowPlayingState nowPlaying,
    QueueStateTracker queueTracker,
    PlayoutStateStore stateStore,
    IHubContext<RadioHub> hub,
    IHttpClientFactory httpClientFactory,
    ScheduleService schedule,
    IOptions<IcecastOptions> icecastOptions,
    IOptions<StreamOptions> streamOptions,
    ILogger<PlaybackReporter> logger) : IPlaybackReporter
{
    private readonly SemaphoreSlim _reportGate = new(1, 1);
    private int _epoch;

    /// <summary>
    /// The encoder feed runs DisplayLatencySeconds ahead of what listeners hear
    /// (pipes, Icecast burst, browser buffer). The visible flip — now-playing,
    /// queue, play log, metadata — is therefore scheduled, not immediate, so the
    /// UI matches the ears. ReportIdle bumps the epoch so a pending flip from a
    /// just-aborted item can't resurrect after the station goes off air.
    /// </summary>
    public Task ReportStartedAsync(PlayoutItem item, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(0, streamOptions.Value.DisplayLatencySeconds));
        var epoch = Volatile.Read(ref _epoch);
        DelayedReportAsync(item, delay, epoch, ct).Forget();
        return Task.CompletedTask;
    }

    private async Task DelayedReportAsync(PlayoutItem item, TimeSpan delay, int epoch, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            await _reportGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _epoch) != epoch)
                {
                    return; // station went idle/off-air in the meantime
                }

                await ReportNowAsync(item, CancellationToken.None);
            }
            finally
            {
                _reportGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // session ended before the flip became visible — nothing to report
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delayed now-playing report failed for \"{Title}\"", item.Title);
        }
    }

    private async Task ReportNowAsync(PlayoutItem item, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var visibleStartedAtUtc = nowUtc - TimeSpan.FromSeconds(
            Math.Clamp(item.StartOffsetSeconds, 0, Math.Max(0, item.DurationSeconds)));
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // A resumed item already aired before a restart. Don't log/count the same
        // airing twice (the cause of phantom "song played twice" rows in the play
        // log). The DB check keeps it correct if the restart happened before the
        // original airing was logged — then this resume is the first record.
        var alreadyLogged = item.IsResumed && await WasRecentlyLoggedAsync(db, item, nowUtc, ct);

        if (!alreadyLogged)
        {
            db.PlayLog.Add(new PlayLogEntry
            {
                PlayedAt = nowUtc,
                ItemType = item.ItemType,
                ItemId = item.ItemId,
                ModeratorId = item.ModeratorId,
                DurationSeconds = item.DurationSeconds,
                WasFallback = item.Origin == PlayoutItemOrigin.Fallback,
            });
        }

        string? artistName = null;
        string? transcript = null;
        string? lyrics = null;
        string? announcementKind = null;
        var title = item.Title;
        var upVotes = 0;
        var downVotes = 0;

        if (item.ItemType == PlayoutItemType.Track)
        {
            if (!alreadyLogged)
            {
                await db.Tracks
                    .Where(t => t.Id == item.ItemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.PlayCount, t => t.PlayCount + 1), ct);
            }

            var track = await db.Tracks.AsNoTracking()
                .Include(t => t.Artist)
                .FirstOrDefaultAsync(t => t.Id == item.ItemId, ct);
            artistName = track?.Artist?.Name;
            upVotes = track?.UpVotes ?? 0;
            downVotes = track?.DownVotes ?? 0;
            lyrics = track?.Lyrics;
        }
        else if (item.ItemType == PlayoutItemType.Jingle)
        {
            if (!alreadyLogged)
            {
                await db.Jingles
                    .Where(j => j.Id == item.ItemId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.PlayCount, j => j.PlayCount + 1)
                        .SetProperty(j => j.LastUsedAtUtc, nowUtc), ct);
            }
        }
        else
        {
            await db.Announcements
                .Where(a => a.Id == item.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.WasPlayed, true), ct);
            await db.TalkBreaks
                .Where(t => t.AnnouncementId == item.ItemId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TalkBreakStatus.Played)
                    .SetProperty(t => t.PlayedAtUtc, nowUtc), ct);
            await db.TalkParts
                .Where(p => p.AnnouncementId == item.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, TalkPartStatus.Played), ct);

            var announcement = await db.Announcements.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == item.ItemId, ct);
            var voicedText = announcement?.VoicedText;
            // Transcripts show the bare spoken text — never the speech markup.
            transcript = voicedText is null ? null : Core.Speech.SpeechMarkerNormalizer.ToPlainText(voicedText);
            announcementKind = announcement?.Kind.ToString();
            title = RadioDisplayNames.AnnouncementTitle(announcementKind);
        }

        await db.SaveChangesAsync(ct);

        string? moderatorName = null;
        if (item.ModeratorId is int moderatorId)
        {
            moderatorName = (await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == moderatorId, ct))?.Name;
        }

        string? formatName = null;
        try
        {
            formatName = (await schedule.GetCurrentAsync(ct)).Format?.Name;
        }
        catch
        {
            // format is decoration — never block the show for it
        }

        nowPlaying.SetCurrent(new NowPlayingInfo(
            item.ItemType, item.ItemId, item.Title, visibleStartedAtUtc, item.DurationSeconds, moderatorName));
        queueTracker.Started(item.ItemId);
        stateStore.BecameVisible(item);

        var dto = new NowPlayingDto(
            item.ItemType.ToString(), item.ItemId, title, visibleStartedAtUtc, item.DurationSeconds,
            moderatorName, artistName, transcript, upVotes, downVotes, formatName, lyrics, announcementKind);

        await PublishAsync(dto, ct);
        await PushIcecastMetadataAsync(item, artistName, moderatorName, ct);

        logger.LogInformation("On air: {Type} \"{Title}\"", item.ItemType, item.Title);
    }

    /// <summary>
    /// True when this exact item already has a play-log row from the airing that was
    /// interrupted by the restart. The window spans the item's own duration (plus a
    /// small grace) measured from now — a resume only happens while the item is still
    /// mid-air, so its original row is always within that span.
    /// </summary>
    private static async Task<bool> WasRecentlyLoggedAsync(
        RadioDbContext db, PlayoutItem item, DateTime nowUtc, CancellationToken ct)
    {
        var windowStart = nowUtc - TimeSpan.FromSeconds(Math.Max(0, item.DurationSeconds) + 30);
        return await db.PlayLog.AsNoTracking()
            .AnyAsync(e => e.ItemId == item.ItemId
                && e.ItemType == item.ItemType
                && e.PlayedAt >= windowStart, ct);
    }

    public void ReportIdle()
    {
        Interlocked.Increment(ref _epoch); // cancel pending delayed flips
        nowPlaying.SetCurrent(null);
        hub.Clients.All.SendAsync("NowPlayingChanged", (NowPlayingDto?)null).Forget();
    }

    private async Task PublishAsync(NowPlayingDto dto, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("NowPlayingChanged", dto, ct);
            var queue = queueTracker.Snapshot()
                .Select(q => new QueueItemDto(q.ItemType.ToString(), q.ItemId, q.Title, q.DurationSeconds))
                .ToList();
            await hub.Clients.All.SendAsync("QueueChanged", queue, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR publish failed (no clients?)");
        }
    }

    /// <summary>Updates the Icecast mount metadata so players like Winamp/VLC show the title.</summary>
    private async Task PushIcecastMetadataAsync(
        PlayoutItem item, string? artistName, string? moderatorName, CancellationToken ct)
    {
        try
        {
            var icecast = icecastOptions.Value;
            var song = item.ItemType switch
            {
                PlayoutItemType.Track => $"{artistName ?? "WhipRadio"} - {item.Title}",
                PlayoutItemType.Jingle => item.Title,
                _ => $"{moderatorName ?? "WhipRadio"} (talk)",
            };

            var client = httpClientFactory.CreateClient("icecast-admin");
            var url = $"http://{icecast.Host}:{icecast.Port}/admin/metadata" +
                      $"?mount={Uri.EscapeDataString(streamOptions.Value.Mount)}" +
                      $"&mode=updinfo&song={Uri.EscapeDataString(song)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{icecast.AdminUser}:{icecast.AdminPassword}")));

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Icecast metadata update returned {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Icecast metadata update failed");
        }
    }
}
