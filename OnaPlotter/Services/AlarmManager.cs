using OnaPlotter.Models;

namespace OnaPlotter.Services;

public sealed class AlarmManager : IAlarmManager
{
    /// <summary>Minimum wall-clock gap between evaluations, to avoid churning
    /// on high-rate SignalK deltas.</summary>
    public const int EvaluationIntervalMs = 1_000;

    public const int SnoozeMinutes = 10;

    /// <summary>Hard cap on the banner stack size. Beyond this a lower-
    /// priority alarm is dropped; it will re-add on the next tick if still
    /// live. Keeps the banner from burying the viewport when everything
    /// goes wrong at once.</summary>
    public const int MaxActiveAlarms = 3;

    /// <summary>How many dismissed alarms to keep for the history drawer.
    /// Ring buffer, newest first. One screen-page of history is enough for
    /// a post-mortem without inflating memory on long watches.</summary>
    public const int MaxDismissedHistory = 20;

    private readonly IReadOnlyList<IAlarmRule> _rules;
    private readonly Dictionary<string, SnoozedTarget> _snoozed = [];
    private readonly Func<DateTime> _now;

    // Active alarms keyed by (Title, TargetKey) so the same rule firing on
    // a different target counts as a different alarm (two CPA threats, or
    // SHALLOW plus CPA coexisting). Value remembers the originating rule
    // so auto-clear / latch behaviour survives severity/message updates.
    private readonly Dictionary<AlarmKey, ActiveEntry> _active = [];
    private readonly List<DismissedAlarm> _history = [];

    private DateTime _lastEvaluation = DateTime.MinValue;
    private AlarmInfo? _lastTop;

    public AlarmInfo? ActiveAlarm => ActiveAlarms.Count > 0 ? ActiveAlarms[0] : null;

    public IReadOnlyList<AlarmInfo> ActiveAlarms => _active.Values
        .OrderByDescending(e => e.Info.Severity)
        .ThenBy(e => e.Rule.Priority)
        .Select(e => e.Info)
        .Take(MaxActiveAlarms)
        .ToList();

    public IReadOnlyList<SnoozedTarget> SnoozedTargets => _snoozed.Values
        .OrderBy(s => s.ExpiresAt)
        .ToList();

    public IReadOnlyList<DismissedAlarm> DismissedHistory => _history.AsReadOnly();

    public int SnoozeDurationMinutes => SnoozeMinutes;

    public event Action<AlarmInfo?>? OnAlarmChanged;
    public event Action? OnAlarmsChanged;

    public AlarmManager(IEnumerable<IAlarmRule> rules)
        : this(rules, () => DateTime.UtcNow) { }

    // Injectable clock for tests.
    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now)
    {
        _rules = rules.OrderBy(r => r.Priority).ToList();
        _now = now;
    }

    public void Evaluate(NavigationData data, IReadOnlyCollection<AisVessel> vessels, IAppSettings settings)
    {
        var now = _now();
        if ((now - _lastEvaluation).TotalMilliseconds < EvaluationIntervalMs) return;
        _lastEvaluation = now;

        SweepExpiredSnoozes(now);

        var ctx = new AlarmEvaluationContext(data, vessels, settings, now, IsSnoozed);

        // Every rule gets a chance to produce an alarm. Unlike the earlier
        // first-wins model, we collect all hits and stack them so a depth
        // warning doesn't hide a closing ferry (and vice versa). The
        // display order is severity-then-priority; the rules themselves
        // stay ignorant of the stack.
        var thisTick = new Dictionary<AlarmKey, (AlarmInfo info, IAlarmRule rule)>();
        foreach (var rule in _rules)
        {
            var alarm = rule.Check(ctx);
            if (alarm is null) continue;
            thisTick[new AlarmKey(alarm.Title, alarm.TargetKey)] = (alarm, rule);
        }

        bool changed = false;

        // Remove alarms that didn't fire this tick and whose rule wants
        // auto-clear. Latching rules (WIND SHIFT) stay in the stack until
        // the user dismisses them, matching the pre-refactor behaviour.
        var toDrop = _active
            .Where(kv => !thisTick.ContainsKey(kv.Key) && kv.Value.Rule.AutoClear)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in toDrop)
        {
            LogHistory(_active[key].Info, now, DismissReason.AutoCleared);
            _active.Remove(key);
            changed = true;
        }

        // Add-or-update from this tick's hits.
        foreach (var (key, (info, rule)) in thisTick)
        {
            if (_active.TryGetValue(key, out var existing))
            {
                // Same rule + same target. A severity change or a message
                // update should update the stored entry. Message-only
                // changes don't re-arm the audio, matching the
                // pre-refactor quiet-tick behaviour.
                if (existing.Info != info)
                {
                    _active[key] = new ActiveEntry(info, rule);
                    changed = true;
                }
            }
            else
            {
                _active[key] = new ActiveEntry(info, rule);
                changed = true;
            }
        }

        if (changed) FireAlarmsChanged();
    }

    public Task DismissAsync()
    {
        if (_active.Count == 0) return Task.CompletedTask;
        var now = _now();
        foreach (var e in _active.Values)
            LogHistory(e.Info, now, DismissReason.UserDismissed);
        _active.Clear();
        FireAlarmsChanged();
        return Task.CompletedTask;
    }

    public Task DismissAsync(AlarmInfo alarm)
    {
        var key = new AlarmKey(alarm.Title, alarm.TargetKey);
        if (!_active.Remove(key, out var removed)) return Task.CompletedTask;
        LogHistory(removed.Info, _now(), DismissReason.UserDismissed);
        FireAlarmsChanged();
        return Task.CompletedTask;
    }

    public Task SnoozeActiveAsync()
    {
        var top = ActiveAlarm;
        if (top is null) return Task.CompletedTask;
        return SnoozeAsync(top);
    }

    public Task SnoozeAsync(AlarmInfo alarm)
    {
        // Life-safety alarms (SART / MOB / EPIRB) refuse snooze even
        // if asked. Belt-and-braces with the UI hiding the button --
        // a programmatic caller or a rogue JS call shouldn't be able
        // to silence a beacon.
        if (!alarm.Snoozeable) return Task.CompletedTask;
        if (alarm.TargetKey is null) return Task.CompletedTask;
        var now = _now();
        var label = alarm.TargetLabel ?? alarm.TargetKey;
        _snoozed[alarm.TargetKey] = new SnoozedTarget(
            alarm.TargetKey, label, now.AddMinutes(SnoozeMinutes));

        // Remove every active alarm referring to this target, not just the
        // snoozed one - all CPA fields for "vessels.a" should go quiet
        // together.
        var toDrop = _active.Where(kv => kv.Key.TargetKey == alarm.TargetKey)
                            .Select(kv => kv.Key).ToList();
        foreach (var key in toDrop)
        {
            LogHistory(_active[key].Info, now, DismissReason.UserSnoozed);
            _active.Remove(key);
        }

        FireAlarmsChanged();
        return Task.CompletedTask;
    }

    public Task UnsnoozeAsync(string targetKey)
    {
        if (!_snoozed.Remove(targetKey)) return Task.CompletedTask;
        FireAlarmsChanged();
        return Task.CompletedTask;
    }

    private void LogHistory(AlarmInfo info, DateTime at, DismissReason reason)
    {
        _history.Insert(0, new DismissedAlarm(info, at, reason));
        if (_history.Count > MaxDismissedHistory)
            _history.RemoveAt(_history.Count - 1);
    }

    private void FireAlarmsChanged()
    {
        OnAlarmsChanged?.Invoke();

        // Audio-relevant change = top alarm identity (Title+TargetKey+Severity)
        // moved. Message-only updates on the same top alarm don't re-arm
        // the audio (the danger/warn cadence doesn't change).
        var top = ActiveAlarm;
        bool topChanged =
            (_lastTop is null) != (top is null)
            || (_lastTop is not null && top is not null && (
                _lastTop.Title != top.Title
                || _lastTop.TargetKey != top.TargetKey
                || _lastTop.Severity != top.Severity
                || _lastTop.Message != top.Message));

        if (topChanged)
        {
            _lastTop = top;
            OnAlarmChanged?.Invoke(top);
        }
    }

    private void SweepExpiredSnoozes(DateTime now)
    {
        if (_snoozed.Count == 0) return;
        var expired = _snoozed.Where(kv => now >= kv.Value.ExpiresAt).Select(kv => kv.Key).ToList();
        foreach (var k in expired) _snoozed.Remove(k);
    }

    private bool IsSnoozed(string targetKey)
    {
        if (!_snoozed.TryGetValue(targetKey, out var s)) return false;
        if (_now() < s.ExpiresAt) return true;
        _snoozed.Remove(targetKey);
        return false;
    }

    private readonly record struct AlarmKey(string Title, string? TargetKey);
    private readonly record struct ActiveEntry(AlarmInfo Info, IAlarmRule Rule);
}
