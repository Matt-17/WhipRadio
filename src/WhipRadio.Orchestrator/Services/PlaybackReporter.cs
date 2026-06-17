using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
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
        _ = DelayedReportAsync(item, delay, epoch);
        return Task.CompletedTask;
    }

    private async Task DelayedReportAsync(PlayoutItem item, TimeSpan delay, int epoch)
    {
        try
        {
            await Task.Delay(delay);
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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delayed now-playing report failed for \"{Title}\"", item.Title);
        }
    }

    private async Task ReportNowAsync(PlayoutItem item, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        db.PlayLog.Add(new PlayLogEntry
        {
            PlayedAt = DateTime.UtcNow,
            ItemType = item.ItemType,
            ItemId = item.ItemId,
            ModeratorId = item.ModeratorId,
            DurationSeconds = item.DurationSeconds,
        });

        string? artistName = null;
        string? transcript = null;
        var upVotes = 0;
        var downVotes = 0;

        if (item.ItemType == PlayoutItemType.Track)
        {
            await db.Tracks
                .Where(t => t.Id == item.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PlayCount, t => t.PlayCount + 1), ct);

            var track = await db.Tracks.AsNoTracking()
                .Include(t => t.Artist)
                .FirstOrDefaultAsync(t => t.Id == item.ItemId, ct);
            artistName = track?.Artist?.Name;
            upVotes = track?.UpVotes ?? 0;
            downVotes = track?.DownVotes ?? 0;
        }
        else
        {
            var playedAt = DateTime.UtcNow;
            await db.Announcements
                .Where(a => a.Id == item.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.WasPlayed, true), ct);
            await db.TalkBreaks
                .Where(t => t.AnnouncementId == item.ItemId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TalkBreakStatus.Played)
                    .SetProperty(t => t.PlayedAtUtc, playedAt), ct);
            await db.TalkParts
                .Where(p => p.AnnouncementId == item.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, TalkPartStatus.Played), ct);

            var voicedText = (await db.Announcements.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == item.ItemId, ct))?.VoicedText;
            // Transcripts show the bare spoken text — never the speech markup.
            transcript = voicedText is null ? null : Core.Speech.SpeechMarkerNormalizer.ToPlainText(voicedText);
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
            item.ItemType, item.ItemId, item.Title, DateTime.UtcNow, item.DurationSeconds, moderatorName));
        queueTracker.Started(item.ItemId);

        var dto = new NowPlayingDto(
            item.ItemType.ToString(), item.ItemId, item.Title, DateTime.UtcNow, item.DurationSeconds,
            moderatorName, artistName, transcript, upVotes, downVotes, formatName);

        await PublishAsync(dto, ct);
        await PushIcecastMetadataAsync(item, artistName, moderatorName, ct);

        logger.LogInformation("On air: {Type} \"{Title}\"", item.ItemType, item.Title);
    }

    public void ReportIdle()
    {
        Interlocked.Increment(ref _epoch); // cancel pending delayed flips
        nowPlaying.SetCurrent(null);
        _ = hub.Clients.All.SendAsync("NowPlayingChanged", (NowPlayingDto?)null);
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
            var song = item.ItemType == PlayoutItemType.Track
                ? $"{artistName ?? "WhipRadio"} - {item.Title}"
                : $"{moderatorName ?? "WhipRadio"} (talk)";

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
