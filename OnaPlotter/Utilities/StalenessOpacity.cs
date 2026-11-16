namespace OnaPlotter.Utilities;

/// <summary>
/// AIS marker fade ramp: the helm should see at-a-glance which
/// targets are fresh (full opacity), drifting stale (mid-fade), or
/// almost certainly gone (heavily faded). The threshold and the
/// linear interpolation are the decision; <c>AisPushService</c> stamps
/// the result on each vessel payload and the JS renderer writes it to
/// <c>style.opacity</c> verbatim.
///
/// <para>Schedule (with the default 300 s faded threshold):
/// <list type="bullet">
///   <item>0-30 s old: full opacity (returns null - caller leaves
///   the element's inline opacity unset so CSS defaults apply).</item>
///   <item>30 s up to the faded threshold: linear fade from 1.0 down
///   to 0.35.</item>
///   <item>At or past the faded threshold: floored at 0.25 ("too long
///   since last fix; treat the target as essentially gone").</item>
/// </list>
/// The faded threshold is the helm's "AIS inactive" setting, so a
/// harbour helm can dim ghosts sooner than the default.</para>
/// </summary>
public static class StalenessOpacity
{
    /// <summary>Below this age (seconds), the marker is considered
    /// fresh and gets the default opacity.</summary>
    public const double FreshSeconds = 30.0;

    /// <summary>Default faded threshold (seconds): the 5 min AIS
    /// inactive default.</summary>
    public const double StaleSeconds = 300.0;

    /// <summary>Floor opacity when a target is past the faded
    /// threshold.</summary>
    public const double StaleOpacity = 0.25;

    /// <summary>Total drop of the linear fade between
    /// <see cref="FreshSeconds"/> and the faded threshold: 1.0 at the
    /// fresh edge, 0.35 just before the threshold. The floor
    /// (<see cref="StaleOpacity"/>) sits a little lower so crossing
    /// the threshold is a visible step.</summary>
    public const double FadeSpan = 0.65;

    /// <summary>
    /// Returns the inline-style opacity for a marker of the given age,
    /// or <c>null</c> when the marker is fresh (caller clears any
    /// previous inline opacity so the CSS default applies). String form
    /// so the JS layer can write it to <c>style.opacity</c> verbatim.
    /// </summary>
    /// <param name="ageSeconds">Seconds since the last AIS evidence.</param>
    /// <param name="fadedSeconds">Age at which the marker pins at the
    /// floor. A value at or below <see cref="FreshSeconds"/> (or
    /// non-finite) would make the linear segment vanish or invert, so
    /// anything past the fresh edge pins at the floor in that case.</param>
    public static string? Compute(double ageSeconds, double fadedSeconds = StaleSeconds)
    {
        if (ageSeconds < FreshSeconds) return null;
        double faded = double.IsFinite(fadedSeconds) && fadedSeconds > FreshSeconds
            ? fadedSeconds
            : FreshSeconds + 1;
        double op = ageSeconds >= faded
            ? StaleOpacity
            : 1.0 - FadeSpan * (ageSeconds - FreshSeconds) / (faded - FreshSeconds);
        return op.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }
}
