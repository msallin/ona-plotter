namespace OnaPlotter.Services;

/// <summary>
/// Shared confirmation prompt for destructive actions (delete
/// waypoint / stop navigation / clear route / raise anchor / ...).
/// One implementation behind every confirmation call so the modal
/// styling, accessibility, and theme-awareness stay consistent
/// without 8+ call sites needing to touch the native
/// <c>window.confirm</c> dialog.
/// </summary>
public interface IConfirmationService
{
    /// <summary>Shows a prompt and resolves to <c>true</c> when the user
    /// confirms, <c>false</c> when they cancel or the prompt can't
    /// render. Optional <paramref name="confirmLabel"/> and
    /// <paramref name="cancelLabel"/> let callers phrase the buttons
    /// as verbs that match the question ("Discard" / "Keep editing"
    /// instead of the default "Confirm" / "Cancel"). Generic labels
    /// invite "user cancels the cancellation" traps; action verbs
    /// remove the ambiguity.</summary>
    Task<bool> ConfirmAsync(string message, bool destructive = true,
        string? confirmLabel = null, string? cancelLabel = null);

    /// <summary>Text-input variant. Shows the modal with an input
    /// field pre-filled with <paramref name="initialValue"/>; resolves
    /// to the trimmed user-entered string on OK with non-empty input,
    /// or <c>null</c> on Cancel / Escape OR when OK is tapped on
    /// whitespace-only input (so callers can use <c>is null</c> to
    /// mean "no usable answer"). Prefer this over <c>window.prompt</c>
    /// -- the native dialog is blocked in some iOS PWA / MDM profiles
    /// and ignores the app theme, while this one reuses the themed
    /// modal host. Destructive is forced to false so the OK button
    /// uses the primary palette (text-rename is rarely destructive).</summary>
    Task<string?> PromptAsync(string message, string initialValue = "",
        string? confirmLabel = null, string? cancelLabel = null);

    /// <summary>True when the pending prompt is a text-input prompt;
    /// the dialog renders an <c>&lt;input&gt;</c> in that mode.
    /// False for a yes/no confirmation.</summary>
    bool IsTextPrompt { get; }

    /// <summary>Current text-input value. The dialog two-way binds
    /// to this; on Resolve the committed value is returned to the
    /// awaiting PromptAsync caller.</summary>
    string TextValue { get; set; }

    /// <summary>Fires whenever the pending state changes so the modal
    /// host component can re-render. Irrelevant to non-UI callers.</summary>
    event Action? OnChanged;

    /// <summary>Current prompt text if a prompt is pending; empty
    /// when none.</summary>
    string Message { get; }

    /// <summary>Whether the pending prompt is for a destructive
    /// action; the host renders the confirm button in the danger
    /// palette when set.</summary>
    bool Destructive { get; }

    /// <summary>Label for the confirm button. Null/empty means use
    /// the default based on <see cref="Destructive"/>.</summary>
    string? ConfirmLabel { get; }

    /// <summary>Label for the cancel button. Null/empty means the
    /// default "Cancel".</summary>
    string? CancelLabel { get; }

    /// <summary>Whether a prompt is currently awaiting the user's
    /// answer. The host component gates its render on this.</summary>
    bool IsPending { get; }

    /// <summary>Called by the modal host when the user picks a
    /// button; resolves the pending ConfirmAsync / PromptAsync task.
    /// For text prompts, <paramref name="ok"/>=true returns the
    /// current <see cref="TextValue"/>; false returns null.</summary>
    void Resolve(bool ok);
}
