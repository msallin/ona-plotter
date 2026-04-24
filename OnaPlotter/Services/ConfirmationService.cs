namespace OnaPlotter.Services;

/// <summary>
/// Default <see cref="IConfirmationService"/>: renders through the
/// Blazor modal host component <c>ConfirmationDialog.razor</c> instead
/// of the browser's native <c>window.confirm</c>.
///
/// Why: the native prompt ignores theme (jarring white box on a
/// red-shifted night-mode helm), can't reason about keyboard focus
/// trap, and on iPad Safari actually blocks the whole page thread
/// during the prompt which stalls incoming SignalK deltas. The
/// modal is a plain <c>&lt;div role="dialog"&gt;</c>; the pending
/// prompt is just state on this singleton service.
///
/// ConfirmAsync returns a Task that the modal host completes via
/// <see cref="Resolve"/> when the user picks OK / Cancel. Only one
/// prompt at a time; starting a second cancels the first (rare in
/// practice; fallback is "safer" = false).
/// </summary>
public sealed class ConfirmationService : IConfirmationService
{
    private TaskCompletionSource<bool>? _pending;

    public event Action? OnChanged;

    public string Message { get; private set; } = "";
    public bool Destructive { get; private set; } = true;
    public string? ConfirmLabel { get; private set; }
    public string? CancelLabel { get; private set; }
    public bool IsPending => _pending is not null;

    public Task<bool> ConfirmAsync(string message, bool destructive = true,
        string? confirmLabel = null, string? cancelLabel = null)
    {
        // If a prompt is already up, cancel it before showing a new
        // one. Stacking confirmations is ambiguous UX; newest wins.
        var prior = _pending;
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Message = message;
        Destructive = destructive;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
        prior?.TrySetResult(false);
        OnChanged?.Invoke();
        return _pending.Task;
    }

    public void Resolve(bool ok)
    {
        var p = _pending;
        _pending = null;
        Message = "";
        ConfirmLabel = null;
        CancelLabel = null;
        // Reset state BEFORE firing OnChanged so a re-render sees the
        // cleared state immediately. Notify then resolve the task so
        // the awaiting caller continues after the UI has caught up.
        OnChanged?.Invoke();
        p?.TrySetResult(ok);
    }
}
