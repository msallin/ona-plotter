namespace OnaPlotter.Services;

/// <summary>
/// Tracks the last time the user interacted with the Map page. Consumed by
/// <see cref="Alarms.DeadmanAlarmRule"/> to fire a "still there?" warning
/// after the configured inactivity window. Lives as a DI singleton because
/// the rule needs to read it on every Evaluate tick without plumbing the
/// Map component into the alarm layer.
/// </summary>
public sealed class DeadmanTracker
{
    private DateTime _lastUtc = DateTime.UtcNow;

    /// <summary>UTC time of the most recent user interaction on the Map
    /// page (any click, tap, key, or pointer event). Defaults to the
    /// moment this service was constructed so a fresh app load doesn't
    /// fire the alarm instantly.</summary>
    public DateTime LastInteractionUtc => _lastUtc;

    /// <summary>Stamps the current time as the latest user interaction.
    /// Map.razor calls this on any input event; the throttle is inside
    /// the caller because a sync property set is cheaper than a no-op
    /// call either way.</summary>
    public void Touch() => _lastUtc = DateTime.UtcNow;

    /// <summary>Test / reset seam: force the tracker to a specific
    /// timestamp. Not called in production code.</summary>
    internal void ForceLastInteraction(DateTime utc) => _lastUtc = utc;
}
