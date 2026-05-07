namespace OnaPlotter.Services.Alarms;

/// <summary>
/// No-op tracker used by tests that don't wire the publishing side.
/// Always reports "not owned", so the bridge rule's
/// <see cref="ServerNotificationsAlarmRule.CheckMany"/> never skips
/// a notification on tracker grounds when running with this stub.
/// <para>
/// The presence of this type lets <see cref="ServerNotificationsAlarmRule"/>
/// keep <see cref="IPublishedAlarmTracker"/> as a non-nullable
/// constructor parameter - production wires the real tracker; tests
/// pass <see cref="Instance"/>. Without this stub the rule would
/// either need a nullable parameter (ambiguous contract; "is the
/// suppression behaviour active or not?") or every test would have
/// to roll its own no-op tracker.
/// </para>
/// </summary>
public sealed class NoopPublishedAlarmTracker : IPublishedAlarmTracker
{
    /// <summary>Singleton instance. Stateless and trivially shareable.</summary>
    public static readonly NoopPublishedAlarmTracker Instance = new();

    private NoopPublishedAlarmTracker() { }

    public bool IsOwnedPath(string path) => false;
}
