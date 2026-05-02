using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// In-memory toast notification queue. Renders a stack of transient
/// messages that auto-dismiss after a configurable duration; pinned
/// messages stay until explicitly dismissed.
/// </summary>
public sealed class ToastService : IToastService
{
    private readonly List<Toast> _toasts = [];

    public IReadOnlyList<Toast> Active
    {
        get
        {
            // Pinned toasts are kept regardless of ExpiresAt so a
            // long-running MOB read-back doesn't disappear under the
            // helm's finger. Everything else expires on the dot.
            _toasts.RemoveAll(t => !t.IsPinned && t.ExpiresAt <= DateTime.UtcNow);
            return _toasts;
        }
    }

    public event Action? OnChanged;

    public void Show(string message, ToastLevel level = ToastLevel.Info, int durationSec = 4)
    {
        // Dedup: a flaky transport firing the same "Failed to fetch"
        // many times in quick succession would otherwise stack five
        // identical cards on top of each other. If the same
        // (message, level) is already active, refresh its expiry so
        // the latest occurrence drives the dismissal clock and skip
        // the new entry. Pinned toasts are not deduplicated -- a
        // pinned card has no expiry to refresh, and the only call site
        // (MOB) won't fire identical lat/lon often enough to matter.
        for (int i = 0; i < _toasts.Count; i++)
        {
            var existing = _toasts[i];
            if (!existing.IsPinned
                && existing.Level == level
                && existing.Message == message
                && existing.ActionLabel is null)
            {
                _toasts[i] = existing with { ExpiresAt = DateTime.UtcNow.AddSeconds(durationSec) };
                OnChanged?.Invoke();
                return;
            }
        }

        var t = new Toast(Guid.NewGuid(), message, level, DateTime.UtcNow.AddSeconds(durationSec));
        _toasts.Add(t);
        OnChanged?.Invoke();

        ScheduleAutoDismiss(t.Id, durationSec);
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
        ScheduleAutoDismiss(t.Id, durationSec);
        return t.Id;
    }

    /// <inheritdoc/>
    public Guid Pinned(string message, ToastLevel level = ToastLevel.Info)
    {
        // ExpiresAt is irrelevant on a pinned toast -- the Active
        // sweep skips them -- but DateTime.MaxValue makes the intent
        // visible if the value ever surfaces in a debugger.
        var t = new Toast(Guid.NewGuid(), message, level, DateTime.MaxValue, IsPinned: true);
        _toasts.Add(t);
        OnChanged?.Invoke();
        return t.Id;
    }

    /// <inheritdoc/>
    public void LogException(Exception ex, string action)
    {
        // Console.Error in Blazor WASM lands in the browser DevTools'
        // console at error level -- same channel a developer reads
        // when triaging from Settings' "Send test log". The exception
        // type + message + stack go there; the helm-facing toast
        // stays generic.
        Console.Error.WriteLine($"OnaPlotter: {action} failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        Show($"{action} failed -- check the browser console", ToastLevel.Error, durationSec: 6);
    }

    public void Dismiss(Guid id)
    {
        _toasts.RemoveAll(x => x.Id == id);
        OnChanged?.Invoke();
    }

    /// <summary>Fire-and-forget delayed removal. Pulled into a helper
    /// so Show / ShowAction share one body. Pinned toasts skip this
    /// path entirely (no auto-dismiss).</summary>
    private void ScheduleAutoDismiss(Guid id, int durationSec)
    {
        _ = Task.Delay(durationSec * 1000).ContinueWith(_ =>
        {
            // Swallow ObjectDisposedException explicitly: app shutdown
            // can fire OnChanged against an already-disposed
            // MainLayout subscriber. Other exception types continue
            // to propagate to TaskScheduler.UnobservedTaskException.
            try
            {
                // Pinned guard: the dedup-refresh path may have flipped
                // a non-pinned toast's expiry, but pinned toasts are
                // only ever created via Pinned() (which never schedules
                // a sweep). Belt-and-suspenders: never auto-dismiss a
                // toast that is now pinned.
                _toasts.RemoveAll(x => x.Id == id && !x.IsPinned);
                OnChanged?.Invoke();
            }
            catch (ObjectDisposedException) { /* tear-down race */ }
        }, TaskScheduler.Default);
    }
}
