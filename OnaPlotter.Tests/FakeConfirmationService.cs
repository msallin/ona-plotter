using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Test double for <see cref="IConfirmationService"/>. Defaults to
/// "always confirm" (the production native <c>confirm()</c> can't
/// be exercised from bUnit anyway); tests that need the "declined"
/// path flip <see cref="AutoConfirm"/> to false.
///
/// Captures the last message so tests can assert on the prompt
/// text without having to stub JSInterop.
/// </summary>
internal sealed class FakeConfirmationService : IConfirmationService
{
    public bool AutoConfirm { get; set; } = true;
    public string? LastMessage { get; private set; }
    public int CallCount { get; private set; }

    // Interface members added to support the Blazor-modal host. Tests
    // that don't exercise the modal just ignore these.
    public event Action? OnChanged { add { } remove { } }
    public string Message => LastMessage ?? "";
    public bool Destructive { get; private set; }
    public string? ConfirmLabel { get; private set; }
    public string? CancelLabel { get; private set; }
    public bool IsPending => false;
    public bool IsTextPrompt { get; private set; }
    public string TextValue { get; set; } = "";
    public void Resolve(bool ok) { /* tests resolve synchronously via AutoConfirm */ }

    public Task<bool> ConfirmAsync(string message, bool destructive = true,
        string? confirmLabel = null, string? cancelLabel = null)
    {
        CallCount++;
        LastMessage = message;
        Destructive = destructive;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
        IsTextPrompt = false;
        return Task.FromResult(AutoConfirm);
    }

    /// <summary>Auto-answer for <see cref="PromptAsync"/>. Defaults
    /// to null (simulates a Cancel). Tests that exercise the rename
    /// flow set this to the desired string; AutoConfirm also gates
    /// this -- false means return null regardless.</summary>
    public string? AutoPromptValue { get; set; }

    public Task<string?> PromptAsync(string message, string initialValue = "",
        string? confirmLabel = null, string? cancelLabel = null)
    {
        CallCount++;
        LastMessage = message;
        Destructive = false;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
        IsTextPrompt = true;
        TextValue = initialValue;
        return Task.FromResult(AutoConfirm ? AutoPromptValue : null);
    }
}
