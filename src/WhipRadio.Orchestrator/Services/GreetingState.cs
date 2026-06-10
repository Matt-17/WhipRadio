using System.Collections.Concurrent;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// In-memory listener-interaction state: a trivial per-IP submission guard
/// (3 per hour) and a one-shot genre hint that a music request leaves for the
/// next generation cycle.
/// </summary>
public class GreetingState
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _submissions = new();
    private string? _nextGenreHint;

    public bool TryRegisterSubmission(string clientHint)
    {
        var now = DateTime.UtcNow;
        var history = _submissions.GetOrAdd(clientHint, _ => []);
        lock (history)
        {
            history.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (history.Count >= 3)
            {
                return false;
            }

            history.Add(now);
            return true;
        }
    }

    public void SetGenreHint(string? genre)
    {
        if (!string.IsNullOrWhiteSpace(genre))
        {
            Volatile.Write(ref _nextGenreHint, genre);
        }
    }

    /// <summary>Returns the pending hint once, then clears it.</summary>
    public string? ConsumeGenreHint() => Interlocked.Exchange(ref _nextGenreHint, null);
}
