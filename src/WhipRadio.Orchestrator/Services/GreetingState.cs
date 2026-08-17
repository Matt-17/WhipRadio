using System.Collections.Concurrent;

namespace WhipRadio.Orchestrator.Services;

/// <summary>A music request waiting for production: which message asked for which genre.</summary>
public sealed record RequestHint(Guid MessageId, string Genre);

/// <summary>
/// In-memory listener-interaction state: a per-client submission guard
/// (10 per hour) and the queue of music-request hints that production
/// consumes one per generation cycle.
/// </summary>
public class GreetingState(TimeProvider timeProvider)
{
    private const int MaxSubmissionsPerHour = 10;

    private readonly ConcurrentDictionary<string, List<DateTime>> _submissions = new();
    private readonly ConcurrentQueue<RequestHint> _requestHints = new();

    public bool TryRegisterSubmission(string clientHint)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var history = _submissions.GetOrAdd(clientHint, _ => []);
        lock (history)
        {
            history.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (history.Count >= MaxSubmissionsPerHour)
            {
                return false;
            }

            history.Add(now);
            return true;
        }
    }

    public void EnqueueRequestHint(Guid messageId, string? genre)
    {
        if (!string.IsNullOrWhiteSpace(genre))
        {
            _requestHints.Enqueue(new RequestHint(messageId, genre));
        }
    }

    /// <summary>Hands the oldest pending request to the production cycle.</summary>
    public RequestHint? ConsumeRequestHint()
        => _requestHints.TryDequeue(out var hint) ? hint : null;
}
