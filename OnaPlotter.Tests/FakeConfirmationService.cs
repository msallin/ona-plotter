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

    public Task<bool> ConfirmAsync(string message, bool destructive = true)
    {
        CallCount++;
        LastMessage = message;
        return Task.FromResult(AutoConfirm);
    }
}
