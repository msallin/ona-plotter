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
    /// exception is already-handled-but-informative.
    /// <para>
    /// LOG REACH: the <c>Console.Error</c> write is intercepted by
    /// <c>wwwroot/js/errorRelayBoot.js</c> and POSTed to the SignalK
    /// server's <c>/log</c> endpoint (bounded at 800 chars message +
    /// 8000 chars stack). So a dev with SSH access to the boat reads
    /// the trace from the SK plugin log without the helm ever opening
    /// DevTools. The toast itself stays helm-facing only - "{action}
    /// failed" with no exception text.
    /// </para>
    /// <para>
    /// SANITISATION: <paramref name="action"/> is stripped of newlines
    /// and control characters before logging. Without that strip a
    /// caller passing user-controlled text (e.g. a waypoint name with
    /// embedded \n) could split the SK server's log entry into two
    /// records, confusing log-aggregation downstream.
    /// </para>
    /// </summary>
    /// <param name="ex">The caught exception. Stack + type are
    /// console-logged; only a generic message reaches the toast.</param>
    /// <param name="action">Verb phrase describing what failed
    /// (e.g. "Save route", "Load chart"). Renders as the toast's
    /// subject so the helm knows which operation broke without seeing
    /// the raw exception text. Newlines + control chars stripped
    /// before logging.</param>
    void LogException(Exception ex, string action);

    void Dismiss(Guid id);
}
