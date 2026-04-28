using OnaPlotter.Models;

namespace OnaPlotter.Services;

public sealed class AlarmManager : IAlarmManager
{
    /// <summary>Minimum wall-clock gap between evaluations, to avoid churning
    /// on high-rate SignalK deltas.</summary>
    public const int EvaluationIntervalMs = 1_000;

    /// <summary>Fallback snooze duration used when no IAppSettings is wired
    /// (legacy test ctors). Production callers override via IAppSettings
    /// so the user can set a shorter / longer snooze to match the
    /// passage style -- loitering fishing fleet vs. a distant freighter.</summary>
    public const int SnoozeMinutes = 10;

    /// <summary>After a dismiss, suppress re-firing the same (title, target)
    /// for this many seconds unless the severity escalates. Sized at 30s so
    /// a persistent threat re-asserts soon enough to matter, but the helm
    /// gets a chance to process / act / call on VHF without mashing dismiss
    /// seven times (which is what the CPA-at-0.02nm alarm log showed before
    /// this landed). Escalation (Warning -> Danger) bypasses the cooldown
    /// so we never hide a worsening situation.</summary>
    public const int DismissCooldownSeconds = 30;

    /// <summary>CPA-specific dismiss window. The default 30s is right
    /// for SHALLOW / depth where seconds matter, but for an AIS target
    /// the helm has already evaluated the meeting (called on VHF, made
    /// a deliberate course choice, or judged the geometry safe) and
    /// will be passed by the same ferry / freighter for several
    /// minutes more. Re-firing CPA every 30s in that window is just
    /// noise. 15 min is the request from helms running in the channel
    /// at Zürich where the same boat triggers half a dozen times on
    /// the same encounter. Escalation (Warning -> Danger) still
    /// bypasses, so a meaningfully worse geometry breaks through.</summary>
    public const int CpaDismissCooldownSeconds = 15 * 60;

    /// <summary>Hard cap on the banner stack size. Beyond this a lower-
    /// priority alarm is dropped; it will re-add on the next tick if still
    /// live. Keeps the banner from burying the viewport when everything
    /// goes wrong at once.</summary>
    public const int MaxActiveAlarms = 3;

    /// <summary>How many dismissed alarms to keep for the history drawer.
    /// Ring buffer, newest first. One screen-page of history is enough for
    /// a post-mortem without inflating memory on long watches.</summary>
    public const int MaxDismissedHistory = 20;

    /// <summary>KV key for the persisted snooze list. Versioned so a
    /// schema change (e.g. adding a per-rule snooze, or switching to a
    /// binary format) can be migrated cleanly.</summary>
    private const string SnoozeStorageKey = "alarmSnoozes.v1";

    private readonly IReadOnlyList<IAlarmRule> _rules;
    private readonly Dictionary<string, SnoozedTarget> _snoozed = [];
    private readonly Func<DateTime> _now;
    private readonly IKeyValueStore? _kv;
    private readonly IAppSettings? _settings;
    /// <summary>SignalK v2 notifications client. Null on the test
    /// constructors that don't care about server-side ack -- the
    /// dismiss path falls through to local-only behaviour. Wired to
    /// concrete <see cref="OnaPlotter.Services.Api.NotificationsApi"/>
    /// in production via DI.</summary>
    private readonly OnaPlotter.Services.Api.INotificationsApi? _notifications;
    private bool _initialized;

    // Active alarms keyed by (Title, TargetKey) so the same rule firing on
    // a different target counts as a different alarm (two CPA threats, or
    // SHALLOW plus CPA coexisting). Value remembers the originating rule
    // so auto-clear / latch behaviour survives severity/message updates.
    private readonly Dictionary<AlarmKey, ActiveEntry> _active = [];

    // Cached sort of _active. Invalidated on every mutation (Add/Remove/
    // Clear) and re-materialised lazily on read. The ActiveAlarms
    // property is hit from every Blazor render + every delta tick;
    // without this cache the 5-iterator LINQ chain allocates 6-7
    // objects per read at 10+ Hz.
    private IReadOnlyList<AlarmInfo>? _activeAlarmsCache;
    private readonly List<DismissedAlarm> _history = [];
    // Per-key cooldown window after a dismiss. Keyed the same as _active
    // so a CPA dismiss on "vessels.a" doesn't silence CPA on "vessels.b".
    // Stores both the expiry instant AND the severity at dismiss time so
    // an escalation (Warn -> Danger) can bypass the window.
    private readonly Dictionary<AlarmKey, DismissCooldown> _dismissCooldown = [];

    private DateTime _lastEvaluation = DateTime.MinValue;
    private AlarmInfo? _lastTop;

    public AlarmInfo? ActiveAlarm => ActiveAlarms.Count > 0 ? ActiveAlarms[0] : null;

    // Ordering:
    //   1. Severity descending (Danger before Warn)
    //   2. Time-to-event ascending (most imminent first); alarms with
    //      no TTI (null) sort last within the tier since a time-aware
    //      threat is more actionable than a latched notification
    //   3. Rule priority ascending as the tie-break
    // Effect: if SHALLOW (TTI=0) and CPA (TCPA=5min) both fire at
    // Danger severity, SHALLOW surfaces first because it's happening
    // now, independent of rule-priority numbers.
    public IReadOnlyList<AlarmInfo> ActiveAlarms
    {
        get
        {
            if (_activeAlarmsCache is not null) return _activeAlarmsCache;
            _activeAlarmsCache = _active.Values
                .OrderByDescending(e => e.Info.Severity)
                .ThenBy(e => e.Info.TimeToEventMinutes ?? double.MaxValue)
                .ThenBy(e => e.Rule.Priority)
                .Select(e => e.Info)
                .Take(MaxActiveAlarms)
                .ToList();
            return _activeAlarmsCache;
        }
    }

    /// <summary>Invalidate the sorted cache on every _active mutation.
    /// Called from Add/Remove/Clear call-sites below.</summary>
    private void InvalidateActiveCache() => _activeAlarmsCache = null;

    public int HiddenAlarmsCount => Math.Max(0, _active.Count - MaxActiveAlarms);

    public IReadOnlyList<SnoozedTarget> SnoozedTargets => _snoozed.Values
        .OrderBy(s => s.ExpiresAt)
        .ToList();

    /// <summary>
    /// Rules that are currently in a post-dismiss rearm window.
    /// Surfaces as small chips in the UI so the helm can see why a
    /// just-dismissed alarm isn't firing again right now. Currently
    /// only SHALLOW reports here; extensibility via IAlarmRule.
    /// GetRearmStatus so future rules (e.g. wind-shift cooldown) can
    /// contribute without changing this signature.
    /// </summary>
    public IReadOnlyList<AlarmRearmInfo> RearmStatuses(DateTime now) =>
        _rules
            .Select(r => r.GetRearmStatus(now))
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToList();

    public IReadOnlyList<DismissedAlarm> DismissedHistory => _history.AsReadOnly();

    public int SnoozeDurationMinutes =>
        // User-configurable via IAppSettings. Falls back to the 10-min
        // default when settings aren't wired (test ctors, DI ordering
        // race). Clamp at 1 min minimum -- zero would snooze forever.
        (_settings?.SnoozeDurationMinutes is int m && m > 0) ? m : SnoozeMinutes;

    public event Action<AlarmInfo?>? OnAlarmChanged;
    public event Action? OnAlarmsChanged;

    public AlarmManager(IEnumerable<IAlarmRule> rules, IKeyValueStore kv, IAppSettings settings,
        OnaPlotter.Services.Api.INotificationsApi notifications)
        : this(rules, () => DateTime.UtcNow, kv, settings, notifications) { }

    // Two-arg overload for tests that don't care about persistence.
    // Keeps the `args: [rules, now]` Activator.CreateInstance pattern
    // working.
    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now)
        : this(rules, now, null, null, null) { }

    // Three-arg overload kept so existing tests invoking Activator with
    // `(rules, now, kv)` still resolve. Defaults settings to null -> the
    // SnoozeDurationMinutes fallback applies.
    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now,
        IKeyValueStore? kv)
        : this(rules, now, kv, null, null) { }

    // Four-arg overload kept so existing tests invoking Activator with
    // `(rules, now, kv, settings)` still resolve. Defaults notifications
    // to null -> dismiss is local-only (no server-side ack POST).
    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now,
        IKeyValueStore? kv, IAppSettings? settings)
        : this(rules, now, kv, settings, null) { }

    // Full-arg internal ctor.
    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now,
        IKeyValueStore? kv, IAppSettings? settings,
        OnaPlotter.Services.Api.INotificationsApi? notifications)
    {
        _rules = rules.OrderBy(r => r.Priority).ToList();
        _now = now;
        _kv = kv;
        _settings = settings;
        _notifications = notifications;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (_kv is null) return;
        try
        {
            var raw = await _kv.GetAsync(SnoozeStorageKey);
            if (string.IsNullOrWhiteSpace(raw)) return;
            var loaded = System.Text.Json.JsonSerializer.Deserialize<SnoozedTarget[]>(raw);
            if (loaded is null) return;
            var now = _now();
            foreach (var s in loaded)
            {
                // Drop already-expired entries so they don't linger.
                if (s.ExpiresAt > now && !string.IsNullOrEmpty(s.TargetKey))
                    _snoozed[s.TargetKey] = s;
            }
            if (_snoozed.Count > 0) OnAlarmsChanged?.Invoke();
        }
        catch (Exception) { /* malformed JSON / storage issue - start empty */ }
    }

    private async Task PersistSnoozesAsync()
    {
        if (_kv is null) return;
        try
        {
            var arr = _snoozed.Values.ToArray();
            var json = System.Text.Json.JsonSerializer.Serialize(arr);
            await _kv.SetAsync(SnoozeStorageKey, json);
        }
        catch (Exception) { /* don't let storage hiccups surface as alarm-stack errors */ }
    }

    public void Evaluate(NavigationData data, IReadOnlyCollection<AisVessel> vessels, IAppSettings settings)
    {
        var now = _now();
        if ((now - _lastEvaluation).TotalMilliseconds < EvaluationIntervalMs) return;
        _lastEvaluation = now;

        SweepExpiredSnoozes(now);
        SweepExpiredDismissCooldowns(now);

        var ctx = new AlarmEvaluationContext(data, vessels, settings, now, IsSnoozed);

        // Every rule gets a chance to produce an alarm. Unlike the earlier
        // first-wins model, we collect all hits and stack them so a depth
        // warning doesn't hide a closing ferry (and vice versa). The
        // display order is severity-then-priority; the rules themselves
        // stay ignorant of the stack.
        var thisTick = new Dictionary<AlarmKey, (AlarmInfo info, IAlarmRule rule)>();
        foreach (var rule in _rules)
        {
            // CheckMany wraps Check by default (single rule -> one alarm),
            // so existing rules don't need to change. The
            // ServerNotificationsAlarmRule overrides it to surface every
            // active server notification on each tick.
            foreach (var alarm in rule.CheckMany(ctx))
            {
                thisTick[new AlarmKey(alarm.Title, alarm.TargetKey)] = (alarm, rule);
            }
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
        if (toDrop.Count > 0) InvalidateActiveCache();

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
                    InvalidateActiveCache();
                    changed = true;
                }
            }
            else
            {
                // New (or re-emerging) alarm. Check the dismiss-cooldown:
                // the helmsman already told us they saw this exact
                // (title, target); don't immediately re-fire, unless
                // severity has escalated since then.
                if (IsInDismissCooldown(key, info.Severity, now)) continue;
                _active[key] = new ActiveEntry(info, rule);
                InvalidateActiveCache();
                changed = true;
            }
        }

        if (changed) FireAlarmsChanged();
    }

    public Task DismissAsync()
    {
        if (_active.Count == 0) return Task.CompletedTask;
        var now = _now();
        var pendingAcks = new List<string>();
        foreach (var (key, e) in _active)
        {
            LogHistory(e.Info, now, DismissReason.UserDismissed);
            RecordDismissCooldown(key, e.Info.Severity, now);
            // Notify the owning rule so rule-specific rearm policies (e.g.
            // SHALLOW's 5 min non-shallow gate) can capture the dismissal.
            e.Rule.OnDismissed(e.Info, now);
            // Server-side ack for any v2-aware notification in the
            // batch. Collected here, fired below once we've cleared
            // the local state (so the delta echo from the server can't
            // race re-rendering an entry we're already dismissing).
            if (e.Info.NotificationId is string id && e.Info.CanAcknowledge)
                pendingAcks.Add(id);
        }
        _active.Clear();
        InvalidateActiveCache();
        FireAlarmsChanged();
        FireAcknowledge(pendingAcks);
        return Task.CompletedTask;
    }

    public Task DismissAsync(AlarmInfo alarm)
    {
        var key = new AlarmKey(alarm.Title, alarm.TargetKey);
        if (!_active.Remove(key, out var removed)) return Task.CompletedTask;
        InvalidateActiveCache();
        var now = _now();
        LogHistory(removed.Info, now, DismissReason.UserDismissed);
        RecordDismissCooldown(key, removed.Info.Severity, now);
        removed.Rule.OnDismissed(removed.Info, now);
        FireAlarmsChanged();
        // Cross-plotter sync: when a v2 server emitted this alarm and
        // declared canAcknowledge, dismiss-locally also POSTs the
        // acknowledge so other plotters see the ack via the next
        // delta echo.
        if (removed.Info.NotificationId is string id && removed.Info.CanAcknowledge)
            FireAcknowledge(new[] { id });
        return Task.CompletedTask;
    }

    /// <summary>Fire-and-forget POST to the v2 notifications API.
    /// Failures are silent: the local dismiss already happened, and a
    /// network blip on the ack POST shouldn't roll the UI back. The
    /// next delta tick reconciles state if the server didn't see our
    /// POST (ack just doesn't propagate; helm dismisses again).</summary>
    private void FireAcknowledge(IReadOnlyList<string> ids)
    {
        if (_notifications is null || ids.Count == 0) return;
        foreach (var id in ids)
        {
            // Discard the task; HttpClient handles retries / timeouts
            // at its own layer. NotificationsApi catches
            // HttpRequestException internally.
            _ = _notifications.AcknowledgeAsync(id);
        }
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
            alarm.TargetKey, label, now.AddMinutes(SnoozeDurationMinutes));

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
        if (toDrop.Count > 0) InvalidateActiveCache();

        // Snooze is stronger than dismiss-cooldown; drop any cooldowns
        // for this target so the UI state doesn't carry stale "I saw
        // this" records behind the longer snooze window.
        var coolKeys = _dismissCooldown.Keys
            .Where(k => k.TargetKey == alarm.TargetKey).ToList();
        foreach (var k in coolKeys) _dismissCooldown.Remove(k);

        FireAlarmsChanged();
        return PersistSnoozesAsync();
    }

    public Task UnsnoozeAsync(string targetKey)
    {
        if (!_snoozed.Remove(targetKey)) return Task.CompletedTask;
        FireAlarmsChanged();
        return PersistSnoozesAsync();
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
        if (expired.Count == 0) return;
        foreach (var k in expired) _snoozed.Remove(k);
        // Fire-and-forget: persist the shrunk list so a reload won't
        // see already-expired entries. Safe to race with other
        // PersistSnoozes calls because the write is full-replace.
        _ = PersistSnoozesAsync();
    }

    private bool IsSnoozed(string targetKey)
    {
        if (!_snoozed.TryGetValue(targetKey, out var s)) return false;
        if (_now() < s.ExpiresAt) return true;
        _snoozed.Remove(targetKey);
        return false;
    }

    private void RecordDismissCooldown(AlarmKey key, AlarmSeverity severity, DateTime now)
    {
        // Per-title window: CPA gets 15 min instead of the default 30s
        // (see CpaDismissCooldownSeconds for rationale). Other titles
        // keep the default; add cases here as more long-cycle threats
        // surface a similar "dismissing every 30s is noise" pattern.
        int seconds = key.Title == "CPA" ? CpaDismissCooldownSeconds : DismissCooldownSeconds;
        _dismissCooldown[key] = new DismissCooldown(
            now.AddSeconds(seconds), severity);
    }

    /// <summary>True if the alarm should be suppressed this tick. Cooldown
    /// silences re-fires of the same (title, target) at the same or lower
    /// severity; an escalation (Warning -> Danger) bypasses because the
    /// helmsman's earlier acknowledgment doesn't cover the worsened case.</summary>
    private bool IsInDismissCooldown(AlarmKey key, AlarmSeverity newSeverity, DateTime now)
    {
        if (!_dismissCooldown.TryGetValue(key, out var c)) return false;
        if (now >= c.Until)
        {
            _dismissCooldown.Remove(key);
            return false;
        }
        // Escalation breaks the cooldown. Equal severity is still suppressed
        // -- the helmsman already acknowledged this exposure.
        return newSeverity <= c.DismissedAtSeverity;
    }

    private void SweepExpiredDismissCooldowns(DateTime now)
    {
        if (_dismissCooldown.Count == 0) return;
        var expired = _dismissCooldown.Where(kv => now >= kv.Value.Until)
                                      .Select(kv => kv.Key).ToList();
        foreach (var k in expired) _dismissCooldown.Remove(k);
    }

    private readonly record struct AlarmKey(string Title, string? TargetKey);
    private readonly record struct ActiveEntry(AlarmInfo Info, IAlarmRule Rule);
    private readonly record struct DismissCooldown(DateTime Until, AlarmSeverity DismissedAtSeverity);
}
