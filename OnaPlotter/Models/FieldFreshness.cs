namespace OnaPlotter.Models;

/// <summary>
/// Freshness category for a safety-critical sensor field. Consumers
/// (HUD cards, alarm rules) decide how to render / gate on the
/// category rather than each reimplementing the timeout thresholds.
/// </summary>
public enum FieldFreshness
{
    /// <summary>Never received. HUD should render "—", not zero / last-known.</summary>
    Missing,

    /// <summary>Recently updated (&lt; 10 s). Render as normal.</summary>
    Live,

    /// <summary>Updated within the last 30 s but not recently. HUD should
    /// badge "stale" so the helmsman knows the sensor is lagging or
    /// reporting slowly.</summary>
    Stale,

    /// <summary>Not updated in 30 s or more. HUD should grey out the
    /// value (or drop it) to signal the sensor is almost certainly
    /// dead or disconnected.</summary>
    Dead,
}
