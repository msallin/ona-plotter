using Microsoft.Extensions.Logging;

namespace OnaPlotter.Tests;

/// <summary>
/// <see cref="ILogger{T}"/> that keeps every entry in memory so a test
/// can assert that a failure was logged at the right level. Paired with
/// <see cref="RecordingLoggerFactory"/> when the unit under test creates
/// its own category loggers.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries;

    public RecordingLogger() : this([]) { }

    internal RecordingLogger(List<(LogLevel Level, string Message)> sink) { _entries = sink; }

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>
/// <see cref="ILoggerFactory"/> that records which categories were
/// requested and funnels every logger into one shared entry list.
/// </summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public List<string> Categories { get; } = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public ILogger CreateLogger(string categoryName)
    {
        Categories.Add(categoryName);
        return new RecordingLogger<object>(_entries);
    }

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}
