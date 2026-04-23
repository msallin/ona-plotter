using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Tracks how long each AIS vessel has held near-zero speed. Once the dwell
/// crosses <see cref="MooredHoldSeconds"/> the vessel is considered moored
/// (harbour tug, anchored fishing boat, ferry holding for a berth) and alarm
/// logic can skip it.
/// <para>
/// Callers must invoke <see cref="Cleanup"/> periodically with the set of
/// currently visible AIS contexts; otherwise vessels that drop out of AIS
/// range would accumulate entries forever.
/// </para>
/// </summary>
public sealed class MooredVesselTracker
{
    /// <summary>Below this SOG (m/s, ~1 kn) a vessel is treated as
    /// stopped. The cutoff bands slow-drifting anchored boats,
    /// fishing vessels jogging on station, and ferries waiting for a
    /// berth -- the classic false-positive CPA sources.</summary>
    public const double MooredSpeedThresholdMs = 0.514;

    /// <summary>Dwell required before a stopped vessel is considered
    /// moored. 60 s is tight enough that anchored neighbours stop
    /// chattering the CPA alarm quickly, loose enough that a vessel
    /// briefly slowing through a turn or for a bridge doesn't get
    /// silently exempted from the projection.</summary>
    public const int MooredHoldSeconds = 60;

    private readonly Dictionary<string, DateTime> _lowSpeedSince = [];

    /// <summary>
    /// Returns true once the vessel has held low speed for MooredHoldSeconds.
    /// Transitions back to "moving" reset the clock.
    /// </summary>
    public bool IsMoored(AisVessel v, DateTime now)
    {
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

    /// <summary>Drops state for vessels no longer visible in AIS.</summary>
    public void Cleanup(IReadOnlyCollection<string> activeContexts)
    {
        if (_lowSpeedSince.Count == 0) return;
        var toRemove = _lowSpeedSince.Keys.Where(k => !activeContexts.Contains(k)).ToList();
        foreach (var k in toRemove) _lowSpeedSince.Remove(k);
    }

    /// <summary>Visible for tests.</summary>
    public int TrackedCount => _lowSpeedSince.Count;
}
