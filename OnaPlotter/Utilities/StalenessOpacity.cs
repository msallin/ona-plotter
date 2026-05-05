namespace OnaPlotter.Utilities;

/// <summary>
/// AIS marker fade ramp: the helm should see at-a-glance which
/// targets are fresh (full opacity), drifting stale (mid-fade), or
/// almost certainly gone (heavily faded). The threshold + the linear
/// interpolation are the actual decision -- they live here so the C#
/// tests pin the exact opacity at every boundary, and JS just mirrors
/// the formula via <c>wwwroot/js/format.js</c>.
///
/// <para>Schedule:
/// <list type="bullet">
///   <item>0-30 s old: full opacity (returns null -- caller leaves
///   the element's inline opacity unset so CSS defaults apply).</item>
///   <item>30-300 s old: linear fade from 1.0 down to 0.35 over
///   the 270 s window.</item>
///   <item>&gt;=300 s old: floored at 0.25 ("too long since last
///   fix; treat the target as essentially gone").</item>
/// </list></para>
/// </summary>
public static class StalenessOpacity
{
    /// <summary>Below this age (seconds), the marker is considered
    /// fresh and gets the default opacity.</summary>
    public const double FreshSeconds = 30.0;

    /// <summary>At and above this age (seconds), the marker is
    /// pinned at <see cref="StaleOpacity"/>.</summary>
    public const double StaleSeconds = 300.0;

    /// <summary>Floor opacity when a target is past
    /// <see cref="StaleSeconds"/>.</summary>
    public const double StaleOpacity = 0.25;

    /// <summary>Slope of the linear fade between
    /// <see cref="FreshSeconds"/> and <see cref="StaleSeconds"/>:
    /// at 30 s = 1.0; at 300 s = 0.35. The 0.65 here is the total
    /// drop over the 270 s window. (StaleOpacity 0.25 only kicks in
    /// past 300 s; the linear segment ends at 0.35 to give a small
    /// visible step at the boundary.)</summary>
    public const double FadeSpan = 0.65;

    /// <summary>
    /// Returns the inline-style opacity to apply for a marker of
    /// the given age, or <c>null</c> when the marker is fresh
    /// (caller should clear any previous inline opacity so the CSS
    /// default applies). String form so the JS layer can write it
    /// to <c>style.opacity</c> verbatim without re-formatting.
    /// </summary>
    public static string? Compute(double ageSeconds)
    {
        if (ageSeconds < FreshSeconds) return null;
        if (ageSeconds >= StaleSeconds)
        {
            return StaleOpacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
        double op = 1.0 - FadeSpan * (ageSeconds - FreshSeconds) / (StaleSeconds - FreshSeconds);
        return op.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }
}
