namespace OnaPlotter.Utilities;

/// <summary>
/// Picks a sensible default swing radius for a fresh anchor drop and
/// describes how it was derived. 5:1 scope on current depth (cruising
/// rule of thumb), clamped to <c>[20, 200]</c> m. Falls back to the
/// helm's last-chosen radius when depth isn't published. Single source
/// of truth for both the panel's pre-fill value and the eyebrow's
/// "(suggestion)" label so the two can't drift.
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
    /// certainly means a depth-sensor fault, not a real anchorage.</summary>
    public const int MaxSuggestedMeters = 200;

    /// <summary>Pick a swing radius (whole metres) per the rules
    /// above. See <see cref="DescribeSuggestion"/> for the matching
    /// "where this number came from" label so the eyebrow text and
    /// the radius value share one decision path.</summary>
    /// <param name="depthMeters">Live <c>environment.depth.belowSurface</c>
    /// when available; null when no transducer is publishing.</param>
    /// <param name="lastChosenMeters">Helm's stored
    /// <c>ManualAnchorRadiusMeters</c>. Acts as the "no depth available"
    /// fallback. Negative or zero values fall through to the floor.
    /// NaN / +/-Infinity are treated as zero (Math.Round + int cast
    /// land in defined territory and the floor pulls the result up).</param>
    public static int Suggest(double? depthMeters, double lastChosenMeters)
    {
        if (depthMeters is double d && IsUsableDepth(d))
        {
            double scoped = d * ScopeMultiplier;
            int rounded = (int)Math.Round(scoped);
            return Math.Clamp(rounded, MinSuggestedMeters, MaxSuggestedMeters);
        }

        // Fallback path: no depth (or unusable). Round defensively.
        // NaN -> 0 -> floor. +Infinity casts to int.MaxValue -> Max
        // pulls the ceiling. -Infinity -> int.MinValue -> floor.
        // The Max(_, MinSuggestedMeters) below normalises every weird
        // input to the floor at minimum.
        // Note: NO upper clamp here - the helm's stored last-chosen
        // value is intentionally preserved even above MaxSuggestedMeters,
        // so a 250 m radius for a genuinely-deep anchorage doesn't get
        // silently capped to 200 on the next drop. See the
        // NullDepth_LastChosenAboveCeiling_KeepsValue test for the
        // contract pin.
        if (double.IsNaN(lastChosenMeters)) return MinSuggestedMeters;
        int fromLast = double.IsInfinity(lastChosenMeters)
            ? (lastChosenMeters > 0 ? MaxSuggestedMeters : MinSuggestedMeters)
            : (int)Math.Round(lastChosenMeters);
        return Math.Max(fromLast, MinSuggestedMeters);
    }

    /// <summary>One-line "where this number came from" label that
    /// the panel's eyebrow shows under the title. Empty string when
    /// the source is uninteresting (no depth + no last-drop). Single
    /// source of truth shared with <see cref="Suggest"/> - the same
    /// <see cref="IsUsableDepth"/> predicate decides which branch
    /// fires, so the radius and the eyebrow can't disagree.</summary>
    public static string DescribeSuggestion(double? depthMeters)
    {
        if (depthMeters is double d && IsUsableDepth(d))
        {
            // Use a compact ASCII "x" instead of the unicode times
            // sign so the test fixtures + production output match
            // with no encoding ambiguity, and the ASCII "x" reads
            // fine on every system font.
            return $"{(int)ScopeMultiplier}x {d:F1} m depth";
        }
        return "from last drop";
    }

    /// <summary>True when a depth reading is positive, finite, and
    /// not absurd. Public so the eyebrow + heuristic share the same
    /// gate - avoids the duplicate-predicate-drift hazard. Anything
    /// outside <c>(0, 1000)</c> metres or non-finite is treated as
    /// "depth missing" and routes to the fallback.</summary>
    public static bool IsUsableDepth(double d) =>
        !double.IsNaN(d) && !double.IsInfinity(d) && d > 0 && d < 1000;
}
