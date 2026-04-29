namespace OnaPlotter.Utilities;

/// <summary>
/// Pure helper that picks a sensible default swing radius for a fresh
/// anchor drop. Replaces the "type the same number every evening at
/// every anchorage" friction with a one-tap suggestion the helm can
/// override via the chip row in the panel.
///
/// <para>The suggestion uses a 5:1 scope multiplier on current depth.
/// 5:1 is the cruising-rule-of-thumb middle of the road -- 3:1 is
/// minimum-storm-anchored, 7:1 is heavy-weather-on-rocks. A picker for
/// the multiplier could land in Settings later; the v1 default keeps
/// the panel one tap deep, not three.</para>
///
/// <para>Floor of 20 m so a shallow harbour (e.g. 2 m at low tide)
/// doesn't suggest a 10 m radius the moment a wind shift puts the
/// boat at the edge. Ceiling of 200 m so a freak depth reading
/// (transducer fault, deep transit) doesn't propose a quarter-mile
/// alarm circle the helm has to tap to undo.</para>
///
/// <para>When depth isn't published (no transducer / boat at the
/// dock) the fallback is whatever the helm last picked -- the existing
/// <c>ManualAnchorRadiusMeters</c> setting -- so the suggestion is
/// always at least as smart as "what did I do last time".</para>
/// </summary>
public static class AnchorRadiusHeuristic
{
    /// <summary>5:1 scope. Promote to a setting when there's demand
    /// for storm-scope vs daysail-scope variants.</summary>
    public const double ScopeMultiplier = 5.0;

    /// <summary>Floor in metres. Below this the chip row's smallest
    /// preset (20 m) is a better default than a tighter radius that
    /// invites jitter false positives.</summary>
    public const int MinSuggestedMeters = 20;

    /// <summary>Ceiling in metres. Anything beyond this almost
    /// certainly means a depth-sensor fault, not a real anchorage.
    /// Capping prevents a quarter-mile alarm ring on first drop.</summary>
    public const int MaxSuggestedMeters = 200;

    /// <summary>
    /// Pick a swing radius (whole metres). When depth is published
    /// uses <see cref="ScopeMultiplier"/> * depth, clamped to
    /// [<see cref="MinSuggestedMeters"/>, <see cref="MaxSuggestedMeters"/>].
    /// When depth isn't published (or is non-positive / NaN / infinity)
    /// returns the helm's last-chosen radius rounded to the nearest metre.
    /// </summary>
    /// <param name="depthMeters">Live <c>environment.depth.belowSurface</c>
    /// when available; null when no transducer is publishing.</param>
    /// <param name="lastChosenMeters">Helm's stored
    /// <c>ManualAnchorRadiusMeters</c>. Acts as the "no depth available"
    /// fallback. Negative or zero values fall through to the floor.</param>
    public static int Suggest(double? depthMeters, double lastChosenMeters)
    {
        if (depthMeters is double d && IsUsableDepth(d))
        {
            double scoped = d * ScopeMultiplier;
            int rounded = (int)Math.Round(scoped);
            return Math.Clamp(rounded, MinSuggestedMeters, MaxSuggestedMeters);
        }

        // Fallback path: no depth (or unusable). Use the persisted
        // helm-chosen value rounded to a whole metre, clamped against
        // the same floor so a corrupt 0 in storage doesn't propose a
        // sub-floor radius.
        int fromLast = (int)Math.Round(lastChosenMeters);
        return Math.Max(fromLast, MinSuggestedMeters);
    }

    /// <summary>True when a depth reading is positive, finite, and
    /// not absurd. Negative depth = sensor calibration issue (most
    /// SK depth plugins report metres-below-transducer; a negative is
    /// a config bug). NaN / infinity = transducer fault. Above 1000 m
    /// is "abyssal" territory the boat isn't anchoring in.</summary>
    private static bool IsUsableDepth(double d) =>
        !double.IsNaN(d) && !double.IsInfinity(d) && d > 0 && d < 1000;
}
