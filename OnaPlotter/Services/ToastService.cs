namespace OnaPlotter.Services;

/// <summary>
/// In-memory toast notification queue. Renders a stack of transient
/// messages that auto-dismiss after a configurable duration.
/// </summary>
public sealed class ToastService : IToastService
{
    public sealed record Toast(Guid Id, string Message, ToastLevel Level, DateTime ExpiresAt);

    public enum ToastLevel { Info, Success, Warning, Error }

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

    public void Dismiss(Guid id)
    {
        _toasts.RemoveAll(x => x.Id == id);
        OnChanged?.Invoke();
    }
}
