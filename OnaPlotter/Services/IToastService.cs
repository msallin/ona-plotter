using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>Transient notification messages shown to the user.</summary>
public interface IToastService
{
    IReadOnlyList<Toast> Active { get; }
    event Action? OnChanged;

    /// <summary>
    /// Show a toast. Identical (<paramref name="message"/>,
    /// <paramref name="level"/>) pairs that are still active are
    /// deduplicated: the existing toast's expiry is pushed out
    /// rather than a second copy stacking up. Prevents a flaky
    /// transport from rendering five "Failed to fetch" cards on top
    /// of each other.
    /// </summary>
    void Show(string message, ToastLevel level = ToastLevel.Info, int durationSec = 4);
    void Success(string message);
    void Warning(string message);
    void Error(string message);
    void Info(string message);

    /// <summary>Shows a toast with an action button. Returns the toast id
    /// in case the caller wants to dismiss it early.</summary>
    Guid ShowAction(string message, string actionLabel, Func<Task> action,
        ToastLevel level = ToastLevel.Info, int durationSec = 6);

    /// <summary>
    /// Show a non-auto-dismissing toast. The helm clears it by tapping
    /// (or the caller calls <see cref="Dismiss"/>). Used for the MOB
    /// lat/lon read-back card: a helm reading the coords aloud must
    /// not lose them to a timer firing under their finger. Returns
    /// the toast id so the caller can dismiss it programmatically
    /// when the underlying state clears.
    /// </summary>
    Guid Pinned(string message, ToastLevel level = ToastLevel.Info);

    /// <summary>
    /// Standard exception-handling pattern: write the full exception
    /// (type + message + stack) to the browser console at error level,
    /// then show a generic toast that does NOT leak <c>ex.Message</c>
    /// into the helm-facing UI. Use this in catch blocks where the
    /// exception is already-handled-but-informative; the helm sees
    /// "{action} failed -- check the browser console" and devs see
    /// the full trace in DevTools (or in the SignalK plugin log via
    /// the error-relay bootstrap).
    /// </summary>
    /// <param name="ex">The caught exception. Stack + type are
    /// console-logged; only a generic message reaches the toast.</param>
    /// <param name="action">Verb phrase describing what failed
    /// (e.g. "Save route", "Load chart"). Renders as the toast's
    /// subject so the helm knows which operation broke without seeing
    /// the raw exception text.</param>
    void LogException(Exception ex, string action);

    void Dismiss(Guid id);
}
