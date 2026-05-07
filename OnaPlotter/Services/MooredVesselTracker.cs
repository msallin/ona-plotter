using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Tracks how long each AIS vessel has held near-zero speed AND honours
/// SignalK's <c>navigation.state</c> when published. Once the dwell
/// crosses <see cref="MooredHoldSeconds"/> - or the vessel publishes a
/// moored-class <c>navigation.state</c> - it's considered moored
/// (harbour tug, anchored fishing boat, ferry holding for a berth) and
/// alarm logic / harbour-mode filtering can skip it.
/// <para>
/// Trust order (decisive at the first hit):
///   1. <c>navigation.state == "moored" / "anchored" / "aground"</c>
///      - moored regardless of speed or dwell.
///   2. <c>navigation.state ==</c> any "underway" / "sailing" /
///      "motoring" / "fishing" / "drifting" - NOT moored regardless of
///      speed (lets a sailboat ghost in light wind without being tagged).
///   3. SOG &lt; 1 kn for &gt;= 60 s straight - the legacy heuristic
///      that handles the typical case where a vessel doesn't publish
///      <c>navigation.state</c> at all.
/// </para>
/// <para>
/// Callers must invoke <see cref="Cleanup"/> periodically with the set
/// of currently visible AIS contexts; otherwise vessels that drop out
/// of AIS range would accumulate entries forever.
/// </para>
/// </summary>
public sealed class MooredVesselTracker : IMooredVesselTracker
{
    /// <summary>Below this SOG (m/s, ~1 kn) a vessel is treated as
    /// stopped. The cutoff bands slow-drifting anchored boats,
    /// fishing vessels jogging on station, and ferries waiting for a
    /// berth - the classic false-positive CPA sources.</summary>
    public const double MooredSpeedThresholdMs = 0.514;

    /// <summary>Dwell required before a stopped vessel is considered
    /// moored. 60 s is tight enough that anchored neighbours stop
    /// chattering the CPA alarm quickly, loose enough that a vessel
    /// briefly slowing through a turn or for a bridge doesn't get
    /// silently exempted from the projection.</summary>
    public const int MooredHoldSeconds = 60;

    // navigation.state classification. AIS message type 1/2/3 broadcasts
    // a numeric nav-status code; SK servers map it to a string. Values
    // here are the lower-case forms that AisVessel.Apply normalises to.
    private static readonly HashSet<string> _mooredStates = new(StringComparer.Ordinal)
    {
        "moored", "anchored", "aground",
        // Less common but unambiguous: a vessel "not under command"
        // that is also stationary by any reasonable definition.
        "not under command",
    };
    private static readonly HashSet<string> _underwayStates = new(StringComparer.Ordinal)
    {
        // The whole AIS-message-5 "Navigation Status" set that says
        // "I'm operating, not parked". Any of these bypasses the
        // SOG-dwell heuristic so a sailboat ghosting under 1 kn in
        // light wind isn't tagged moored after a minute.
        "sailing", "motoring", "fishing", "drifting",
        "under way", "under way using engine", "under way sailing",
        "restricted manoeuverability", "restricted maneuverability",
        "constrained by her draught", "engaged in fishing",
        "power-driven vessel towing astern", "power-driven vessel towing alongside",
    };

    private readonly Dictionary<string, DateTime> _lowSpeedSince = [];

    public bool IsMoored(AisVessel v, DateTime now)
    {
        // Authoritative SK signal trumps the heuristic when published.
        var navState = v.NavigationState;
        if (navState is not null)
        {
            if (_mooredStates.Contains(navState))
            {
                // Reset the dwell ring so a re-classification back to
                // underway via heuristic doesn't carry stale dwell data.
                _lowSpeedSince.Remove(v.Context);
                return true;
            }
            if (_underwayStates.Contains(navState))
            {
                _lowSpeedSince.Remove(v.Context);
                return false;
            }
            // Unknown nav state value - fall through to the heuristic.
        }

        string key = v.Context;
        bool slow = v.SpeedOverGround is not null && v.SpeedOverGround.Value < MooredSpeedThresholdMs;
        if (!slow)
        {
            _lowSpeedSince.Remove(key);
            return false;
        }
        if (!_lowSpeedSince.TryGetValue(key, out var since))
        {
            _lowSpeedSince[key] = now;
            return false;
        }
        return (now - since).TotalSeconds >= MooredHoldSeconds;
    }

    public void Cleanup(IReadOnlyCollection<string> activeContexts)
    {
        if (_lowSpeedSince.Count == 0) return;
        RemoveExcept(activeContexts.Contains);
    }

    public void Cleanup(IEnumerable<AisVessel> activeVessels)
    {
        // Hot-path overload: most ticks have no tracked dwellers, so we
        // can skip the HashSet build entirely. Only when something is
        // tracked do we materialise a context set to do O(1) lookups.
        if (_lowSpeedSince.Count == 0) return;
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in activeVessels) active.Add(v.Context);
        RemoveExcept(active.Contains);
    }

    /// <summary>Shared helper: drop every tracked entry whose key is
    /// NOT in the active set (per the supplied predicate). Lazily
    /// allocates the to-remove list only if a removal is actually
    /// needed - the steady-state "no churn" path stays zero-alloc.</summary>
    private void RemoveExcept(Func<string, bool> isActive)
    {
        List<string>? toRemove = null;
        foreach (var k in _lowSpeedSince.Keys)
        {
            if (isActive(k)) continue;
            (toRemove ??= []).Add(k);
        }
        if (toRemove is null) return;
        foreach (var k in toRemove) _lowSpeedSince.Remove(k);
    }

    /// <summary>Visible for tests.</summary>
    public int TrackedCount => _lowSpeedSince.Count;
}
