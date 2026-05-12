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
    // Separate TCSes because the three flavours resolve different
    // types and we can't easily share one via a boxed / object answer
    // without re-introducing type ambiguity at every call site.
    private TaskCompletionSource<bool>? _pendingConfirm;
    private TaskCompletionSource<string?>? _pendingPrompt;
    private TaskCompletionSource<string?>? _pendingChoose;
    private TaskCompletionSource<IReadOnlySet<string>?>? _pendingMultiChoose;
    private HashSet<string> _multiChoiceSelected = new(StringComparer.Ordinal);

    public event Action? OnChanged;

    public string Message { get; private set; } = "";
    public bool Destructive { get; private set; } = true;
    public string? ConfirmLabel { get; private set; }
    public string? CancelLabel { get; private set; }
    public bool IsTextPrompt { get; private set; }
    public bool IsChoice { get; private set; }
    public bool IsMultiChoice { get; private set; }
    public IReadOnlyList<string> Options { get; private set; } = [];
    public IReadOnlySet<string> MultiChoiceSelected => _multiChoiceSelected;
    public string TextValue { get; set; } = "";
    public bool IsPending =>
        _pendingConfirm is not null
        || _pendingPrompt is not null
        || _pendingChoose is not null
        || _pendingMultiChoose is not null;

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
        IsChoice = false;
        IsMultiChoice = false;
        Options = [];
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
        IsChoice = false;
        IsMultiChoice = false;
        Options = [];
        TextValue = initialValue;
        OnChanged?.Invoke();
        return _pendingPrompt.Task;
    }

    public Task<string?> ChooseAsync(string message, IReadOnlyList<string> options)
    {
        CancelPending();
        if (options is null || options.Count == 0)
        {
            // No options = degenerate caller. Return null synchronously
            // rather than open an empty dialog the helm has to dismiss.
            return Task.FromResult<string?>(null);
        }
        _pendingChoose = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Message = message;
        // Choosers are non-destructive by default. If a future
        // destructive chooser ("Delete just this / Delete the whole
        // group?") shows up, add an overload then; YAGNI for now.
        Destructive = false;
        ConfirmLabel = null;
        CancelLabel = "Cancel";
        IsTextPrompt = false;
        IsChoice = true;
        IsMultiChoice = false;
        Options = options;
        TextValue = "";
        OnChanged?.Invoke();
        return _pendingChoose.Task;
    }

    public void Pick(string option)
    {
        var choose = _pendingChoose;
        if (choose is null) return;
        // Reset before notifying, same pattern as Resolve().
        _pendingChoose = null;
        Message = "";
        ConfirmLabel = null;
        CancelLabel = null;
        IsChoice = false;
        Options = [];
        OnChanged?.Invoke();
        choose.TrySetResult(option);
    }

    public Task<IReadOnlySet<string>?> MultiChooseAsync(string message,
        IReadOnlyList<string> options,
        IReadOnlyCollection<string> defaults,
        string? confirmLabel = null)
    {
        CancelPending();
        if (options is null || options.Count == 0)
        {
            // No options to pick from = degenerate caller. Resolve
            // synchronously with the empty set so the awaiting code
            // can short-circuit without a dismiss tap.
            return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>(StringComparer.Ordinal));
        }
        _pendingMultiChoose = new TaskCompletionSource<IReadOnlySet<string>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Message = message;
        Destructive = false;
        ConfirmLabel = confirmLabel ?? "Export";
        CancelLabel = "Cancel";
        IsTextPrompt = false;
        IsChoice = false;
        IsMultiChoice = true;
        Options = options;
        TextValue = "";
        // Seed the checked set from defaults, intersected with options
        // so a caller-supplied stray default doesn't pollute the
        // resolved set. StringComparer.Ordinal because the options
        // are stable enum-like labels, not free-form helm input.
        var optionSet = new HashSet<string>(options, StringComparer.Ordinal);
        _multiChoiceSelected = new HashSet<string>(StringComparer.Ordinal);
        if (defaults is not null)
        {
            foreach (var d in defaults)
            {
                if (optionSet.Contains(d)) _multiChoiceSelected.Add(d);
            }
        }
        OnChanged?.Invoke();
        return _pendingMultiChoose.Task;
    }

    public void ToggleMultiChoice(string option, bool value)
    {
        if (_pendingMultiChoose is null) return;
        // Validate against the current Options to keep the resolved
        // set bounded; a host that two-way-binds against a stale
        // option list shouldn't be able to leak a non-option string.
        bool isOption = false;
        for (int i = 0; i < Options.Count; i++)
        {
            if (string.Equals(Options[i], option, StringComparison.Ordinal))
            {
                isOption = true;
                break;
            }
        }
        if (!isOption) return;
        if (value) _multiChoiceSelected.Add(option);
        else _multiChoiceSelected.Remove(option);
        OnChanged?.Invoke();
    }

    public void Resolve(bool ok)
    {
        var confirm = _pendingConfirm;
        var prompt = _pendingPrompt;
        var choose = _pendingChoose;
        var multi = _pendingMultiChoose;
        var value = TextValue;
        // Snapshot the checked set before clearing it so the resolved
        // task receives a stable read-only view.
        IReadOnlySet<string>? multiResult = null;
        if (multi is not null && ok)
        {
            multiResult = new HashSet<string>(_multiChoiceSelected, StringComparer.Ordinal);
        }
        _pendingConfirm = null;
        _pendingPrompt = null;
        _pendingChoose = null;
        _pendingMultiChoose = null;
        Message = "";
        ConfirmLabel = null;
        CancelLabel = null;
        IsTextPrompt = false;
        IsChoice = false;
        IsMultiChoice = false;
        Options = [];
        _multiChoiceSelected = new HashSet<string>(StringComparer.Ordinal);
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
        // Chooser: Resolve(true) without going through Pick() means a
        // background flow tried to confirm a chooser; treat as cancel.
        // Pick() handles the actual option-chosen path and clears the
        // TCS before this branch is reached.
        choose?.TrySetResult(null);
        // Multi-choose: OK -> the captured snapshot above (may be the
        // empty set when the helm un-checked every option, which is
        // distinct from null = Cancel). Cancel always returns null.
        multi?.TrySetResult(ok ? multiResult : null);
    }

    private void CancelPending()
    {
        var confirm = _pendingConfirm;
        var prompt = _pendingPrompt;
        var choose = _pendingChoose;
        var multi = _pendingMultiChoose;
        _pendingConfirm = null;
        _pendingPrompt = null;
        _pendingChoose = null;
        _pendingMultiChoose = null;
        confirm?.TrySetResult(false);
        prompt?.TrySetResult(null);
        choose?.TrySetResult(null);
        multi?.TrySetResult(null);
    }
}
