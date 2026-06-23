using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Music;

/// <summary>
/// Books the first free recording studio for each generation — artists queue
/// for A studio, not a specific one. The request is adapted to whatever
/// protocol the acquired studio speaks (e.g. vocals off for MusicGen).
/// </summary>
public sealed class StudioMusicGenerator(
    StudioCoordinator coordinator,
    StudioProviderFactory factory,
    StudioDockerControl dockerControl,
    StudioHistoryRecorder history,
    OllamaModelMemoryManager modelMemory,
    ILogger<StudioMusicGenerator> logger) : IMusicGenerator
{
    private static readonly TimeSpan AcquireRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromMinutes(30);

    public async Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken ct)
    {
        // Pin the provider only when the caller insists; with fallback allowed,
        // any free studio will do and the request adapts to it.
        var requiredProvider = !request.AllowProviderFallback && !string.IsNullOrWhiteSpace(request.Provider)
            ? MusicBackends.Normalize(request.Provider)
            : null;

        var label = $"Recording for {request.ArtistName ?? request.Genre}";
        var deadline = DateTime.UtcNow + AcquireTimeout;

        Studio? studio = null;
        while (studio is null)
        {
            studio = await coordinator.TryAcquireAsync(StudioKind.Recording, requiredProvider, label, ct);
            if (studio is not null)
            {
                break;
            }

            if (!await coordinator.AnyActiveAsync(StudioKind.Recording, requiredProvider, ct))
            {
                throw new MusicBackendUnavailableException(requiredProvider ?? "recording studio");
            }

            if (!await coordinator.AnyBusyAsync(StudioKind.Recording, requiredProvider, ct)
                && !await coordinator.AnyAvailableAsync(StudioKind.Recording, requiredProvider, ct))
            {
                throw new MusicBackendUnavailableException(requiredProvider ?? "recording studio");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new MusicBackendUnavailableException("all recording studios busy");
            }

            // All studios occupied — the artist waits in line.
            await Task.Delay(AcquireRetryDelay, ct);
        }

        var success = false;
        Guid? historyId = null;
        try
        {
            var effective = AdaptRequestToStudio(request, studio) with
            {
                ProgressReporter = async (progress, token) =>
                    await coordinator.UpdateJobProgressAsync(studio.Id, ProgressText(progress), token),
            };
            historyId = await history.BeginAsync(
                studio,
                label,
                MusicPrompt(effective),
                MusicDetail(effective),
                ct);
            await modelMemory.TryPrepareForLocalGpuJobAsync(
                studio.Url, unloadOllama: true, unloadLocalTts: true, ct);
            var provider = factory.CreateMusicProvider(studio);
            var result = await provider.GenerateAsync(effective, ct);
            await history.CompleteAsync(historyId, MusicResultDetail(result), null, CancellationToken.None);
            success = true;
            return result;
        }
        catch (TimeoutException ex)
        {
            await history.FailAsync(historyId, ex, "Timed out; container restart requested.", CancellationToken.None);
            // A generation that never finishes means the studio's worker is
            // wedged (it keeps answering /health while processing nothing) —
            // every later job would time out too, so restart the container.
            logger.LogWarning(
                "{Studio} timed out — restarting its container: {Message}", studio.Name, ex.Message);
            var (ok, detail) = await dockerControl.TryRestartAsync(
                studio, $"generation timeout: {ex.Message}", force: false, CancellationToken.None);
            logger.LogWarning("{Studio} container restart: {Detail}", studio.Name, ok ? detail : $"skipped/failed — {detail}");
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await history.FailAsync(historyId, ex, null, CancellationToken.None);
            throw;
        }
        finally
        {
            await coordinator.ReleaseAsync(studio.Id, success, CancellationToken.None);
        }
    }

    public Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct)
        => coordinator.AnyAvailableAsync(StudioKind.Recording, MusicBackends.Normalize(backend), ct);

    private MusicRequest AdaptRequestToStudio(MusicRequest request, Studio studio)
    {
        var provider = MusicBackends.Normalize(studio.Provider);
        if (provider == MusicBackends.MusicGen && request.WantVocals)
        {
            logger.LogInformation(
                "{Studio} speaks MusicGen — vocals dropped for this recording", studio.Name);
            return request with
            {
                Provider = provider,
                WantVocals = false,
                Lyrics = null,
                LyricsMode = LyricsMode.Instrumental,
            };
        }

        return request with { Provider = provider };
    }

    private static string MusicPrompt(MusicRequest request)
    {
        var lines = new List<string>
        {
            $"Prompt: {request.Prompt}",
            $"Genre: {request.Genre}",
        };

        Add(lines, "Title", request.SongTitle);
        Add(lines, "Subgenre", request.SubGenre);
        Add(lines, "Style", request.Style);
        Add(lines, "Artist", request.ArtistName);
        Add(lines, "Artist biography", request.ArtistBackstory);
        Add(lines, "Song story", request.SongStory);
        Add(lines, "Language", request.Language);
        Add(lines, "Lyrics mode", request.LyricsMode.ToString());
        Add(lines, "Artist song history", request.ArtistSongHistory);
        if (!string.IsNullOrWhiteSpace(request.Lyrics))
        {
            lines.Add($"Lyrics:{Environment.NewLine}{request.Lyrics}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string MusicDetail(MusicRequest request)
    {
        var lines = new List<string>
        {
            $"Provider: {request.Provider}",
            $"Duration: {request.DurationSeconds}s",
            $"Vocals: {(request.WantVocals ? "yes" : "no")}",
            $"Vocal gender: {request.VocalGender}",
        };

        Add(lines, "Vocal style", request.VocalStyle);
        Add(lines, "BPM", request.Bpm?.ToString());
        Add(lines, "Key", request.KeyScale);
        Add(lines, "Time signature", request.TimeSignature);
        Add(lines, "Seed", request.Seed?.ToString());
        Add(lines, "Reference audio", request.ReferenceAudioLabel);
        Add(lines, "Reference audio path", request.ReferenceAudioPath);

        return string.Join(Environment.NewLine, lines);
    }

    private static string MusicResultDetail(MusicResult result)
    {
        var lines = new List<string>
        {
            $"Backend: {result.BackendUsed}",
            $"Audio bytes: {result.WavData.Length}",
        };

        Add(lines, "Model", result.ModelUsed);
        Add(lines, "Seed", result.SeedUsed);
        Add(lines, "Task", result.TaskId);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ProgressText(MusicGenerationProgress progress)
    {
        var task = string.IsNullOrWhiteSpace(progress.TaskId)
            ? string.Empty
            : $" (task {ShortTaskId(progress.TaskId)})";
        if (progress.Percent is { } percent)
        {
            return $"{Math.Clamp(percent, 0, 100):0}% · {progress.Message}";
        }

        return $"{progress.Message}{task}";
    }

    private static string ShortTaskId(string taskId)
        => taskId.Length <= 12 ? taskId : taskId[..12];

    private static void Add(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }
}
