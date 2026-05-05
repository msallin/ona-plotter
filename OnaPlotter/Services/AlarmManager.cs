using OnaPlotter.Models;
using OnaPlotter.Services.Json;

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
    // Cached uncapped variant for cross-plotter publishers and any other
    // downstream that needs the FULL active set (not capped at
    // MaxActiveAlarms like the UI banner). Same invalidation contract as
    // _activeAlarmsCache; populated from the same sorted projection minus
    // the Take().
    private IReadOnlyList<AlarmInfo>? _allActiveAlarmsCache;
    // Paired (info, rule) view of the same uncapped set so publishers
    // can ask each rule for its IAlarmRule.GetPublishPath mapping
    // without reverse-engineering it from the title. Same invalidation.
    private IReadOnlyList<(AlarmInfo Info, IAlarmRule Rule)>? _allActiveEntriesCache;
    // Sorted-entries cache shared across the three public sorted-active
    // readers above. SortedEntries() materialises this once; the
    // per-property caches are then materialised from it in O(N) without
    // re-sorting. Same invalidation contract as the per-property caches.
    private ActiveEntry[]? _sortedEntriesCache;
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
            var sorted = SortedEntries();
            int n = Math.Min(sorted.Length, MaxActiveAlarms);
            var result = new AlarmInfo[n];
            for (int i = 0; i < n; i++) result[i] = sorted[i].Info;
            return _activeAlarmsCache = result;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<AlarmInfo> AllActiveAlarms
    {
        get
        {
            if (_allActiveAlarmsCache is not null) return _allActiveAlarmsCache;
            var sorted = SortedEntries();
            var result = new AlarmInfo[sorted.Length];
            for (int i = 0; i < sorted.Length; i++) result[i] = sorted[i].Info;
            return _allActiveAlarmsCache = result;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<(AlarmInfo Info, IAlarmRule Rule)> AllActiveEntries
    {
        get
        {
            if (_allActiveEntriesCache is not null) return _allActiveEntriesCache;
            var sorted = SortedEntries();
            var result = new (AlarmInfo, IAlarmRule)[sorted.Length];
            for (int i = 0; i < sorted.Length; i++) result[i] = (sorted[i].Info, sorted[i].Rule);
            return _allActiveEntriesCache = result;
        }
    }

    /// <summary>Single-pass sort over <c>_active.Values</c> shared by
    /// every public sorted-active reader. Replaces a triple-OrderBy
    /// LINQ chain (3 enumerator-class allocations + a deferred List
    /// allocation per read) with one allocation + one Array.Sort call.
    /// Tie-break order matches the previous LINQ semantics:
    /// severity desc, time-to-event asc (null = +infinity), priority
    /// asc. Caller materialises into the per-property shape.</summary>
    private ActiveEntry[] SortedEntries()
    {
        if (_sortedEntriesCache is not null) return _sortedEntriesCache;
        int n = _active.Count;
        if (n == 0) return _sortedEntriesCache = [];
        var arr = new ActiveEntry[n];
        int i = 0;
        foreach (var v in _active.Values) arr[i++] = v;
        Array.Sort(arr, static (a, b) =>
        {
            // Severity is an enum where higher ordinal = more urgent;
            // we want most-urgent first -> compare b vs a.
            int s = b.Info.Severity.CompareTo(a.Info.Severity);
            if (s != 0) return s;
            double aTte = a.Info.TimeToEventMinutes ?? double.MaxValue;
            double bTte = b.Info.TimeToEventMinutes ?? double.MaxValue;
            int t = aTte.CompareTo(bTte);
            if (t != 0) return t;
            return a.Rule.Priority.CompareTo(b.Rule.Priority);
        });
        return _sortedEntriesCache = arr;
    }

    /// <summary>Invalidate the sorted caches on every _active mutation.
    /// Called from Add/Remove/Clear call-sites below.</summary>
    private void InvalidateActiveCache()
    {
        _activeAlarmsCache = null;
        _allActiveAlarmsCache = null;
        _allActiveEntriesCache = null;
        _sortedEntriesCache = null;
    }

    public int HiddenAlarmsCount => Math.Max(0, _active.Count - MaxActiveAlarms);

    public IReadOnlyList<SnoozedTarget> SnoozedTargets
    {
        get
        {
            // HUD reads this every render. The typical case is empty
            // or 1-3 entries; LINQ would still allocate enumerator +
            // List even for size 0. Manual path: early-return on
            // empty, copy + Array.Sort otherwise (in-place, single
            // allocation sized exactly to the entry count).
            int n = _snoozed.Count;
            if (n == 0) return [];
            var arr = new SnoozedTarget[n];
            int i = 0;
            foreach (var v in _snoozed.Values) arr[i++] = v;
            Array.Sort(arr, static (a, b) => a.ExpiresAt.CompareTo(b.ExpiresAt));
            return arr;
        }
    }

    /// <summary>
    /// Rules that are currently in a post-dismiss rearm window.
    /// Surfaces as small chips in the UI so the helm can see why a
    /// just-dismissed alarm isn't firing again right now. Currently
    /// only SHALLOW reports here; extensibility via IAlarmRule.
    /// GetRearmStatus so future rules (e.g. wind-shift cooldown) can
    /// contribute without changing this signature.
    /// </summary>
    public IReadOnlyList<AlarmRearmInfo> RearmStatuses(DateTime now)
    {
        // HUD reads this every render. Today only SHALLOW returns
        // a non-null status, so the typical result is 0 entries --
        // the LINQ chain would still allocate two enumerators + a
        // List for the empty case. Lazy-allocate the result list
        // on the first hit; null + a small List<>(2) is much
        // cheaper than three enumerators per render.
        List<AlarmRearmInfo>? results = null;
        foreach (var r in _rules)
        {
            var status = r.GetRearmStatus(now);
            if (status is { } s)
            {
                (results ??= new List<AlarmRearmInfo>(2)).Add(s);
            }
        }
        return results ?? (IReadOnlyList<AlarmRearmInfo>)[];
    }

    public IReadOnlyList<DismissedAlarm> DismissedHistory => _history.AsReadOnly();

    public int SnoozeDurationMinutes =>
        // User-configurable via IAppSettings. Falls back to the 10-min
        // default when settings aren't wired (test ctors, DI ordering
        // race). Clamp at 1 min minimum -- zero would snooze forever.
        (_settings?.SnoozeDurationMinutes is int m && m > 0) ? m : SnoozeMinutes;

    public event Action<AlarmInfo?>? OnAlarmChanged;
    public event Action? OnAlarmsChanged;

    public AlarmManager(IEnumerable<IAlarmRule> rules, IKeyValueStore kv, IAppSettings settings)
        : this(rules, () => DateTime.UtcNow, kv, settings) { }

    // Two-arg overload for tests that don't care about persistence.
    // Production wiring uses the public 3-arg ctor above; this and
    // the 3-arg internal sibling below let the test fixtures call
    // `new AlarmManager(rules, () => clock.Now)` (and `+ kv`) without
    // having to thread a settings object through every test.
    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now)
        : this(rules, now, null, null) { }

    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now,
        IKeyValueStore? kv)
        : this(rules, now, kv, null) { }

    // Full-arg internal ctor. Cross-plotter ack used to inject an
    // INotificationsApi here; that responsibility now lives on the
    // per-alarm IAlarmAcknowledger handle that the bridge rule attaches
    // to AlarmInfo, so the manager no longer depends on the SignalK
    // REST surface at all. Removing the API param simplifies the
    // construction story (one less field, one less ctor overload) and
    // matches ARCH-001's goal of keeping the manager transport-neutral.
    internal AlarmManager(IEnumerable<IAlarmRule> rules, Func<DateTime> now,
        IKeyValueStore? kv, IAppSettings? settings)
    {
        _rules = rules.OrderBy(r => r.Priority).ToList();
        _now = now;
        _kv = kv;
        _settings = settings;
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
            var loaded = System.Text.Json.JsonSerializer.Deserialize(raw, OnaJsonContext.Default.SnoozedTargetArray);
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
        // Narrow catches surface OOM / thread-aborts to the global
        // boundary instead of swallowing them; corrupt storage and
        // missing JS interop both fall back to "start empty" without
        // taking down the alarm pipeline.
        catch (System.Text.Json.JsonException ex)
        {
            Console.WriteLine($"[alarm.snooze] hydrate skipped: malformed json: {ex.Message}");
        }
        catch (Microsoft.JSInterop.JSException ex)
        {
            Console.WriteLine($"[alarm.snooze] hydrate skipped: localStorage unavailable: {ex.Message}");
        }
        catch (Microsoft.JSInterop.JSDisconnectedException) { /* page tear-down race */ }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[alarm.snooze] hydrate skipped: {ex.Message}");
        }
    }

    private async Task PersistSnoozesAsync()
    {
        if (_kv is null) return;
        try
        {
            var arr = _snoozed.Values.ToArray();
            var json = System.Text.Json.JsonSerializer.Serialize(arr, OnaJsonContext.Default.SnoozedTargetArray);
            await _kv.SetAsync(SnoozeStorageKey, json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.WriteLine($"[alarm.snooze] persist skipped: serialize: {ex.Message}");
        }
        catch (Microsoft.JSInterop.JSException ex)
        {
            Console.WriteLine($"[alarm.snooze] persist skipped: localStorage: {ex.Message}");
        }
        catch (Microsoft.JSInterop.JSDisconnectedException) { /* page tear-down */ }
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
        // The previous Where + Select + ToList allocated an enumerator
        // chain + a List per Evaluate tick (every alarm evaluation,
        // multi-Hz under bursty SK feeds), even when no alarms were
        // up. Defer the list allocation to the case where something
        // actually needs dropping.
        List<AlarmKey>? toDrop = null;
        foreach (var kv in _active)
        {
            if (!thisTick.ContainsKey(kv.Key) && kv.Value.Rule.AutoClear)
            {
                toDrop ??= new List<AlarmKey>(2);
                toDrop.Add(kv.Key);
            }
        }
        if (toDrop is not null)
        {
            foreach (var key in toDrop)
            {
                LogHistory(_active[key].Info, now, DismissReason.AutoCleared);
                _active.Remove(key);
                changed = true;
            }
            InvalidateActiveCache();
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
        var pendingAcks = new List<IAlarmAcknowledger>();
        foreach (var (key, e) in _active)
        {
            LogHistory(e.Info, now, DismissReason.UserDismissed);
            RecordDismissCooldown(key, e.Info.Severity, now);
            // Notify the owning rule so rule-specific rearm policies (e.g.
            // SHALLOW's 5 min non-shallow gate) can capture the dismissal.
            e.Rule.OnDismissed(e.Info, now);
            // Cross-plotter ack: the source-specific Acknowledger handle
            // owns the dispatch (SignalK v2 -> POST /notifications/{id}/
            // acknowledge today; future transports add their own impl).
            // Collected here, fired below once local state is clear so
            // the server's delta echo can't race re-rendering.
            if (e.Info.Acknowledger is { CanAcknowledge: true } ack)
                pendingAcks.Add(ack);
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
        // Cross-plotter ack via the per-alarm acknowledger handle.
        // Pre-Phase A this directly inspected NotificationId+CanAcknowledge
        // on the alarm; the abstraction now keeps the manager transport-
        // neutral (ARCH-001).
        if (removed.Info.Acknowledger is { CanAcknowledge: true } ack)
            FireAcknowledge(new[] { ack });
        return Task.CompletedTask;
    }

    /// <summary>Fire-and-forget invoke of each acknowledger handle.
    /// Failures are silent at this layer: the local dismiss already
    /// happened, and a network blip on the ack shouldn't roll the UI
    /// back. The acknowledger implementation owns its own logging /
    /// retry policy (e.g. SignalKNotificationAcknowledger forwards to
    /// the per-call-timeout NotificationsApi). The next delta tick
    /// reconciles state if the ack didn't propagate.</summary>
    private static void FireAcknowledge(IReadOnlyList<IAlarmAcknowledger> acks)
    {
        if (acks.Count == 0) return;
        foreach (var ack in acks)
        {
            // Discard the task; transport-specific failure handling
            // belongs to the acknowledger.
            _ = ack.AcknowledgeAsync();
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
        // together. Manual scan to avoid the LINQ enumerator + List
        // alloc on a path the helm hits routinely (one snooze tap
        // per alarm). Preserves the original no-cleanup-when-empty
        // semantics: when no active alarms match, the rest of the
        // method (cooldown drop, FireAlarmsChanged, persistence)
        // still runs -- the snooze itself is the meaningful change.
        List<AlarmKey>? toDrop = null;
        foreach (var kv in _active)
        {
            if (kv.Key.TargetKey == alarm.TargetKey)
            {
                toDrop ??= new List<AlarmKey>(2);
                toDrop.Add(kv.Key);
            }
        }
        if (toDrop is not null)
        {
            foreach (var key in toDrop)
            {
                LogHistory(_active[key].Info, now, DismissReason.UserSnoozed);
                _active.Remove(key);
            }
            InvalidateActiveCache();
        }

        // Snooze is stronger than dismiss-cooldown; drop any cooldowns
        // for this target so the UI state doesn't carry stale "I saw
        // this" records behind the longer snooze window. Same lazy-
        // alloc pattern as toDrop above.
        List<AlarmKey>? coolKeys = null;
        foreach (var k in _dismissCooldown.Keys)
        {
            if (k.TargetKey == alarm.TargetKey)
            {
                coolKeys ??= new List<AlarmKey>(2);
                coolKeys.Add(k);
            }
        }
        if (coolKeys is not null)
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
        // Manual loop + lazy-allocated list so the per-Tick sweep
        // doesn't allocate an enumerator chain + List<> when no
        // entries have expired (the typical case once an active
        // snooze is in place: the user sets it for an hour, the
        // sweep runs every tick of that hour and finds nothing).
        List<string>? expired = null;
        foreach (var kv in _snoozed)
        {
            if (now < kv.Value.ExpiresAt) continue;
            (expired ??= new List<string>(2)).Add(kv.Key);
        }
        if (expired is null) return;
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
        // Same shape as SweepExpiredSnoozes -- manual walk +
        // lazy-allocated list. Cooldowns sit in the dict for
        // CpaDismissCooldownSeconds (15 min on CPA) or 30 s
        // on others, so most sweeps during the cooldown find
        // nothing expired.
        List<AlarmKey>? expired = null;
        foreach (var kv in _dismissCooldown)
        {
            if (now < kv.Value.Until) continue;
            (expired ??= new List<AlarmKey>(2)).Add(kv.Key);
        }
        if (expired is null) return;
        foreach (var k in expired) _dismissCooldown.Remove(k);
    }

    private readonly record struct AlarmKey(string Title, string? TargetKey);
    private readonly record struct ActiveEntry(AlarmInfo Info, IAlarmRule Rule);
    private readonly record struct DismissCooldown(DateTime Until, AlarmSeverity DismissedAtSeverity);
}
