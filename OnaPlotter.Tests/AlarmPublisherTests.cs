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
        public AlarmInfo? ActiveAlarm => _active.Count > 0 ? _active[0] : null;
        public IReadOnlyList<AlarmInfo> ActiveAlarms => _active;
        public int HiddenAlarmsCount => 0;
        public IReadOnlyList<SnoozedTarget> SnoozedTargets => [];
        public IReadOnlyList<DismissedAlarm> DismissedHistory => [];
        public int SnoozeDurationMinutes => 10;
        public IReadOnlyList<AlarmRearmInfo> RearmStatuses(DateTime now) => [];
        public event Action<AlarmInfo?>? OnAlarmChanged;
        public event Action? OnAlarmsChanged;

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

    private sealed class FakeApi : INotificationsApi
    {
        public List<(string Path, NotificationPayload Body)> Raised { get; } = [];
        public List<string> Cleared { get; } = [];
        public Func<string, NotificationPayload, ApiResult<string>>? RaiseHandler { get; set; }
        public Func<string, NotificationPayload, Task<ApiResult<string>>>? AsyncRaiseHandler { get; set; }

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
            return Task.FromResult(ApiResult.Ok);
        }
    }

    // -----------------------------------------------------------------
    // Title -> Path mapping. Pin every supported title so a refactor
    // that drops a case fails the build instead of silently making the
    // alarm un-publishable.
    // -----------------------------------------------------------------

    [Test]
    public async Task TryMapToPath_Shallow_BelowSurface()
    {
        var info = new AlarmInfo("SHALLOW", "Depth 1.8m < 3.0m", AlarmSeverity.Danger);
        AlarmPublisher.TryMapToPath(info, out var path);
        await Assert.That(path).IsEqualTo("notifications.environment.depth.belowSurface");
    }

    [Test]
    public async Task TryMapToPath_Cpa_PerTargetSuffix()
    {
        // SK MMSI URN format: vessels.urn:mrn:imo:mmsi:261006533
        // Path must be stable per target; sanitise ':' -> '_' so the
        // result tokenises as a clean SK path. Server-derived id will
        // overlay a re-raise for the same target.
        var info = new AlarmInfo("CPA", "MV Aurora: CPA 0.20nm in 4min",
            AlarmSeverity.Danger, TargetKey: "vessels.urn:mrn:imo:mmsi:261006533");
        AlarmPublisher.TryMapToPath(info, out var path);
        await Assert.That(path).IsEqualTo(
            "notifications.security.collision.urn_mrn_imo_mmsi_261006533");
    }

    [Test]
    public async Task TryMapToPath_Cpa_NoTargetKey_Skipped()
    {
        // Defensive: a CPA without a TargetKey shouldn't synthesize a
        // bogus shared path. SanitisePerTargetPath returns empty,
        // TryMapToPath returns false -- the publisher will skip.
        var info = new AlarmInfo("CPA", "missing target",
            AlarmSeverity.Danger, TargetKey: null);
        var ok = AlarmPublisher.TryMapToPath(info, out var path);
        await Assert.That(ok).IsFalse();
        await Assert.That(path).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TryMapToPath_AnchorDrag_DraggingLeaf()
    {
        var info = new AlarmInfo("ANCHOR DRAG", "Dragging: 50m / 30m radius",
            AlarmSeverity.Danger);
        AlarmPublisher.TryMapToPath(info, out var path);
        await Assert.That(path).IsEqualTo("notifications.navigation.anchor.dragging");
    }

    [Test]
    public async Task TryMapToPath_AnchorTide_TideLeaf()
    {
        var info = new AlarmInfo("ANCHOR TIDE", "Keel touches bottom at LW in 2h",
            AlarmSeverity.Warn);
        AlarmPublisher.TryMapToPath(info, out var path);
        await Assert.That(path).IsEqualTo("notifications.navigation.anchor.tide");
    }

    [Test]
    public async Task TryMapToPath_WindShift()
    {
        var info = new AlarmInfo("WIND SHIFT", "TWD shifted 30 deg in 5 min",
            AlarmSeverity.Warn);
        AlarmPublisher.TryMapToPath(info, out var path);
        await Assert.That(path).IsEqualTo("notifications.environment.wind.shift");
    }

    [Test]
    public async Task TryMapToPath_Deadman()
    {
        var info = new AlarmInfo("DEADMAN", "No interaction for 12 min",
            AlarmSeverity.Danger);
        AlarmPublisher.TryMapToPath(info, out var path);
        await Assert.That(path).IsEqualTo("notifications.helm.deadman");
    }

    [Test]
    public async Task TryMapToPath_Sart_NotPublished()
    {
        // Server-side: AIS feed produces SART/MOB/EPIRB notifications
        // directly. Republishing would clash with the server's own
        // path. Skip.
        var info = new AlarmInfo("SART", "Beacon", AlarmSeverity.Danger);
        var ok = AlarmPublisher.TryMapToPath(info, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryMapToPath_Approach_NotPublished()
    {
        // signalk-server's course-provider plugin already publishes
        // notifications.navigation.course.* . Republishing would race
        // its own deltas.
        var info = new AlarmInfo("APPROACH", "100m to WP", AlarmSeverity.Warn);
        var ok = AlarmPublisher.TryMapToPath(info, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryMapToPath_UnknownTitle_NotPublished()
    {
        // New rule added without registering a path mapping: skip
        // rather than crash. Add the case here when the rule arrives.
        var info = new AlarmInfo("FUTURE_RULE", "msg", AlarmSeverity.Warn);
        var ok = AlarmPublisher.TryMapToPath(info, out _);
        await Assert.That(ok).IsFalse();
    }

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
        // ServerNotificationsAlarmRule emits AlarmInfo with
        // NotificationId set. Republishing would loop -- we'd POST
        // /notifications, server emits delta, our store applies,
        // bridge rule emits, we'd POST again. The publisher must
        // recognise these as "already from the server" via the
        // NotificationId and skip.
        var mgr = new StubAlarmManager();
        var api = new FakeApi();
        var tracker = new PublishedAlarmTracker();
        await using var pub = new AlarmPublisher(mgr, api, tracker);

        var serverAlarm = new AlarmInfo("ANCHOR", "dragging",
            AlarmSeverity.Danger,
            TargetKey: "notifications.navigation.anchor.position",
            NotificationId: "server-uuid-1",
            CanAcknowledge: true);
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

        // Bounded poll: wait up to 1s for the late-clear continuation
        // to run. Yields drive both the threadpool and any Blazor-
        // style sync context the test runner installed; under heavy
        // parallel load three Task.Yield() calls aren't enough on
        // every platform. The deadline keeps the test fast on CI
        // while tolerating a busy scheduler.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline && !api.Cleared.Contains("late-id"))
        {
            await Task.Delay(10);
        }
        await Assert.That(api.Cleared).Contains("late-id");
    }
}
