using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Playout;

public sealed record NowPlayingInfo(
    PlayoutItemType ItemType,
    Guid ItemId,
    string Title,
    DateTime StartedAtUtc,
    double DurationSeconds,
    string? ModeratorName);

/// <summary>Singleton "what's on air right now", published by the PlayoutService.</summary>
public interface INowPlayingState
{
    NowPlayingInfo? Current { get; }

    void SetCurrent(NowPlayingInfo? info);
}

public class NowPlayingState : INowPlayingState
{
    private NowPlayingInfo? _current;

    public NowPlayingInfo? Current => Volatile.Read(ref _current);

    public void SetCurrent(NowPlayingInfo? info) => Volatile.Write(ref _current, info);
}
