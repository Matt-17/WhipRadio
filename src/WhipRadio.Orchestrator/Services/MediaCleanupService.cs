using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Api;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public sealed class MediaCleanupService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<RadioOptions> radioOptions,
    ILogger<MediaCleanupService> logger,
    IHubContext<RadioHub>? hub = null)
{
    private readonly object _sync = new();
    private MediaCleanupStatusDto _status = IdleStatus;
    private Task? _currentRun;
    private bool _starting;

    private static MediaCleanupStatusDto IdleStatus { get; } = new(
        "Idle",
        null,
        null,
        null,
        null,
        null);

    public MediaCleanupStatusDto CurrentStatus
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    private string? _previewToken;
    private DateTime _previewTokenExpiresUtc;

    public async Task<MediaCleanupPlanDto> PlanOrphanLibraryFilesAsync(CancellationToken ct)
    {
        CleanupCandidateSet candidates = await LoadCandidatesAsync(ct);
        return ToPlan(candidates);
    }

    /// <summary>Issues a short-lived token that <see cref="ValidatePreviewToken"/> accepts, so a
    /// chat-verb run must follow a fresh preview (defence in depth beyond Boss approval).</summary>
    public string IssuePreviewToken()
    {
        string token = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _previewToken = token;
            _previewTokenExpiresUtc = DateTime.UtcNow.AddMinutes(15);
        }

        return token;
    }

    public bool ValidatePreviewToken(string? token)
    {
        lock (_sync)
        {
            return _previewToken is not null
                && string.Equals(_previewToken, token, StringComparison.Ordinal)
                && DateTime.UtcNow <= _previewTokenExpiresUtc;
        }
    }

    public async Task<MediaCleanupStatusDto> StartDeleteOrphanLibraryFilesAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_starting || _currentRun is { IsCompleted: false })
            {
                return _status;
            }

            _starting = true;
        }

        try
        {
            CleanupCandidateSet candidates = await LoadCandidatesAsync(ct);
            MediaCleanupStatusDto running = new(
                "Running",
                DateTime.UtcNow,
                null,
                ToPlan(candidates),
                null,
                null);

            lock (_sync)
            {
                _status = running;
                _currentRun = Task.Run(
                    () => RunCleanupAsync(candidates, running.StartedAtUtc!.Value, CancellationToken.None),
                    CancellationToken.None);
                _starting = false;
            }

            await PublishStatusAsync();
            return running;
        }
        catch
        {
            lock (_sync)
            {
                _starting = false;
            }

            throw;
        }
    }

    public async Task<MediaCleanupResultDto> DeleteOrphanLibraryFilesAsync(CancellationToken ct)
    {
        CleanupCandidateSet candidates = await LoadCandidatesAsync(ct);
        return DeleteCandidates(candidates, ct);
    }

    private async Task RunCleanupAsync(CleanupCandidateSet candidates, DateTime startedAtUtc, CancellationToken ct)
    {
        try
        {
            MediaCleanupResultDto result = DeleteCandidates(candidates, ct);
            SetStatus(new MediaCleanupStatusDto(
                result.FailedFiles.Count == 0 ? "Succeeded" : "Finished",
                startedAtUtc,
                DateTime.UtcNow,
                ToPlan(candidates),
                result,
                null));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Orphan media cleanup failed");
            SetStatus(new MediaCleanupStatusDto(
                "Failed",
                startedAtUtc,
                DateTime.UtcNow,
                ToPlan(candidates),
                null,
                ex.Message));
        }

        await PublishStatusAsync();
    }

    private async Task<CleanupCandidateSet> LoadCandidatesAsync(CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        string dataRoot = Path.GetFullPath(radioOptions.Value.DataRoot);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var announcementReferences = LoadReferencedPaths(
            dataRoot,
            await db.Announcements.AsNoTracking().Select(item => item.FilePath).ToListAsync(ct),
            pathComparer);
        var trackReferences = LoadReferencedPaths(
            dataRoot,
            await db.Tracks.AsNoTracking().Select(item => item.FilePath).ToListAsync(ct),
            pathComparer);

        IReadOnlyList<CleanupCandidate> announcements = FindCandidates(
            radioOptions.Value.AnnouncementsDirectory,
            announcementReferences,
            pathComparison,
            ct);
        IReadOnlyList<CleanupCandidate> tracks = FindCandidates(
            radioOptions.Value.TracksDirectory,
            trackReferences,
            pathComparison,
            ct);

        return new CleanupCandidateSet(announcements, tracks);
    }

    private MediaCleanupResultDto DeleteCandidates(CleanupCandidateSet candidates, CancellationToken ct)
    {
        CleanupAreaResult announcements = CleanupArea(candidates.Announcements, ct);
        CleanupAreaResult tracks = CleanupArea(candidates.Tracks, ct);

        return new MediaCleanupResultDto(
            announcements.DeletedFiles,
            tracks.DeletedFiles,
            announcements.BytesDeleted + tracks.BytesDeleted,
            announcements.FailedFiles.Concat(tracks.FailedFiles).ToList());
    }

    private static HashSet<string> LoadReferencedPaths(
        string dataRoot,
        IEnumerable<string> relativePaths,
        StringComparer pathComparer)
    {
        return relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.Combine(dataRoot, path)))
            .ToHashSet(pathComparer);
    }

    private IReadOnlyList<CleanupCandidate> FindCandidates(
        string directory,
        HashSet<string> referencedPaths,
        StringComparison pathComparison,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var candidates = new List<CleanupCandidate>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var absolutePath = Path.GetFullPath(file);
            if (referencedPaths.Contains(absolutePath) || !IsUnderDirectory(absolutePath, root, pathComparison))
            {
                continue;
            }

            candidates.Add(new CleanupCandidate(absolutePath, new FileInfo(absolutePath).Length));
        }

        return candidates;
    }

    private CleanupAreaResult CleanupArea(IReadOnlyList<CleanupCandidate> candidates, CancellationToken ct)
    {
        var deletedFiles = 0;
        long bytesDeleted = 0;
        var failedFiles = new List<string>();

        foreach (CleanupCandidate candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                File.Delete(candidate.AbsolutePath);
                deletedFiles++;
                bytesDeleted += candidate.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failedFiles.Add(Path.GetRelativePath(radioOptions.Value.DataRoot, candidate.AbsolutePath));
                logger.LogWarning(ex, "Failed to delete orphan media file {Path}", candidate.AbsolutePath);
            }
        }

        return new CleanupAreaResult(deletedFiles, bytesDeleted, failedFiles);
    }

    private void SetStatus(MediaCleanupStatusDto status)
    {
        lock (_sync)
        {
            _status = status;
        }
    }

    private async Task PublishStatusAsync()
    {
        if (hub is null)
        {
            return;
        }

        try
        {
            await hub.Clients.All.SendAsync("MediaCleanupChanged", CurrentStatus);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR media cleanup publish failed");
        }
    }

    private static MediaCleanupPlanDto ToPlan(CleanupCandidateSet candidates)
        => new(
            candidates.Announcements.Count,
            candidates.Tracks.Count,
            candidates.Announcements.Sum(item => item.Bytes) + candidates.Tracks.Sum(item => item.Bytes));

    private static bool IsUnderDirectory(string path, string directory, StringComparison pathComparison)
        => path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, pathComparison);

    private sealed record CleanupAreaResult(int DeletedFiles, long BytesDeleted, IReadOnlyList<string> FailedFiles)
    {
        public static CleanupAreaResult Empty { get; } = new(0, 0, []);
    }

    private sealed record CleanupCandidate(string AbsolutePath, long Bytes);

    private sealed record CleanupCandidateSet(
        IReadOnlyList<CleanupCandidate> Announcements,
        IReadOnlyList<CleanupCandidate> Tracks);
}
