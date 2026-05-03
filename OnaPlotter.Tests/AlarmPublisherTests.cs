using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the cross-plotter publish flow added in Phase B. Tests use a
/// fake <see cref="INotificationsApi"/> + a stub
/// <see cref="IAlarmManager"/> rather than driving the full alarm
/// pipeline so the assertions focus on the publisher's behaviour
/// (raise on add, clear on remove, skip server-emitted echoes, retry
/// on failure, etc.) without entangling the rule + manager logic.
/// The end-to-end "alarm on plotter A surfaces as a banner on
/// plotter B" lives in <see cref="MultiInstancePlotterTests"/>.
/// </summary>
public class AlarmPublisherTests
{
    /// <summary>Stub manager that exposes a mutable active list and
    /// fires <c>OnAlarmsChanged</c> on demand. Lets tests script the
    /// state transitions the publisher reacts to without needing the
    /// real AlarmManager + a rule list to pull a particular alarm
    /// shape into existence.</summary>
    private sealed class StubAlarmManager : IAlarmManager
    {
        private List<AlarmInfo> _active = [];
        // Optional rule pairing for AllActiveEntries. Defaults to a
        // title-based mapping that mirrors the production rules'
        // GetPublishPath overrides so existing publisher tests assert
        // the same paths they did pre-ARCH-002. Tests that need
        // null-path or different paths can override via RuleFor.
        private Func<AlarmInfo, IAlarmRule> _ruleFor = DefaultRuleFor;

        private static IAlarmRule DefaultRuleFor(AlarmInfo a)
        {
            // Mirror the production rules' published paths. Mapping
            // matches each rule's IAlarmRule.GetPublishPath override
            // so the publisher tests assert the same paths the live
            // rules emit.
            string? path = a.Title switch
            {
                "SHALLOW" => "notifications.environment.depth.belowSurface",
                "CPA" when !string.IsNullOrEmpty(a.TargetKey)
                    => $"notifications.security.collision.{Sanitise(a.TargetKey!)}",
                "WIND SHIFT" => "notifications.environment.wind.shift",
                "ANCHOR DRAG" => "notifications.navigation.anchor.dragging",
                "ANCHOR TIDE" => "notifications.navigation.anchor.tide",
                "DEADMAN" => "notifications.helm.deadman",
                _ => null,
            };
            return new PassThroughRule(path);

            static string Sanitise(string targetKey)
            {
                var suffix = targetKey.StartsWith("vessels.", StringComparison.Ordinal)
                    ? targetKey["vessels.".Length..] : targetKey;
                var sb = new System.Text.StringBuilder(suffix.Length);
                foreach (var c in suffix)
                    sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
                return sb.ToString();
            }
        }

        public AlarmInfo? ActiveAlarm => _active.Count > 0 ? _active[0] : null;
        public IReadOnlyList<AlarmInfo> ActiveAlarms => _active;
        // Stub uses the same list for both: pile-up beyond
        // MaxActiveAlarms is exercised by the OPS-003 regression test
        // which inserts 4+ alarms and asserts the publisher reads from
        // the uncapped collection.
        public IReadOnlyList<AlarmInfo> AllActiveAlarms => _active;
        public IReadOnlyList<(AlarmInfo Info, IAlarmRule Rule)> AllActiveEntries =>
            _active.Select(a => (a, _ruleFor(a))).ToList();
        public int HiddenAlarmsCount => 0;
        public IReadOnlyList<SnoozedTarget> SnoozedTargets => [];
        public IReadOnlyList<DismissedAlarm> DismissedHistory => [];
        public int SnoozeDurationMinutes => 10;
        public IReadOnlyList<AlarmRearmInfo> RearmStatuses(DateTime now) => [];
        public event Action<AlarmInfo?>? OnAlarmChanged;
        public event Action? OnAlarmsChanged;

        /// <summary>Override the rule pairing -- e.g. to return null
        /// from GetPublishPath for some alarms.</summary>
        public void RuleFor(Func<AlarmInfo, IAlarmRule> selector)
        {
            _ruleFor = selector;
        }

        public void Set(params AlarmInfo[] alarms)
        {
            _active = alarms.ToList();
            OnAlarmsChanged?.Invoke();
            OnAlarmChanged?.Invoke(ActiveAlarm);
        }

        public void Evaluate(NavigationData data, IReadOnlyCollection<AisVessel> vessels, IAppSettings settings) { }
        public Task DismissAsync() { Set([]); return Task.CompletedTask; }
        public Task DismissAsync(AlarmInfo a)
        {
            _active.RemoveAll(x => x.Title == a.Title && x.TargetKey == a.TargetKey);
            OnAlarmsChanged?.Invoke();
            return Task.CompletedTask;
        }
        public Task SnoozeActiveAsync() => Task.CompletedTask;
        public Task SnoozeAsync(AlarmInfo a) => Task.CompletedTask;
        public Task UnsnoozeAsync(string targetKey) => Task.CompletedTask;
        public Task InitializeAsync() => Task.CompletedTask;
    }

    /// <summary>Stub rule used by StubAlarmManager.AllActiveEntries.
    /// Has a configurable PublishPath so tests can drive the publisher
    /// down both branches of the GetPublishPath check.</summary>
    private sealed class PassThroughRule : IAlarmRule
    {
        private readonly string? _publishPath;
        public PassThroughRule(string? publishPath = null) { _publishPath = publishPath; }
        public string Title => "_STUB_";
        public int Priority => 999;
        public bool AutoClear => true;
        public AlarmInfo? Check(AlarmEvaluationContext ctx) => null;
        public string? GetPublishPath(AlarmInfo alarm) => _publishPath;
    }

    private sealed class FakeApi : INotificationsApi
    {
        public List<(string Path, NotificationPayload Body)> Raised { get; } = [];
        public List<string> Cleared { get; } = [];
        public Func<string, NotificationPayload, ApiResult<string>>? RaiseHandler { get; set; }
        public Func<string, NotificationPayload, Task<ApiResult<string>>>? AsyncRaiseHandler { get; set; }

        /// <summary>Fires once for every <see cref="ClearAsync"/> call.
        /// Lets tests await a clear deterministically instead of polling
        /// the <see cref="Cleared"/> list with sleeps. Subscribers that
        /// only care about a specific id filter on <c>e</c> in their
        /// handler.</summary>
        public event Action<string>? OnCleared;

        public Task<ApiResult> AcknowledgeAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> SilenceAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default)
        {
            Raised.Add((path, body));
            // Async handler wins when set; lets tests defer completion
            // via TaskCompletionSource without blocking the calling
            // thread synchronously (which would deadlock the test
            // because Blazor WASM is single-threaded).
            if (AsyncRaiseHandler is not null) return AsyncRaiseHandler(path, body);
            var result = RaiseHandler?.Invoke(path, body)
                ?? ApiResult<string>.Ok($"id-{Raised.Count}");
            return Task.FromResult(result);
        }
        public Task<ApiResult> ClearAsync(string id, CancellationToken ct = default)
        {
            Cleared.Add(id);
            OnCleared?.Invoke(id);
            return Task.FromResult(ApiResult.Ok);
        }

        // MOB-specific verbs: not exercised by AlarmPublisher tests
        // (the publisher only uses RaiseAsync / ClearAsync for path-
        // keyed notifications). Stubbed for interface compliance.
        public Task<ApiResult<string>> RaiseMobAsync(string? message, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok("mob-stub"));
        public Task<ApiResult> ClearByActionAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<IReadOnlyDictionary<string, ServerNotificationDto>?> ListActiveAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, ServerNotificationDto>?>(null);
    }

    /// <summary>Stub acknowledger used to mark an AlarmInfo as
    /// "originated from a remote source" (the publisher checks
    /// Acknowledger != null to skip publishing). Counts invocations
    /// so tests can assert no-op when no dismiss happens.</summary>
    private sealed class StubAcknowledger : IAlarmAcknowledger
    {
        public StubAcknowledger(bool canAcknowledge = true)
        {
            CanAcknowledge = canAcknowledge;
        }
        public bool CanAcknowledge { get; }
        public int AcknowledgeCount { get; private set; }
        public Task AcknowledgeAsync()
        {
            AcknowledgeCount++;
            return Task.CompletedTask;
        }
    }

    // -----------------------------------------------------------------
    // Title -> Path mapping. Pin every supported title so a refactor
    // that drops a case fails the build instead of silently making the
    // alarm un-publishable.
    // -----------------------------------------------------------------

    // Path-mapping was previously a centralised switch on
    // AlarmPublisher.TryMapToPath. After ARCH-002 each rule owns its
    // own GetPublishPath; the per-rule unit coverage moved to
    // AlarmRulePublishPathTests.cs. These tests stay focused on the
    // publisher's diff/raise/clear behaviour given a rule that returns
    // some path.

    // -----------------------------------------------------------------
    // Diff-based reaction to OnAlarmsChanged.
    // -----------------------------------------------------------------

    [Test]
    public async Task NewLocalAlarm_FiresRaiseAndOwnsThePath()
    {
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        mgr.Set(new AlarmInfo("SHALLOW", "Depth 1.8m", AlarmSeverity.Danger));
        // Yield to give the fire-and-forget RaiseAsync time to complete.
        await Task.Yield();

        await Assert.That(api.Raised.Count).IsEqualTo(1);
        await Assert.That(api.Raised[0].Path).IsEqualTo("notifications.environment.depth.belowSurface");
        await Assert.That(api.Raised[0].Body.State).IsEqualTo("alarm");
        await Assert.That(api.Raised[0].Body.Method).Contains("sound");
        await Assert.That(api.Raised[0].Body.Message).IsEqualTo("Depth 1.8m");
        await Assert.That(tracker.IsOwnedPath("notifications.environment.depth.belowSurface")).IsTrue();
    }

    [Test]
    public async Task ClearedAlarm_FiresClearAndReleasesOwnership()
    {
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        mgr.Set(new AlarmInfo("SHALLOW", "Depth 1.8m", AlarmSeverity.Danger));
        await Task.Yield();
        await Assert.That(api.Raised.Count).IsEqualTo(1);
        var raisedId = "id-1";

        // Local rule stops firing -- alarm leaves active set.
        mgr.Set();
        await Task.Yield();

        await Assert.That(api.Cleared).Contains(raisedId);
        await Assert.That(tracker.IsOwnedPath("notifications.environment.depth.belowSurface")).IsFalse();
    }

    [Test]
    public async Task IdempotentRaise_DoesNotDoubleFireOnRepeatedEvents()
    {
        // Same alarm staying active across multiple Evaluate ticks
        // generates multiple OnAlarmsChanged events (e.g. message
        // updates as the depth fluctuates). The publisher must NOT
        // re-fire a raise -- the SK server is idempotent on path+
        // $source overlay, but the network call is wasted work and
        // creates extra wire traffic on a flaky link.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        var alarm = new AlarmInfo("SHALLOW", "Depth 1.8m", AlarmSeverity.Danger);
        mgr.Set(alarm);
        await Task.Yield();
        mgr.Set(alarm);    // re-render, same alarm
        mgr.Set(alarm);    // again
        await Task.Yield();

        await Assert.That(api.Raised.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ServerEmittedAlarm_NotRepublished()
    {
        // ServerNotificationsAlarmRule emits AlarmInfo with an
        // Acknowledger set. Republishing would loop -- we'd POST
        // /notifications, server emits delta, our store applies,
        // bridge rule emits, we'd POST again. The publisher must
        // recognise these as "already from the server" via the
        // presence of the Acknowledger handle and skip.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        // Synthetic acknowledger marks the alarm as server-sourced;
        // the actual ack call is irrelevant here -- we're testing
        // the publisher's skip logic.
        var serverAck = new StubAcknowledger(canAcknowledge: true);
        var serverAlarm = new AlarmInfo("ANCHOR", "dragging",
            AlarmSeverity.Danger,
            TargetKey: "notifications.navigation.anchor.position",
            Acknowledger: serverAck);
        mgr.Set(serverAlarm);
        await Task.Yield();

        await Assert.That(api.Raised.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnsupportedTitle_DoesNotRaise()
    {
        // SART, APPROACH, and unknown titles return false from
        // TryMapToPath -- the publisher must not POST anything.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        mgr.Set(
            new AlarmInfo("SART", "Beacon", AlarmSeverity.Danger),
            new AlarmInfo("APPROACH", "100m to WP", AlarmSeverity.Warn),
            new AlarmInfo("FUTURE_RULE", "msg", AlarmSeverity.Warn));
        await Task.Yield();

        await Assert.That(api.Raised.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RaiseFailure_ReleasesOwnership_NextChangeRetries()
    {
        // Server returns 5xx (busy) on first attempt: drop the path
        // ownership so the next OnAlarmsChanged fires another raise.
        // Without this a transient failure permanently disables
        // cross-plotter sync until app restart.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        bool firstCall = true;
        api.RaiseHandler = (_, _) =>
        {
            if (firstCall) { firstCall = false; return ApiResult<string>.Fail("503"); }
            return ApiResult<string>.Ok("id-recovered");
        };
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        var alarm = new AlarmInfo("SHALLOW", "Depth 1.8m", AlarmSeverity.Danger);
        mgr.Set(alarm);
        await Task.Yield();
        // First raise failed: ownership released, ID not tracked.
        await Assert.That(tracker.IsOwnedPath("notifications.environment.depth.belowSurface")).IsFalse();

        // Trigger another change (alarm message ticks down).
        mgr.Set(alarm with { Message = "Depth 1.7m" });
        await Task.Yield();

        await Assert.That(api.Raised.Count).IsEqualTo(2);
        await Assert.That(tracker.IsOwnedPath("notifications.environment.depth.belowSurface")).IsTrue();
    }

    [Test]
    public async Task CpaPerTarget_RaisesOneNotificationPerVessel()
    {
        // Three vessels triggering CPA simultaneously -- three
        // distinct paths, three raises. Independent ack semantics:
        // dismissing target A on plotter B doesn't silence B's
        // banner for target B (each path has its own server id).
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        mgr.Set(
            new AlarmInfo("CPA", "Aurora", AlarmSeverity.Danger,
                TargetKey: "vessels.urn:mrn:imo:mmsi:111"),
            new AlarmInfo("CPA", "Beluga", AlarmSeverity.Danger,
                TargetKey: "vessels.urn:mrn:imo:mmsi:222"),
            new AlarmInfo("CPA", "Calypso", AlarmSeverity.Warn,
                TargetKey: "vessels.urn:mrn:imo:mmsi:333"));
        await Task.Yield();

        await Assert.That(api.Raised.Count).IsEqualTo(3);
        await Assert.That(api.Raised.Select(r => r.Path)).IsEquivalentTo([
            "notifications.security.collision.urn_mrn_imo_mmsi_111",
            "notifications.security.collision.urn_mrn_imo_mmsi_222",
            "notifications.security.collision.urn_mrn_imo_mmsi_333",
        ]);
        // Severity reflected per-payload.
        var aurora = api.Raised.Single(r => r.Path.EndsWith("111"));
        var calypso = api.Raised.Single(r => r.Path.EndsWith("333"));
        await Assert.That(aurora.Body.State).IsEqualTo("alarm");
        await Assert.That(calypso.Body.State).IsEqualTo("warn");
    }

    [Test]
    public async Task DispatchedClearOnDispose_FiresClearForEachRaised()
    {
        // App teardown / nav-away: the publisher's DisposeAsync must
        // DELETE every server notification it raised, otherwise the
        // 60s GC leaves stale alarms visible to other plotters until
        // it runs.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        var pub = new AlarmPublisher(mgr, api, tracker);
        mgr.Set(
            new AlarmInfo("SHALLOW", "x", AlarmSeverity.Danger),
            new AlarmInfo("WIND SHIFT", "y", AlarmSeverity.Warn));
        await Task.Yield();
        await Assert.That(api.Raised.Count).IsEqualTo(2);

        await pub.DisposeAsync();

        await Assert.That(api.Cleared.Count).IsEqualTo(2);
        await Assert.That(tracker.OwnedPaths.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DisposedPublisher_IgnoresFurtherChanges()
    {
        // OnAlarmsChanged firing after DisposeAsync (e.g. one last
        // tick during teardown) must not crash on the dictionary
        // we just emptied.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        var pub = new AlarmPublisher(mgr, api, tracker);
        await pub.DisposeAsync();

        // Should be a no-op; the publisher unsubscribed in DisposeAsync,
        // but defensive guard inside HandleAlarmsChanged covers the
        // race where an event fires between the unsubscribe and the
        // subscriber-list update.
        mgr.Set(new AlarmInfo("SHALLOW", "x", AlarmSeverity.Danger));
        await Task.Yield();

        await Assert.That(api.Raised.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PartialClear_OnlyRemovesNoLongerActiveAlarms()
    {
        // Two alarms active; one clears. The remaining one stays
        // raised. The diff logic must NOT clear the survivor on a
        // change that only added/removed others.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        var shallow = new AlarmInfo("SHALLOW", "x", AlarmSeverity.Danger);
        var wind = new AlarmInfo("WIND SHIFT", "y", AlarmSeverity.Warn);
        mgr.Set(shallow, wind);
        await Task.Yield();
        await Assert.That(api.Raised.Count).IsEqualTo(2);
        await Assert.That(api.Cleared.Count).IsEqualTo(0);

        mgr.Set(wind);    // shallow gone, wind stays
        await Task.Yield();

        await Assert.That(api.Cleared.Count).IsEqualTo(1);
        // Wind shift's path is still tracked.
        await Assert.That(tracker.IsOwnedPath("notifications.environment.wind.shift")).IsTrue();
        // Shallow's path was released.
        await Assert.That(tracker.IsOwnedPath("notifications.environment.depth.belowSurface")).IsFalse();
    }

    [Test]
    public async Task LateClearRace_ClearsAfterRaiseResolves()
    {
        // Ordering: alarm fires (raise in flight), helm dismisses
        // immediately (clear arrives BEFORE raise resolved). The
        // raise-completion handler must notice the slot was already
        // removed and fire the clear retroactively, otherwise the
        // server entry would linger until the 60s GC.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        // Deferred raise via TCS so we can drive the ordering: the
        // raise is in flight while the OnAlarmsChanged for the clear
        // fires. RunContinuationsAsynchronously prevents the SetResult
        // call from inlining the publisher's continuation onto the
        // test thread (which would change the observed ordering).
        var raiseTcs = new TaskCompletionSource<ApiResult<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.AsyncRaiseHandler = (_, _) => raiseTcs.Task;
        // Signal that fires when ClearAsync runs for our late id;
        // replaces a polling loop that previously slept up to a second
        // checking the Cleared list every 10 ms.
        var lateClearSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnCleared += id => { if (id == "late-id") lateClearSeen.TrySetResult(); };
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        mgr.Set(new AlarmInfo("SHALLOW", "x", AlarmSeverity.Danger));
        // The raise's await is suspended on the TCS; the publisher
        // has reserved the slot in _raised. Now the alarm clears --
        // HandleAlarmsChanged removes the slot and the late-completion
        // path inside TryRaiseAsync should fire the clear when the
        // TCS resolves.
        mgr.Set();
        // Resolve the raise. The pending await resumes; finds no slot
        // in _raised; fires ClearAsync directly.
        raiseTcs.SetResult(ApiResult<string>.Ok("late-id"));

        // Wait for ClearAsync to run, with a generous deadline that
        // only matters when the scheduler genuinely starves us. The
        // happy path completes in microseconds.
        await lateClearSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(api.Cleared).Contains("late-id");
    }
}
