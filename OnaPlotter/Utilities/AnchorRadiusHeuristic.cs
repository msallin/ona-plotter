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

    /// <summary>
    /// Horizontal distance the boat can reach from the anchor given a
    /// length of deployed rode and the water depth - the taut rode
    /// collapsed to a straight line: <c>sqrt(rode^2 - depth^2)</c>.
    /// This is the worst case the chain alone permits, used as a floor
    /// for the Auto radius so the alarm circle isn't drawn inside the
    /// ring the boat could physically swing to before it has actually
    /// blown back that far (observed swing lags the chain on a calm
    /// set).
    ///
    /// <para>Input: rode=40 m, depth=8 m  -> sqrt(1600-64) = 39.2 m.
    /// Input: rode=40 m, depth=null       -> 40 m (conservative).</para>
    ///
    /// <list type="bullet">
    ///   <item>Rode &lt;= 0 / NaN / Infinity -> 0 (no usable rode, the
    ///   chain term simply doesn't contribute).</item>
    ///   <item>Depth unusable (null / non-positive / non-finite) ->
    ///   the full rode length, the conservative upper bound (rode lying
    ///   flat); over-estimating the radius errs on the safe side.</item>
    ///   <item>Rode &lt;= depth (sub-1:1 scope, rode effectively
    ///   vertical) -> 0; there's no meaningful horizontal reach.</item>
    /// </list>
    /// </summary>
    public static double ChainHorizontalProjection(double chainLengthMeters, double? depthMeters)
    {
        if (double.IsNaN(chainLengthMeters) || double.IsInfinity(chainLengthMeters) || chainLengthMeters <= 0)
            return 0;
        if (depthMeters is not double d || double.IsNaN(d) || double.IsInfinity(d) || d <= 0)
            return chainLengthMeters;       // depth unknown -> conservative upper bound
        if (chainLengthMeters <= d)
            return 0;                       // rode shorter than depth -> ~vertical, no horizontal reach
        return Math.Sqrt(chainLengthMeters * chainLengthMeters - d * d);
    }

    /// <summary>Smallest radius the Auto picker ships. Stops a stale GPS
    /// sitting on top of the pin from committing a 5 m alarm radius
    /// (smaller than helm-typical swing on any cruising boat).</summary>
    public const int AutoFloorMeters = 20;

    /// <summary>Auto radii are rounded up to a multiple of this.</summary>
    public const double AutoStepMeters = 5.0;

    /// <summary>Which input drove the swing component of the Auto
    /// radius; selects the breakdown label so the helm can tell whether
    /// the number came from observed motion or the entered rode.</summary>
    public enum SwingSource { Peak, Current, Distance, Chain }

    /// <summary>Live inputs for the Auto radius. Anchor / own-ship
    /// positions feed the distance fallback; the tide pair is
    /// <c>environment.tide.heightNow</c> / <c>heightLow</c>.</summary>
    public readonly record struct AutoInputs(
        double? PeakRadius,
        double? CurrentRadius,
        double? AnchorLat,
        double? AnchorLon,
        double? OwnLat,
        double? OwnLon,
        double ChainLengthMeters,
        double? DepthMeters,
        double? TideHeightNow,
        double? TideHeightLow);

    /// <summary>Unrounded components of the Auto radius.</summary>
    public readonly record struct AutoParts(
        double SwingMeters,
        double TideDropMeters,
        SwingSource Source);

    /// <summary>
    /// Auto = swing + tide drop + safety margin, rounded up to
    /// <see cref="AutoStepMeters"/>, floored at <see cref="AutoFloorMeters"/>.
    /// Returns null when there is no usable signal (no observed swing
    /// and no chain entered).
    ///
    /// <list type="bullet">
    ///   <item><b>swing</b>: the largest distance the boat has reached
    ///     from the anchor pin since drop: the plugin's peak radius,
    ///     else its current radius, else the haversine distance from
    ///     boat to pin. The helm-entered rode projected onto the
    ///     horizontal (<see cref="ChainHorizontalProjection"/>) is a
    ///     floor, and the sole input when nothing is observed yet.</item>
    ///   <item><b>tide drop</b>: heightNow minus the next predicted
    ///     low; zero when either is missing or the tide is already at
    ///     or below that low. Adding it directly is conservative.</item>
    /// </list>
    /// </summary>
    public static AutoParts? ComputeAutoParts(in AutoInputs i)
    {
        // Observed swing from the most reliable source available.
        double? observed = null;
        SwingSource source = SwingSource.Distance;
        if (i.PeakRadius is double pk && double.IsFinite(pk) && pk >= 0)
        {
            observed = pk;
            source = SwingSource.Peak;
        }
        else if (i.CurrentRadius is double cur && double.IsFinite(cur) && cur >= 0)
        {
            observed = cur;
            source = SwingSource.Current;
        }
        else if (i.AnchorLat is double aLat
                 && i.AnchorLon is double aLon
                 && i.OwnLat is double lat
                 && i.OwnLon is double lon)
        {
            double distM = GeoMath.HaversineMeters(aLat, aLon, lat, lon);
            if (double.IsFinite(distM) && distM >= 0)
            {
                observed = distM;
                source = SwingSource.Distance;
            }
        }

        // Chain floor: how far the boat CAN swing on the rode that's
        // out, even before it has blown back that far. Keeps Auto safe
        // on a calm set where the boat sits over its anchor with lots
        // of scope out.
        double chainProjection = ChainHorizontalProjection(i.ChainLengthMeters, i.DepthMeters);

        double swing;
        if (observed is double os)
        {
            if (chainProjection > os) { swing = chainProjection; source = SwingSource.Chain; }
            else swing = os;
        }
        else if (chainProjection > 0)
        {
            // No GPS / pin signal yet, but the helm gave us chain.
            swing = chainProjection;
            source = SwingSource.Chain;
        }
        else
        {
            return null;
        }

        double tideDrop = 0;
        if (i.TideHeightNow is double hn
            && i.TideHeightLow is double hl
            && double.IsFinite(hn) && double.IsFinite(hl)
            && hn > hl)
        {
            tideDrop = hn - hl;
        }
        return new AutoParts(swing, tideDrop, source);
    }

    /// <summary>The radius the Auto chip commits: components plus
    /// <paramref name="marginMeters"/>, rounded up to
    /// <see cref="AutoStepMeters"/>, never below
    /// <see cref="AutoFloorMeters"/>.</summary>
    public static int AutoRadiusMeters(in AutoParts parts, double marginMeters)
    {
        double total = parts.SwingMeters + parts.TideDropMeters + marginMeters;
        int rounded = (int)Math.Ceiling(total / AutoStepMeters) * (int)AutoStepMeters;
        return Math.Max(rounded, AutoFloorMeters);
    }

    /// <summary>Helm-readable breakdown of the Auto radius, e.g.
    /// "swing 38 m + tide drop 4 m + 5 m margin". The tide term is
    /// omitted when it rounds to zero so the line carries no
    /// "+ 0 m" noise.</summary>
    public static string DescribeAuto(in AutoParts parts, double marginMeters)
    {
        int swing = (int)Math.Round(parts.SwingMeters);
        int tide = (int)Math.Round(parts.TideDropMeters);
        string swingLabel = parts.Source switch
        {
            SwingSource.Peak => "swing",
            SwingSource.Chain => "rode",
            _ => "current",
        };
        return tide > 0
            ? $"{swingLabel} {swing} m + tide drop {tide} m + {marginMeters:F0} m margin"
            : $"{swingLabel} {swing} m + {marginMeters:F0} m margin";
    }
}
