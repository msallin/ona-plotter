namespace OnaPlotter.Services.Alarms;

/// <summary>
/// Read-side view of the set of <c>notifications.*</c> paths this
/// plotter's <see cref="AlarmPublisher"/> has raised on the SignalK
/// server. Consulted by <see cref="ServerNotificationsAlarmRule"/> so
/// it can skip echoes of our own publications -- otherwise a single
/// CPA alarm would render twice in our banner stack: once from the
/// originating client-side rule, once from the bridge rule reading
/// our own publish back through the WebSocket feed.
/// <para>
/// Only the publisher mutates the path set; everyone else is a reader.
/// Splitting the read interface from the concrete tracker keeps the
/// bridge rule's dependency surface narrow (no API client, no
/// AlarmManager) and avoids the construction-order tangle that would
/// arise if the rule injected the publisher directly.
/// </para>
/// </summary>
public interface IPublishedAlarmTracker
{
    /// <summary>True if this plotter raised the given notification
    /// path during the current session. Bridge rule skips matching
    /// store entries so the local rule remains the sole source of
    /// truth for the originating banner.</summary>
    bool IsOwnedPath(string path);
}
