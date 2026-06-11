namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Live, in-memory view of what the mixer is doing right now — surfaced on the
/// admin page so "is it even running?" never needs log spelunking.
/// </summary>
public class MixerDiagnostics
{
    private readonly Lock _lock = new();
    private bool _sessionActive;
    private DateTime? _engagedAtUtc;
    private double _masterSeconds;
    private List<string> _activeItems = [];
    private string? _lastDecision;
    private DateTime? _lastDecisionAtUtc;
    private int _transitionsThisSession;

    public void SessionStarted()
    {
        lock (_lock)
        {
            _sessionActive = true;
            _engagedAtUtc = DateTime.UtcNow;
            _masterSeconds = 0;
            _activeItems = [];
            _transitionsThisSession = 0;
        }
    }

    public void SessionEnded()
    {
        lock (_lock)
        {
            _sessionActive = false;
            _activeItems = [];
        }
    }

    public void Update(double masterSeconds, IEnumerable<string> activeItems)
    {
        lock (_lock)
        {
            _masterSeconds = masterSeconds;
            _activeItems = [.. activeItems];
        }
    }

    public void DecisionMade(string trace)
    {
        lock (_lock)
        {
            _lastDecision = trace;
            _lastDecisionAtUtc = DateTime.UtcNow;
            _transitionsThisSession++;
        }
    }

    public (bool Active, DateTime? EngagedAtUtc, double MasterSeconds, IReadOnlyList<string> ActiveItems,
        string? LastDecision, DateTime? LastDecisionAtUtc, int Transitions) Snapshot()
    {
        lock (_lock)
        {
            return (_sessionActive, _engagedAtUtc, _masterSeconds, _activeItems,
                _lastDecision, _lastDecisionAtUtc, _transitionsThisSession);
        }
    }
}
