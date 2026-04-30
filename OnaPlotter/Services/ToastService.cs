using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// In-memory toast notification queue. Renders a stack of transient
/// messages that auto-dismiss after a configurable duration.
/// </summary>
public sealed class ToastService : IToastService
{
    private readonly List<Toast> _toasts = [];

    public IReadOnlyList<Toast> Active
    {
        get
        {
            _toasts.RemoveAll(t => t.ExpiresAt <= DateTime.UtcNow);
            return _toasts;
        }
    }

    public event Action? OnChanged;

    public void Show(string message, ToastLevel level = ToastLevel.Info, int durationSec = 4)
    {
        var t = new Toast(Guid.NewGuid(), message, level, DateTime.UtcNow.AddSeconds(durationSec));
        _toasts.Add(t);
        OnChanged?.Invoke();

        _ = Task.Delay(durationSec * 1000).ContinueWith(_ =>
        {
            _toasts.RemoveAll(x => x.Id == t.Id);
            OnChanged?.Invoke();
        });
    }

    public void Success(string message) => Show(message, ToastLevel.Success);
    public void Warning(string message) => Show(message, ToastLevel.Warning);
    public void Error(string message) => Show(message, ToastLevel.Error, durationSec: 6);
    /// <summary>Info-level toast convenience helper. Previously
    /// several call sites wrote <c>Toasts.Show(msg, ToastLevel.Info)</c>
    /// inline; this method collapses them to <c>Toasts.Info(msg)</c>
    /// for symmetry with Success / Warning / Error. Default duration
    /// matches Show's default.</summary>
    public void Info(string message) => Show(message, ToastLevel.Info);

    /// <summary>Adds a toast with an action button (e.g. "Undo" after a
    /// destructive operation). The action runs asynchronously; any
    /// exception is swallowed so a flaky restore doesn't crash the UI.</summary>
    public Guid ShowAction(string message, string actionLabel, Func<Task> action,
        ToastLevel level = ToastLevel.Info, int durationSec = 6)
    {
        var t = new Toast(Guid.NewGuid(), message, level,
            DateTime.UtcNow.AddSeconds(durationSec), actionLabel, action);
        _toasts.Add(t);
        OnChanged?.Invoke();

        _ = Task.Delay(durationSec * 1000).ContinueWith(_ =>
        {
            _toasts.RemoveAll(x => x.Id == t.Id);
            OnChanged?.Invoke();
        });
        return t.Id;
    }

    public void Dismiss(Guid id)
    {
        _toasts.RemoveAll(x => x.Id == id);
        OnChanged?.Invoke();
    }
}
