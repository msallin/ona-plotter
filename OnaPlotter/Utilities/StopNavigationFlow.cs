using OnaPlotter.Services.Api;

namespace OnaPlotter.Utilities;

/// <summary>
/// Outcome of a Stop-Navigation request, used by the testable
/// <see cref="StopNavigationFlow.RequestStopAsync"/> helper.
/// </summary>
public enum StopNavigationOutcome
{
    /// <summary>A previous Stop tap is still awaiting the SK delta;
    /// the request was a no-op. The route HUD card stays in its
    /// "stopping..." state.</summary>
    AlreadyPending,

    /// <summary>The CourseApi.ClearAsync call succeeded; the helm-
    /// facing visual "stopping in progress" state should engage and
    /// the timeout countdown starts now.</summary>
    Stopped,

    /// <summary>The CourseApi.ClearAsync call threw or returned
    /// failure; the C# state stays as it was (no pending-stop
    /// timestamp recorded) so the next tap retries cleanly.</summary>
    Failed,
}

/// <summary>
/// Result of a Stop-Navigation request. <see cref="ErrorMessage"/>
/// is non-null only on <see cref="StopNavigationOutcome.Failed"/>.
/// </summary>
public readonly record struct StopNavigationResult(
    StopNavigationOutcome Outcome,
    string? ErrorMessage = null);

/// <summary>
/// Pure helper for the Stop-Navigation flow. Lifts the in-flight guard
/// and the CourseApi.ClearAsync call out of <c>Map.razor.StopNavigation</c>
/// so the JSInvokable + popup-driven paths share a single source of
/// truth and the contract can be unit-tested without bUnit / JS
/// interop. The Map.razor caller still owns the visual side-effects
/// (route-stopping CSS, undo toast, _courseStopPendingUtc stamp);
/// this helper just answers "is the request actionable, and if so,
/// did the server accept it?".
/// </summary>
public static class StopNavigationFlow
{
    /// <summary>
    /// Checks the in-flight guard and - if the request is actionable -
    /// fires <see cref="ICourseApi.ClearAsync"/>. Returns the outcome
    /// the caller needs to update its visual state. Never throws on
    /// the API path: a thrown exception becomes
    /// <see cref="StopNavigationOutcome.Failed"/> with the message
    /// preserved for a toast.
    /// </summary>
    /// <param name="api">SignalK course API (production:
    /// <c>CourseApi</c>; tests can pass a fake).</param>
    /// <param name="pendingSince">The Map page's
    /// <c>_courseStopPendingUtc</c>. Non-null means a previous Stop
    /// is still awaiting the SK delta and this request is a no-op.</param>
    public static async Task<StopNavigationResult> RequestStopAsync(
        ICourseApi api, DateTime? pendingSince)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (pendingSince is not null)
            return new(StopNavigationOutcome.AlreadyPending);
        try
        {
            var result = await api.ClearAsync();
            if (result.Success)
                return new(StopNavigationOutcome.Stopped);
            return new(StopNavigationOutcome.Failed, result.Error);
        }
        catch (Exception ex)
        {
            return new(StopNavigationOutcome.Failed, ex.Message);
        }
    }
}
