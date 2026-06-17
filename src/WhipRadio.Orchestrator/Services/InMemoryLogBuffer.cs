using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WhipRadio.Orchestrator.Services;

public sealed record LogEntry(
    DateTime TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? SourceKind = null,
    string? SourceName = null);

/// <summary>Ring buffer of recent log lines, served on the web app's Console page.</summary>
public class InMemoryLogBuffer
{
    private const int Capacity = 1200;
    private const int BroadcastCapacity = 512;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly Channel<LogEntry> _broadcast = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(BroadcastCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<LogEntry> Broadcast => _broadcast.Reader;

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }

        _broadcast.Writer.TryWrite(entry);
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
            var source = InferSource(category, shortCategory, state);
            buffer.Add(new LogEntry(
                DateTime.UtcNow,
                logLevel.ToString(),
                shortCategory,
                message,
                source.Kind,
                source.Name));
        }

        private static (string? Kind, string? Name) InferSource<TState>(
            string fullCategory, string shortCategory, TState state)
        {
            if (IsWriterRoom(fullCategory, shortCategory))
            {
                return ("WriterRoom", "Writer Room");
            }

            var studio = StructuredValue(state, "Studio", "StudioName", "Booth");
            if (!string.IsNullOrWhiteSpace(studio))
            {
                return ("Studio", studio);
            }

            if (fullCategory.Contains(".Studios.", StringComparison.Ordinal)
                || shortCategory.Contains("Studio", StringComparison.Ordinal))
            {
                return ("Studio", null);
            }

            return (null, null);
        }

        private static bool IsWriterRoom(string fullCategory, string shortCategory)
            => shortCategory is "WriterRoom"
                    or nameof(WhipRadio.Infrastructure.Llm.TextGenerationRouter)
                    or nameof(WhipRadio.Infrastructure.Llm.OllamaTextGenerationService)
                    or nameof(WhipRadio.Infrastructure.Llm.OpenAiTextGenerationService)
                || fullCategory.Contains(".Llm.", StringComparison.Ordinal);

        private static string? StructuredValue<TState>(TState state, params string[] keys)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            {
                return null;
            }

            foreach (var key in keys)
            {
                foreach (var pair in values)
                {
                    if (string.Equals(pair.Key, key, StringComparison.Ordinal)
                        && pair.Value is not null)
                    {
                        return pair.Value.ToString();
                    }
                }
            }

            return null;
        }
    }
}
