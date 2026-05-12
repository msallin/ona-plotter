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
    /// - the native dialog is blocked in some iOS PWA / MDM profiles
    /// and ignores the app theme, while this one reuses the themed
    /// modal host. Destructive is forced to false so the OK button
    /// uses the primary palette (text-rename is rarely destructive).</summary>
    Task<string?> PromptAsync(string message, string initialValue = "",
        string? confirmLabel = null, string? cancelLabel = null);

    /// <summary>N-option chooser. Shows the modal with one button per
    /// entry in <paramref name="options"/> plus a Cancel; resolves to
    /// the picked option string, or <c>null</c> on Cancel / Escape /
    /// backdrop click. Used for "Export as GPX or GeoJSON?" - where
    /// a yes/no Confirm would force misleading "Cancel = GeoJSON"
    /// labelling and a free-text Prompt would be slower than two
    /// taps. Caller can <c>switch</c> on the returned string; null
    /// means "no usable answer", same convention as
    /// <see cref="PromptAsync"/>.</summary>
    Task<string?> ChooseAsync(string message, IReadOnlyList<string> options);

    /// <summary>Multi-select variant of <see cref="ChooseAsync"/>.
    /// Shows the modal with a checkbox per entry in
    /// <paramref name="options"/>, pre-checked per
    /// <paramref name="defaults"/>, plus a confirm + Cancel button.
    /// Resolves to the set of picked option strings on OK, or
    /// <c>null</c> on Cancel / Escape / backdrop click.
    /// <para>Used for the GPX export field picker (speed / course /
    /// depth) where the helm wants to opt in / out per export. An
    /// empty default set is honoured (everything starts unchecked);
    /// passing options without a defaults entry treats that option
    /// as unchecked.</para></summary>
    Task<IReadOnlySet<string>?> MultiChooseAsync(string message,
        IReadOnlyList<string> options,
        IReadOnlyCollection<string> defaults,
        string? confirmLabel = null);

    /// <summary>True when the pending prompt is a text-input prompt;
    /// the dialog renders an <c>&lt;input&gt;</c> in that mode.
    /// False for a yes/no confirmation.</summary>
    bool IsTextPrompt { get; }

    /// <summary>True when the pending prompt is an N-option chooser;
    /// the dialog renders one button per <see cref="Options"/> entry
    /// + a Cancel. Mutually exclusive with <see cref="IsTextPrompt"/>.</summary>
    bool IsChoice { get; }

    /// <summary>True when the pending prompt is a multi-select chooser
    /// (<see cref="MultiChooseAsync"/>); the dialog renders a checkbox
    /// per <see cref="Options"/> entry, pre-checked per
    /// <see cref="MultiChoiceSelected"/>, plus a confirm + Cancel
    /// button. Mutually exclusive with <see cref="IsTextPrompt"/> and
    /// <see cref="IsChoice"/>.</summary>
    bool IsMultiChoice { get; }

    /// <summary>The choices for an in-flight chooser prompt; empty
    /// when no chooser is pending. The dialog host iterates this to
    /// render one button per option and calls <see cref="Pick"/>
    /// when the user clicks one.</summary>
    IReadOnlyList<string> Options { get; }

    /// <summary>Currently-checked entries for an in-flight multi-choose
    /// prompt. The dialog two-way binds checkbox state through
    /// <see cref="ToggleMultiChoice"/>; on Resolve(true) the set is
    /// returned to the awaiting MultiChooseAsync caller.</summary>
    IReadOnlySet<string> MultiChoiceSelected { get; }

    /// <summary>Toggle (or set) one option's check state during a
    /// pending <see cref="MultiChooseAsync"/> prompt. Pass <paramref
    /// name="value"/> = true to check, false to uncheck. No-op when
    /// no multi-choose is pending OR the option is not in
    /// <see cref="Options"/>.</summary>
    void ToggleMultiChoice(string option, bool value);

    /// <summary>Called by the modal host when the user clicks one of
    /// the chooser buttons. Resolves the pending
    /// <see cref="ChooseAsync"/> with the picked label.</summary>
    void Pick(string option);

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
