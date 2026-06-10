using System.Collections.Concurrent;

namespace WhipRadio.Orchestrator.Services;

public sealed record LogEntry(DateTime TimestampUtc, string Level, string Category, string Message);

/// <summary>Ring buffer of recent log lines, served on the web app's Console page.</summary>
public class InMemoryLogBuffer
{
    private const int Capacity = 600;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<LogEntry> Snapshot(int take = 300)
        => _entries.Reverse().Take(take).ToList();
}

public sealed class BufferLoggerProvider(InMemoryLogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, categoryName);

    public void Dispose()
    {
    }

    private sealed class BufferLogger(InMemoryLogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" — {exception.GetBaseException().Message}";
            }

            var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            buffer.Add(new LogEntry(DateTime.UtcNow, logLevel.ToString(), shortCategory, message));
        }
    }
}
