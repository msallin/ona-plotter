using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Utilities;

/// <summary>
/// Pure, side-effect-light helper for the Harbor-mode AIS filter. Was
/// previously inlined in <c>Map.razor.PushAisTargets</c>; lifting it
/// here means the moored-filter contract is unit-testable without
/// bUnit + JS interop, which is what the parallel review flagged
/// (TEST-001). Map.razor becomes a one-liner that delegates the
/// filtering decision and stays focused on the JS-payload assembly.
/// </summary>
public static class HarborAisFilter
{
    /// <summary>
    /// Returns the subset of vessels that should be rendered on the
    /// chart and pushed to the JS layer.
    /// <para>
    /// When <paramref name="harbor"/> is false, every vessel with a
    /// known position passes through (legacy behaviour).
    /// </para>
    /// <para>
    /// When <paramref name="harbor"/> is true:
    /// <list type="bullet">
    ///   <item><description>The tracker's <see cref="IMooredVesselTracker.Cleanup"/>
    ///   is called so dwell rings for vanished vessels don't leak
    ///   memory across long sessions.</description></item>
    ///   <item><description>Vessels classified moored (per the
    ///   tracker's nav.state + dwell rules) are dropped.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static IReadOnlyList<AisVessel> Apply(
        IReadOnlyCollection<AisVessel> vessels,
        IMooredVesselTracker tracker,
        bool harbor,
        DateTime now)
    {
        if (!harbor)
        {
            // Steady-state path: harbor mode is OFF on every tick the
            // helm isn't actively entering / leaving a marina. The
            // producer in production is AisStore.GetVessels(), which
            // already filters null-position vessels and returns an
            // AisVessel[]. When that invariant holds we can fast-return
            // the array as-is, avoiding the per-tick List<>(N) +
            // copy that previously dominated this method's allocation
            // cost on a 200-vessel push.
            //
            // The null-position drop contract is preserved for
            // collections that DO contain nulls (test fixtures that
            // pass raw `new[] { ... }` literals): we scan once, and
            // if a null is found we fall through to the slow allocate
            // path to drop it. Producer-clean arrays (the only case
            // that matters in production) get the zero-alloc path.
            if (vessels is AisVessel[] arr)
            {
                bool allClean = true;
                for (int i = 0; i < arr.Length; i++)
                {
                    var v = arr[i];
                    if (v.Latitude is null || v.Longitude is null)
                    {
                        allClean = false;
                        break;
                    }
                }
                if (allClean) return arr;
            }
            var keptOff = new List<AisVessel>(vessels.Count);
            foreach (var v in vessels)
            {
                if (v.Latitude is null || v.Longitude is null) continue;
                keptOff.Add(v);
            }
            return keptOff;
        }

        // Harbor mode ON: cleanup + drop moored.
        // Vessels-collection overload skips the HashSet build when
        // nothing is tracked, which is most ticks. The previous
        // pattern allocated the set unconditionally.
        tracker.Cleanup(vessels);

        var kept = new List<AisVessel>(vessels.Count);
        foreach (var v in vessels)
        {
            // Position is the bedrock prerequisite - a vessel with
            // null lat / lon can't render on the chart regardless of
            // harbor mode. Mirror what Map.razor was doing inline.
            if (v.Latitude is null || v.Longitude is null) continue;
            if (tracker.IsMoored(v, now)) continue;
            kept.Add(v);
        }
        return kept;
    }
}
