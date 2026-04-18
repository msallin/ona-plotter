using OnaPlotter.Models;

namespace OnaPlotter.Services;

public sealed class AlarmManager : IAlarmManager
{
    /// <summary>Minimum wall-clock gap between evaluations, to avoid churning
    /// on high-rate SignalK deltas.</summary>
    public const int EvaluationIntervalMs = 1_000;

    public const int SnoozeMinutes = 10;

    private readonly IReadOnlyList<IAlarmRule> _rules;
    private readonly Dictionary<string, DateTime> _snoozed = [];
    private readonly Func<DateTime> _now;

    private DateTime _lastEvaluation = DateTime.MinValue;
    private IAlarmRule? _activeRule;

    public AlarmInfo? ActiveAlarm { get; private set; }
    public int SnoozeDurationMinutes => SnoozeMinutes;

    public event Action<AlarmInfo?>? OnAlarmChanged;

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

        // Rules evaluate in priority order. First that returns a non-null
        // alarm wins and short-circuits the rest. Others are still given
        // the chance to update their internal state on later ticks.
        foreach (var rule in _rules)
        {
            var alarm = rule.Check(ctx);
            if (alarm is not null)
            {
                SetAlarm(alarm, rule);
                return;
            }
        }

        // Nothing matched this tick. Auto-clear if the last-active rule is
        // self-clearing (SHALLOW, CPA); latch rules (WIND SHIFT) keep the
        // banner until the user dismisses.
        if (ActiveAlarm is not null && (_activeRule?.AutoClear ?? true))
        {
            ActiveAlarm = null;
            _activeRule = null;
            OnAlarmChanged?.Invoke(null);
        }
    }

    public Task DismissAsync()
    {
        if (ActiveAlarm is null) return Task.CompletedTask;
        ActiveAlarm = null;
        _activeRule = null;
        OnAlarmChanged?.Invoke(null);
        return Task.CompletedTask;
    }

    public Task SnoozeActiveAsync()
    {
        if (ActiveAlarm?.TargetKey is null) return Task.CompletedTask;
        _snoozed[ActiveAlarm.TargetKey] = _now().AddMinutes(SnoozeMinutes);
        ActiveAlarm = null;
        _activeRule = null;
        OnAlarmChanged?.Invoke(null);
        return Task.CompletedTask;
    }

    private void SweepExpiredSnoozes(DateTime now)
    {
        if (_snoozed.Count == 0) return;
        var expired = _snoozed.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToList();
        foreach (var k in expired) _snoozed.Remove(k);
    }

    private bool IsSnoozed(string targetKey)
    {
        if (!_snoozed.TryGetValue(targetKey, out var until)) return false;
        if (_now() < until) return true;
        _snoozed.Remove(targetKey);
        return false;
    }

    private void SetAlarm(AlarmInfo next, IAlarmRule rule)
    {
        // Same rule + same target already showing - update the message in
        // place, don't fire OnAlarmChanged for a message-only update so
        // the audio driver doesn't re-arm on every tick.
        if (ActiveAlarm is not null
            && ActiveAlarm.Title == next.Title
            && ActiveAlarm.TargetKey == next.TargetKey)
        {
            if (ActiveAlarm.Message != next.Message)
            {
                ActiveAlarm = ActiveAlarm with { Message = next.Message };
                OnAlarmChanged?.Invoke(ActiveAlarm);
            }
            return;
        }
        ActiveAlarm = next;
        _activeRule = rule;
        OnAlarmChanged?.Invoke(ActiveAlarm);
    }
}
