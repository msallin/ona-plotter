using Microsoft.Extensions.Logging;

namespace OnaPlotter.Tests.Startup;

internal sealed class CapturingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, Exception? Exception)> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _entries.Add((logLevel, exception));
    }
}
