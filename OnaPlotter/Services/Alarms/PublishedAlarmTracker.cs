namespace OnaPlotter.Services.Alarms;

/// <summary>
/// Concrete owned-path set used by <see cref="AlarmPublisher"/>. The
/// publisher mutates via <see cref="Add"/> / <see cref="Remove"/>;
/// the bridge rule reads via the <see cref="IPublishedAlarmTracker"/>
/// interface. Backed by an ordinal-comparing <see cref="HashSet{T}"/>
/// so the read path (called inside the alarm-evaluation hot loop) is
/// O(1).
/// <para>
/// Lifetime: singleton, scoped to the app session. We never persist
/// the set -- a reload starts with an empty tracker. That's correct
/// because <see cref="AlarmPublisher"/> also clears its server-side
/// raises on dispose, and a new session re-raises any still-live
/// alarms on the next Evaluate tick.
/// </para>
/// </summary>
public sealed class PublishedAlarmTracker : IPublishedAlarmTracker
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

    public bool IsOwnedPath(string path) => _paths.Contains(path);

    /// <summary>Records a path as ours. Called by
    /// <see cref="AlarmPublisher"/> when it submits a raise; the bridge
    /// rule starts skipping the path on the very next Evaluate tick.</summary>
    internal void Add(string path) => _paths.Add(path);

    /// <summary>Releases ownership of a path. Called by the publisher
    /// after a successful clear / on raise failure / on dispose. Returns
    /// true when the path was present.</summary>
    internal bool Remove(string path) => _paths.Remove(path);

    /// <summary>Snapshot of currently-owned paths. Test-only; production
    /// callers go through <see cref="IsOwnedPath"/>.</summary>
    internal IReadOnlyCollection<string> OwnedPaths => _paths;
}
