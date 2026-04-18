using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services;

public sealed class AlarmManager : IAlarmManager
{
    /// <summary>Minimum wall-clock gap between evaluations, to avoid churning
    /// on high-rate SignalK deltas.</summary>
    public const int EvaluationIntervalMs = 1_000;

    /// <summary>Wind-shift detection compares TWD to the value captured this
    /// many minutes ago.</summary>
    public const int WindShiftLookbackMinutes = 5;

    public const int SnoozeMinutes = 10;

    private readonly MooredVesselTracker _moored = new();
    private readonly Dictionary<string, DateTime> _snoozed = [];
    private readonly Func<DateTime> _now;

    private DateTime _lastEvaluation = DateTime.MinValue;
    private double? _windShiftAnchorDeg;
    private DateTime _windShiftAnchorAt = DateTime.MinValue;

    public AlarmInfo? ActiveAlarm { get; private set; }
    public int SnoozeDurationMinutes => SnoozeMinutes;

    public event Action<AlarmInfo?>? OnAlarmChanged;

    public AlarmManager() : this(() => DateTime.UtcNow) { }

    // Injectable clock for tests.
    internal AlarmManager(Func<DateTime> now)
    {
        _now = now;
    }

    public void Evaluate(NavigationData data, IReadOnlyCollection<AisVessel> vessels, IAppSettings settings)
    {
        var now = _now();
        if ((now - _lastEvaluation).TotalMilliseconds < EvaluationIntervalMs) return;
        _lastEvaluation = now;

        // Drop expired snooze entries before doing anything else. Otherwise a
        // vessel that was snoozed once and then dropped off AIS forever would
        // leave a dead entry in the dict until its Context was re-evaluated.
        SweepExpiredSnoozes(now);

        if (CheckDepth(data, settings)) return;
        CheckWindShift(data, settings, now);                 // not an early-return: warn, not danger
        if (CheckGuardZone(data, vessels, settings, now)) return;

        ClearResolved(data, settings);
    }

    private void SweepExpiredSnoozes(DateTime now)
    {
        if (_snoozed.Count == 0) return;
        var expired = _snoozed.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToList();
        foreach (var k in expired) _snoozed.Remove(k);
    }

    public Task DismissAsync()
    {
        if (ActiveAlarm is null) return Task.CompletedTask;
        ActiveAlarm = null;
        OnAlarmChanged?.Invoke(ActiveAlarm);
        return Task.CompletedTask;
    }

    public Task SnoozeActiveAsync()
    {
        if (ActiveAlarm?.TargetKey is null) return Task.CompletedTask;
        _snoozed[ActiveAlarm.TargetKey] = _now().AddMinutes(SnoozeMinutes);
        ActiveAlarm = null;
        OnAlarmChanged?.Invoke(ActiveAlarm);
        return Task.CompletedTask;
    }

    // --- rule implementations -----------------------------------------

    private bool CheckDepth(NavigationData data, IAppSettings settings)
    {
        if (data.Depth is null || data.Depth >= settings.DepthAlarmThreshold) return false;
        SetAlarm("SHALLOW",
            $"Depth {data.Depth:F1}m < {settings.DepthAlarmThreshold:F1}m",
            AlarmSeverity.Danger);
        return true;
    }

    private void CheckWindShift(NavigationData data, IAppSettings settings, DateTime now)
    {
        if (data.WindDirectionTrue is null) return;

        double twdDeg = data.WindDirectionTrue.Value * 180.0 / Math.PI;
        if (twdDeg < 0) twdDeg += 360;

        // First sample or anchor is older than the lookback window.
        if (_windShiftAnchorDeg is null
            || (now - _windShiftAnchorAt).TotalMinutes >= WindShiftLookbackMinutes)
        {
            if (_windShiftAnchorDeg is not null)
            {
                double shift = Math.Abs(twdDeg - _windShiftAnchorDeg.Value);
                if (shift > 180) shift = 360 - shift;
                if (shift > settings.WindShiftAlarmThreshold)
                {
                    SetAlarm("WIND SHIFT",
                        $"TWD shifted {shift:F0}\u00b0 in {WindShiftLookbackMinutes} min",
                        AlarmSeverity.Warn);
                }
            }
            _windShiftAnchorDeg = twdDeg;
            _windShiftAnchorAt = now;
        }
    }

    private bool CheckGuardZone(NavigationData data, IReadOnlyCollection<AisVessel> vessels,
        IAppSettings settings, DateTime now)
    {
        if (data.Latitude is null || data.Longitude is null
            || data.CourseOverGround is null || data.SpeedOverGround is null)
            return false;

        double cpaLimit = settings.CpaAlarmThreshold;
        double tcpaLimit = settings.GuardZoneLookaheadMinutes;

        // Evict moored-tracker state for vessels no longer in AIS range so
        // the dictionary does not grow without bound over long sessions.
        _moored.Cleanup(vessels.Select(v => v.Context).ToHashSet());

        foreach (var v in vessels)
        {
            if (v.Latitude is null || v.Longitude is null
                || v.CourseOverGround is null || v.SpeedOverGround is null)
                continue;

            if (v.IsBuddy) continue;                  // friends, not threats
            if (_moored.IsMoored(v, now)) continue;   // harbour tugs, anchored vessels
            if (IsSnoozed(v.Context, now)) continue;

            var cpa = Cpa.Compute(
                data.Latitude.Value, data.Longitude.Value,
                data.CourseOverGround, data.SpeedOverGround,
                v.Latitude.Value, v.Longitude.Value,
                v.CourseOverGround, v.SpeedOverGround);

            if (cpa is null) continue;
            if (cpa.Value.CpaNm >= cpaLimit) continue;
            if (cpa.Value.TcpaMin > tcpaLimit) continue;

            string name = v.Name ?? v.Mmsi ?? "vessel";
            SetAlarm("CPA",
                $"{name}: CPA {cpa.Value.CpaNm:F2}nm in {cpa.Value.TcpaMin:F0}min",
                AlarmSeverity.Danger,
                v.Context);
            return true;
        }
        return false;
    }

    private void ClearResolved(NavigationData data, IAppSettings settings)
    {
        if (ActiveAlarm is null) return;
        bool resolved = ActiveAlarm.Title switch
        {
            "SHALLOW" => data.Depth is null || data.Depth >= settings.DepthAlarmThreshold,
            "CPA" => true, // auto-clears once no vessel matches the loop above
            _ => false
        };
        if (resolved)
        {
            ActiveAlarm = null;
            OnAlarmChanged?.Invoke(ActiveAlarm);
        }
    }

    private bool IsSnoozed(string targetKey, DateTime now)
    {
        if (!_snoozed.TryGetValue(targetKey, out var until)) return false;
        if (now < until) return true;
        _snoozed.Remove(targetKey);
        return false;
    }

    private void SetAlarm(string title, string message, AlarmSeverity severity, string? targetKey = null)
    {
        // Same target already showing - update the message in place, don't
        // fire OnAlarmChanged for a severity/title change so audio doesn't
        // re-arm on every tick.
        if (ActiveAlarm?.Title == title && ActiveAlarm.TargetKey == targetKey)
        {
            if (ActiveAlarm.Message != message)
            {
                ActiveAlarm = ActiveAlarm with { Message = message };
                OnAlarmChanged?.Invoke(ActiveAlarm);
            }
            return;
        }
        ActiveAlarm = new AlarmInfo(title, message, severity, targetKey);
        OnAlarmChanged?.Invoke(ActiveAlarm);
    }
}
