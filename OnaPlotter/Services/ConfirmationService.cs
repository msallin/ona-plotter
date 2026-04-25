namespace OnaPlotter.Services;

/// <summary>
/// Default <see cref="IConfirmationService"/>: renders through the
/// Blazor modal host component <c>ConfirmationDialog.razor</c> instead
/// of the browser's native <c>window.confirm</c> / <c>window.prompt</c>.
///
/// Why: the native dialogs ignore theme (jarring white box on a
/// red-shifted night-mode helm), can't reason about keyboard focus
/// trap, and on iPad Safari actually block the whole page thread
/// during the prompt which stalls incoming SignalK deltas. They
/// are also suppressed entirely in some MDM / PWA-standalone
/// configurations. The modal is a plain <c>&lt;div role="dialog"&gt;</c>;
/// the pending prompt is just state on this singleton service.
///
/// Two prompt flavours share the same modal:
///   - ConfirmAsync  -&gt; yes / no question
///   - PromptAsync   -&gt; text input + OK / Cancel
/// The dialog branches on <see cref="IsTextPrompt"/>.
///
/// Only one prompt at a time; starting a second cancels the first
/// (rare in practice; fallback is "safer" = false / null).
/// </summary>
public sealed class ConfirmationService : IConfirmationService
{
    // Separate TCSes because the two flavours resolve different types
    // and we can't easily share one via a boxed / object answer
    // without re-introducing type ambiguity at every call site.
    private TaskCompletionSource<bool>? _pendingConfirm;
    private TaskCompletionSource<string?>? _pendingPrompt;

    public event Action? OnChanged;

    public string Message { get; private set; } = "";
    public bool Destructive { get; private set; } = true;
    public string? ConfirmLabel { get; private set; }
    public string? CancelLabel { get; private set; }
    public bool IsTextPrompt { get; private set; }
    public string TextValue { get; set; } = "";
    public bool IsPending => _pendingConfirm is not null || _pendingPrompt is not null;

    public Task<bool> ConfirmAsync(string message, bool destructive = true,
        string? confirmLabel = null, string? cancelLabel = null)
    {
        // Cancel any prior pending dialog (of either flavour) before
        // showing a new one. Stacking prompts is ambiguous UX; newest
        // wins.
        CancelPending();
        _pendingConfirm = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Message = message;
        Destructive = destructive;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
        IsTextPrompt = false;
        TextValue = "";
        OnChanged?.Invoke();
        return _pendingConfirm.Task;
    }

    public Task<string?> PromptAsync(string message, string initialValue = "",
        string? confirmLabel = null, string? cancelLabel = null)
    {
        CancelPending();
        _pendingPrompt = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Message = message;
        // Text prompts use the primary palette because rename / edit
        // is almost never destructive; destructive text input would
        // be something like "type 'delete' to confirm", which isn't
        // how this is used today.
        Destructive = false;
        ConfirmLabel = confirmLabel ?? "OK";
        CancelLabel = cancelLabel ?? "Cancel";
        IsTextPrompt = true;
        TextValue = initialValue;
        OnChanged?.Invoke();
        return _pendingPrompt.Task;
    }

    public void Resolve(bool ok)
    {
        var confirm = _pendingConfirm;
        var prompt = _pendingPrompt;
        var value = TextValue;
        _pendingConfirm = null;
        _pendingPrompt = null;
        Message = "";
        ConfirmLabel = null;
        CancelLabel = null;
        IsTextPrompt = false;
        TextValue = "";
        // Reset state BEFORE firing OnChanged so a re-render sees the
        // cleared state immediately. Notify then resolve the task so
        // the awaiting caller continues after the UI has caught up.
        OnChanged?.Invoke();
        confirm?.TrySetResult(ok);
        // For text prompts: OK -> trimmed input (null if it trims to
        // empty, so callers can use `is null` to mean "no valid
        // answer"). Cancel always returns null.
        prompt?.TrySetResult(ok
            ? (string.IsNullOrWhiteSpace(value) ? null : value.Trim())
            : null);
    }

    private void CancelPending()
    {
        var confirm = _pendingConfirm;
        var prompt = _pendingPrompt;
        _pendingConfirm = null;
        _pendingPrompt = null;
        confirm?.TrySetResult(false);
        prompt?.TrySetResult(null);
    }
}
