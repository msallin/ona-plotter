using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Recording <see cref="IToastService"/>: every toast lands in
/// <see cref="Shown"/> as (level, message) so tests can assert on what
/// the helm would have seen. Nothing expires.
/// </summary>
internal sealed class FakeToastService : IToastService
{
    private readonly object _lock = new();

    public List<(ToastLevel Level, string Message)> Shown { get; } = [];

    public IReadOnlyList<Toast> Active => [];

    public event Action? OnChanged;

    /// <summary>Messages shown at <see cref="ToastLevel.Warning"/>.</summary>
    public List<string> Warnings
    {
        get { lock (_lock) return Shown.Where(t => t.Level == ToastLevel.Warning).Select(t => t.Message).ToList(); }
    }

    public void Show(string message, ToastLevel level = ToastLevel.Info, int durationSec = 4) => Record(message, level);
    public void Success(string message) => Record(message, ToastLevel.Success);
    public void Warning(string message) => Record(message, ToastLevel.Warning);
    public void Error(string message) => Record(message, ToastLevel.Error);
    public void Info(string message) => Record(message, ToastLevel.Info);

    public Guid ShowAction(string message, string actionLabel, Func<Task> action,
        ToastLevel level = ToastLevel.Info, int durationSec = 6)
    {
        Record(message, level);
        return Guid.NewGuid();
    }

    public Guid Pinned(string message, ToastLevel level = ToastLevel.Info)
    {
        Record(message, level);
        return Guid.NewGuid();
    }

    public void LogException(Exception ex, string action) => Record($"{action} failed", ToastLevel.Error);

    public void Dismiss(Guid id) { }

    private void Record(string message, ToastLevel level)
    {
        lock (_lock) Shown.Add((level, message));
        OnChanged?.Invoke();
    }
}
