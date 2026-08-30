using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Tests.Fakes;

// Minimal ILogger<T> that keeps what was written. Used where two code paths deliberately produce
// the same outward result and the log line is the only thing that tells them apart — a denied role
// check that could not reach Twitch versus one Twitch actually answered "no" to.
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
