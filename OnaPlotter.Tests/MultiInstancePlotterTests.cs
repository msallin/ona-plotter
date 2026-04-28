using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

/// <summary>
/// Cross-plotter sync coverage for SignalK v2 notifications. Models the
/// real-world scenario where two (or more) plotters share a single
/// SignalK server: helm at the chart table runs ona-plotter, helm at
/// the cockpit runs Freeboard-SK, both subscribed to the same
/// notifications.* feed. The contract under test:
/// <list type="bullet">
///   <item>An ack from ANY plotter clears the banner on EVERY plotter.</item>
///   <item>A locally-only dismiss (no v2 id, or canAcknowledge=false)
///         leaves the other plotters unchanged.</item>
///   <item>Concurrent acks from two plotters don't corrupt state -- the
///         ack is idempotent on the server, and the second client sees
///         the already-cleared state.</item>
///   <item>Snoozes stay plotter-local: silencing a target on plotter A
///         does NOT silence it on plotter B (snooze is a UX preference,
///         not a system-wide silence).</item>
/// </list>
/// <para>
/// The fixture is purely in-memory -- no HTTP, no WebSocket. The
/// <see cref="FakeServer"/> models the server's canonical state and
/// fans out deltas to every connected plotter. This lets us assert on
/// the convergence after a series of operations without flakiness from
/// real network timing.
/// </para>
/// </summary>
public class MultiInstancePlotterTests
{
    /// <summary>Test clock seeded at a fixed UTC instant. Hidden
    /// dependence on DateTime.UtcNow at construction would let a future
    /// cross-plotter timing assertion go wall-clock-flaky; the fixed
    /// seed keeps tests deterministic regardless of when they run.</summary>
    private sealed class MutableClock
    {
        public DateTime Now { get; set; } = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// One plotter instance. Holds the alarm-stack pipeline (store ->
    /// rule -> manager) plus the AlarmManager-injected
    /// <see cref="INotificationsApi"/> -- the latter routes acks +
    /// publish + clear through the shared <see cref="FakeServer"/>.
    /// <para>
    /// Phase B additions: <see cref="PublishedAlarmTracker"/> +
    /// <see cref="AlarmPublisher"/> are wired automatically. The bridge
    /// rule consults the tracker so this plotter's own publication
    /// echoes don't double-banner. The publisher subscribes to
    /// OnAlarmsChanged and POSTs to FakeServer.RaiseFromPlotter when
    /// a local rule emits an alarm. Locally-evaluated alarms are
    /// surfaced via optional client-side rules passed to the
    /// constructor; tests use <see cref="LocalStubRule"/> to script
    /// when those fire.
    /// </para>
    /// </summary>
    private sealed class Plotter
    {
        public string Name { get; }
        public ServerNotificationStore Store { get; }
        public AlarmManager Manager { get; }
        public MutableClock Clock { get; }
        public PublishedAlarmTracker Tracker { get; }
        public AlarmPublisher Publisher { get; }

        public Plotter(string name, FakeServer server,
            params IAlarmRule[] localRules)
        {
            Name = name;
            Store = new ServerNotificationStore();
            Clock = new MutableClock();
            Tracker = new PublishedAlarmTracker();
            // Per-plotter API stub that delegates to the shared server.
            // Each plotter has its own instance so the server can tell
            // which plotter originated a call (for the "concurrent acks"
            // test) without inferring it from caller stack.
            var api = new ServerBackedApi(server, this);
            // Bridge rule wires the API (so each emitted AlarmInfo carries
            // an IAlarmAcknowledger that points back at the FakeServer)
            // and the tracker (so it can suppress echoes of our own
            // publications). Both are optional in production tests; the
            // multi-instance fixture wants them both for the full
            // round-trip.
            var bridgeRule = new ServerNotificationsAlarmRule(Store, api, Tracker);
            // Compose: client-side local rules + bridge rule. The bridge
            // is last so its emissions overlay if a (Title, TargetKey)
            // collides -- matches the production rule order.
            var allRules = new List<IAlarmRule>(localRules) { bridgeRule };
            Manager = (AlarmManager)Activator.CreateInstance(
                typeof(AlarmManager),
                bindingAttr: System.Reflection.BindingFlags.Instance
                           | System.Reflection.BindingFlags.NonPublic
                           | System.Reflection.BindingFlags.Public,
                binder: null,
                args: [(IEnumerable<IAlarmRule>)allRules,
                       (Func<DateTime>)(() => Clock.Now),
                       (IKeyValueStore?)null,
                       (IAppSettings?)null],
                culture: null)!;
            Publisher = new AlarmPublisher(Manager, api, Tracker);
        }

        /// <summary>Bumps the clock past AlarmManager.EvaluationIntervalMs
        /// and re-runs Evaluate so a freshly-fanned-out delta can take
        /// effect on the next tick.</summary>
        public void Tick()
        {
            Clock.Now = Clock.Now.AddSeconds(2);
            Manager.Evaluate(new NavigationData(), [], new FakeSettings());
        }
    }

    /// <summary>Trivial client-side rule used by the Phase B tests to
    /// script when local alarms fire. Mirrors the test stub in
    /// AlarmManagerTests but keeps the multi-instance fixture self-
    /// contained so the file can be read top-to-bottom.</summary>
    private sealed class LocalStubRule : IAlarmRule
    {
        public string Title { get; }
        public int Priority { get; }
        public bool AutoClear => true;
        public bool ShouldFire { get; set; } = true;
        public string Message { get; set; } = "msg";
        public AlarmSeverity Severity { get; set; }
        public string? TargetKey { get; set; }
        /// <summary>Optional cross-plotter publish path. Mirrors the
        /// per-rule GetPublishPath override on production rules. The
        /// multi-instance fixture passes the matching path so the
        /// publisher actually emits a notification for the alarms the
        /// test arranges.</summary>
        public string? PublishPath { get; set; }

        public LocalStubRule(string title, int priority,
            AlarmSeverity sev, string? targetKey = null,
            string? publishPath = null)
        {
            Title = title;
            Priority = priority;
            Severity = sev;
            TargetKey = targetKey;
            PublishPath = publishPath;
        }

        public AlarmInfo? Check(AlarmEvaluationContext ctx)
            => ShouldFire
                ? new AlarmInfo(Title, Message, Severity, TargetKey, TargetKey)
                : null;

        public string? GetPublishPath(AlarmInfo alarm)
        {
            // Prefer the explicitly-set path; fall back to a title-
            // based default that mirrors the production rules so older
            // tests (which only set Title) continue to drive the
            // publisher down a real raise path.
            if (!string.IsNullOrEmpty(PublishPath)) return PublishPath;
            return Title switch
            {
                "SHALLOW" => "notifications.environment.depth.belowSurface",
                "CPA" when !string.IsNullOrEmpty(alarm.TargetKey)
                    => $"notifications.security.collision.{Sanitise(alarm.TargetKey!)}",
                "WIND SHIFT" => "notifications.environment.wind.shift",
                "ANCHOR DRAG" => "notifications.navigation.anchor.dragging",
                "ANCHOR TIDE" => "notifications.navigation.anchor.tide",
                "DEADMAN" => "notifications.helm.deadman",
                _ => null,
            };

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
    }

    /// <summary>
    /// In-memory model of a SignalK server's notifications view. Holds
    /// the canonical state (id -> notification) and fans deltas out to
    /// every registered plotter's <see cref="ServerNotificationStore"/>
    /// in registration order. Mirrors what signalk-server does when it
    /// receives a POST /acknowledge: update the in-memory record, then
    /// emit a delta on the WebSocket to every subscriber.
    /// </summary>
    private sealed class FakeServer
    {
        private readonly Dictionary<string, ServerNotification> _byPath = new(StringComparer.Ordinal);
        // path -> server-assigned id. Real signalk-server derives the
        // id from (context, path, $source) so a re-raise on the same
        // path overlays the existing entry. Modelled here by reusing
        // the same id when a path already exists.
        private readonly Dictionary<string, string> _idByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pathById = new(StringComparer.Ordinal);
        private readonly List<Plotter> _plotters = new();
        private int _idSeq;
        public int AcknowledgeCallCount { get; private set; }
        public int RaiseCallCount { get; private set; }
        public int ClearCallCount { get; private set; }

        public void Connect(Plotter p)
        {
            _plotters.Add(p);
            // Replay the active set on connect (matches the SignalK
            // hello + initial-values behaviour). New plotter joining
            // mid-watch sees existing armed notifications.
            foreach (var n in _byPath.Values)
                p.Store.Apply(n.Path, n.State, n.Message, n.Id, n.Status);
        }

        /// <summary>Server-side: a plugin armed a new notification.
        /// Equivalent to a delta arriving over WS.</summary>
        public void Raise(string path, string state, string message,
            string id, NotificationStatus status)
        {
            var sev = ServerNotificationStore.MapSeverity(state)
                ?? throw new InvalidOperationException($"unmappable state {state}");
            _byPath[path] = new ServerNotification(path, state, message, sev, id, status);
            _idByPath[path] = id;
            _pathById[id] = path;
            FanOut(path, state, message, id, status);
        }

        /// <summary>Plotter-side raise: AlarmPublisher posted a
        /// notification via INotificationsApi.RaiseAsync. Server
        /// derives a stable id per path (overlay semantics) and fans
        /// the resulting delta to every connected plotter, including
        /// the publishing plotter itself.</summary>
        public string RaiseFromPlotter(string path, NotificationPayload payload)
        {
            RaiseCallCount++;
            // Reuse the existing id for an overlay; mint a new one for
            // a fresh path. Mirrors signalk-server's deterministic-id
            // behaviour.
            if (!_idByPath.TryGetValue(path, out var id))
            {
                id = $"server-{++_idSeq}";
                _idByPath[path] = id;
                _pathById[id] = path;
            }
            // Default status: full permissions. Real server derives
            // canSilence/canAcknowledge from the PGN translation map;
            // synthetic v2 alarms default to permissive so cross-
            // plotter ack works as expected.
            var status = new NotificationStatus(
                Silenced: false, Acknowledged: false,
                CanSilence: true, CanAcknowledge: true, CanClear: true);
            var sev = ServerNotificationStore.MapSeverity(payload.State)
                ?? AlarmSeverity.Danger;
            _byPath[path] = new ServerNotification(
                path, payload.State, payload.Message, sev, id, status);
            FanOut(path, payload.State, payload.Message, id, status);
            return id;
        }

        /// <summary>Server-side ack handler. Sets status.acknowledged=true
        /// on the canonical record and fans the updated delta out.
        /// Idempotent: a second ack on an already-acked id is a no-op
        /// (matches signalk-server behaviour).</summary>
        public void Acknowledge(string id)
        {
            AcknowledgeCallCount++;
            var entry = _byPath.Values.FirstOrDefault(n => n.Id == id);
            if (entry is null) return;
            // Already acked: idempotent.
            if (entry.Status?.Acknowledged == true) return;
            var ackedStatus = entry.Status! with { Acknowledged = true };
            var updated = entry with { Status = ackedStatus };
            _byPath[entry.Path] = updated;
            FanOut(entry.Path, entry.State, entry.Message, id, ackedStatus);
        }

        /// <summary>Server-side clear: DELETE /notifications/{id}. The
        /// canonical state transitions to "normal" and a delta with
        /// state=normal is fanned out so every plotter's store drops
        /// the entry.</summary>
        public void ClearById(string id)
        {
            ClearCallCount++;
            if (!_pathById.TryGetValue(id, out var path)) return;
            _byPath.Remove(path);
            _idByPath.Remove(path);
            _pathById.Remove(id);
            // state="normal" tells every store's Apply to drop the path.
            foreach (var p in _plotters)
                p.Store.Apply(path, "normal", null);
        }

        private void FanOut(string path, string state, string? message,
            string id, NotificationStatus status)
        {
            foreach (var p in _plotters)
                p.Store.Apply(path, state, message, id, status);
        }
    }

    /// <summary>Routes per-plotter API calls back to the shared
    /// <see cref="FakeServer"/>. Phase A wired Acknowledge; Phase B
    /// wires Raise + Clear so AlarmPublisher's POSTs become real
    /// state changes on the canonical server view.</summary>
    private sealed class ServerBackedApi : INotificationsApi
    {
        private readonly FakeServer _server;
        private readonly Plotter _origin;

        public ServerBackedApi(FakeServer server, Plotter origin)
        {
            _server = server;
            _origin = origin;
        }

        public Task<ApiResult> AcknowledgeAsync(string id, CancellationToken ct = default)
        {
            _server.Acknowledge(id);
            return Task.FromResult(ApiResult.Ok);
        }

        public Task<ApiResult> SilenceAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);

        public Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default)
        {
            var id = _server.RaiseFromPlotter(path, body);
            return Task.FromResult(ApiResult<string>.Ok(id));
        }

        public Task<ApiResult> ClearAsync(string id, CancellationToken ct = default)
        {
            _server.ClearById(id);
            return Task.FromResult(ApiResult.Ok);
        }
    }

    private static NotificationStatus FullPermissions =>
        new(Silenced: false, Acknowledged: false,
            CanSilence: true, CanAcknowledge: true, CanClear: true);

    // -----------------------------------------------------------------
    // Cross-plotter ack: the headline behaviour the SK v2 spec promises.
    // -----------------------------------------------------------------

    [Test]
    public async Task AckOnPlotterA_ClearsBannerOnPlotterB()
    {
        // Helm at the chart table dismisses an anchor alarm; helm at
        // the cockpit's plotter sees the banner clear automatically on
        // the next tick (no separate dismiss needed). Without the v2
        // delta echo, both helms have to dismiss separately, which is
        // the bug v2 was designed to fix.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        var b = new Plotter("PlotterB", server);
        server.Connect(a);
        server.Connect(b);

        server.Raise("notifications.navigation.anchor.position", "alarm",
            "Anchor dragging", "anchor-1", FullPermissions);

        a.Tick();
        b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);

        // PlotterA helm dismisses. ServerBackedApi -> FakeServer.Acknowledge
        // -> fan out delta with status.acknowledged=true to BOTH stores.
        await a.Manager.DismissAsync(a.Manager.ActiveAlarms[0]);

        a.Tick();
        b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LateJoiner_ReceivesCurrentNotificationsOnConnect()
    {
        // PlotterC (e.g. a phone the crew picks up mid-watch) joins
        // after the alarm has fired. The "hello" replay must surface
        // the same ANCHOR banner it would have seen had it been
        // connected earlier.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        server.Connect(a);
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "anchor-1", FullPermissions);

        var c = new Plotter("PlotterC", server);
        server.Connect(c);
        c.Tick();

        await Assert.That(c.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(c.Manager.ActiveAlarms[0].Title).IsEqualTo("ANCHOR");
        // The replay carries the v2 acknowledger too, so the late
        // joiner can also ack across plotters from its banner.
        var ack = c.Manager.ActiveAlarms[0].Acknowledger
            as SignalKNotificationAcknowledger;
        await Assert.That(ack).IsNotNull();
        await Assert.That(ack!.Id).IsEqualTo("anchor-1");
        await Assert.That(ack.CanAcknowledge).IsTrue();
    }

    [Test]
    public async Task ConcurrentAck_FromTwoPlotters_LeavesBothCleared()
    {
        // Race condition coverage: helm at chart table AND helm at
        // cockpit press dismiss in the same second. Both clients fire
        // AcknowledgeAsync; the shared server gets two calls. The
        // second is idempotent and the local state ends up consistent
        // on both ends. Without the idempotency guarantee, the second
        // ack could re-fire a phantom delta or 500 the server.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "anchor-1", FullPermissions);
        a.Tick(); b.Tick();

        // Simulate near-simultaneous dismiss: both helms tap before
        // either delta echoes. AlarmManager removes from local _active
        // synchronously, then fires the ack fire-and-forget; in
        // testland the ack runs synchronously through the FakeServer
        // so we can assert ordering directly.
        await a.Manager.DismissAsync(a.Manager.ActiveAlarms[0]);
        await b.Manager.DismissAsync(b.Manager.ActiveAlarms[0]);

        a.Tick(); b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(0);
        // Both plotters fired the ack (no client-side coordination);
        // the server saw two calls but the second was a no-op.
        await Assert.That(server.AcknowledgeCallCount).IsEqualTo(2);
    }

    [Test]
    public async Task DismissBeforeFanout_DoesNotResurrectOnDeltaEcho()
    {
        // PlotterA dismisses locally. The ack fans out, including back
        // to A's own store. A's bridge rule must NOT re-emit the alarm
        // on the next Evaluate tick (the store removed the entry on
        // status.acknowledged=true). Otherwise A's banner would flicker
        // back on for one tick before clearing again.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        server.Connect(a);
        server.Raise("notifications.environment.depth.belowSurface", "warn",
            "Below 3m", "depth-1", FullPermissions);
        a.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);

        await a.Manager.DismissAsync(a.Manager.ActiveAlarms[0]);

        // Run several ticks to expose any flicker.
        for (int i = 0; i < 5; i++) a.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
    }

    // -----------------------------------------------------------------
    // Server forbids ack: cross-plotter sync explicitly disabled.
    // -----------------------------------------------------------------

    [Test]
    public async Task ServerForbidsAck_LocalDismissDoesNotReachOtherPlotter()
    {
        // Emergency-state notification (MOB / SART): canAcknowledge=false.
        // Spec forbids silencing across plotters. PlotterA's helm can
        // dismiss locally (their own banner clears so they can keep
        // working) but PlotterB's banner stays up so the second helm
        // sees the alarm on their station too -- that's the safety
        // property the spec is protecting.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);
        var emergencyStatus = new NotificationStatus(
            Silenced: false, Acknowledged: false,
            CanSilence: false, CanAcknowledge: false, CanClear: false);
        server.Raise("notifications.mob", "emergency",
            "Man overboard", "mob-1", emergencyStatus);
        a.Tick(); b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);

        await a.Manager.DismissAsync(a.Manager.ActiveAlarms[0]);

        a.Tick(); b.Tick();
        // PlotterA cleared locally; the cooldown suppresses re-emit.
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        // PlotterB still sees the MOB -- second helm hasn't seen it yet.
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms[0].Title).IsEqualTo("MOB");
        // Server received NO ack call (canAcknowledge=false short-
        // circuited the AlarmManager's FireAcknowledge).
        await Assert.That(server.AcknowledgeCallCount).IsEqualTo(0);
    }

    // -----------------------------------------------------------------
    // Server-side clear (state -> normal): both plotters auto-drop.
    // -----------------------------------------------------------------

    [Test]
    public async Task ServerClearsNotification_BothPlottersDropBanner()
    {
        // Plugin resolves the underlying condition (anchor re-set,
        // depth recovered): server emits a state="normal" delta. The
        // store removes the path, the bridge rule stops emitting, the
        // alarm-manager auto-clears on every connected plotter.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "anchor-1", FullPermissions);
        a.Tick(); b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);

        // Server-side clear: emit the same path with state=normal.
        // Mirrors signalk-anchoralarm-plugin transitioning back to
        // "anchor set, position OK".
        a.Store.Apply("notifications.navigation.anchor.position", "normal", null);
        b.Store.Apply("notifications.navigation.anchor.position", "normal", null);

        a.Tick(); b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(0);
    }

    // -----------------------------------------------------------------
    // Snooze isolation: per-plotter UX, not system-wide silence.
    // -----------------------------------------------------------------

    [Test]
    public async Task SnoozeOnPlotterA_DoesNotAffectPlotterB()
    {
        // Snooze is intentionally a per-plotter affordance: helm at the
        // chart table doesn't want to keep being warned about a slow-
        // moving fishing fleet, so they snooze. The cockpit helm's
        // plotter still shows the alarm because they haven't made the
        // same call. Cross-plotter snooze would require persisting the
        // snooze list to the server (not in scope for v2 ack); we keep
        // it local on purpose.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "anchor-1", FullPermissions);
        a.Tick(); b.Tick();

        await a.Manager.SnoozeAsync(a.Manager.ActiveAlarms[0]);

        a.Tick(); b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(a.Manager.SnoozedTargets.Count).IsEqualTo(1);
        // PlotterB unaffected.
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.SnoozedTargets.Count).IsEqualTo(0);
    }

    // -----------------------------------------------------------------
    // Idempotent delta echo: the server re-emits a state we already have.
    // -----------------------------------------------------------------

    [Test]
    public async Task RepeatedDeltaEcho_DoesNotResurrectClearedAlarm()
    {
        // After PlotterA acks, the server's 60s GC may not have fired
        // yet -- a re-emission of the same path with the SAME
        // status.acknowledged=true must not unclear the local state.
        // Without idempotency the alarm would flap on every echo.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        server.Connect(a);
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "anchor-1", FullPermissions);
        a.Tick();

        await a.Manager.DismissAsync(a.Manager.ActiveAlarms[0]);
        a.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);

        // Server re-emits the same already-acked state (e.g. another
        // client just connected and the server replayed everything).
        server.Acknowledge("anchor-1");

        a.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MultipleConcurrentNotifications_AckIsolatedPerId()
    {
        // Three independent server alarms armed simultaneously. PlotterA
        // acks ONE -- the other two stay live on both plotters. Per-id
        // ack isolation is what makes the cross-plotter flow tractable
        // (no bulk-ack semantics to trip over).
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "anchor-1", FullPermissions);
        server.Raise("notifications.environment.depth.belowSurface", "warn",
            "shallow", "depth-1", FullPermissions);
        server.Raise("notifications.environment.wind.speed.high", "warn",
            "gusty", "wind-1", FullPermissions);
        a.Tick(); b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(3);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(3);

        // Dismiss only DEPTH on plotter A.
        var depthA = a.Manager.ActiveAlarms.Single(x => x.Title == "DEPTH");
        await a.Manager.DismissAsync(depthA);

        a.Tick(); b.Tick();
        // Both plotters: DEPTH gone, ANCHOR + WIND still active.
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(2);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(2);
        await Assert.That(a.Manager.ActiveAlarms.Any(x => x.Title == "DEPTH")).IsFalse();
        await Assert.That(b.Manager.ActiveAlarms.Any(x => x.Title == "DEPTH")).IsFalse();
        await Assert.That(a.Manager.ActiveAlarms.Any(x => x.Title == "ANCHOR")).IsTrue();
        await Assert.That(b.Manager.ActiveAlarms.Any(x => x.Title == "ANCHOR")).IsTrue();
    }

    [Test]
    public async Task ReArmAfterAck_FiresFreshAlarmOnBothPlotters()
    {
        // Helm acks an anchor drag at t=0. Server's 60s GC clears the
        // record; the plugin re-arms an hour later when the boat drifts
        // again -- new id (server derives from context+path+$source but
        // a fresh "raise" cycle bumps the version). Both plotters must
        // see the fresh alarm: the dismiss-cooldown is keyed on
        // (Title, TargetKey) and the path is the TargetKey, so a
        // genuine re-arm on the same path within the cooldown window
        // would be silenced. To bypass, the cooldown must respect that
        // server-side ack already cleared this generation.
        //
        // Pin: re-raise after the cooldown window expires re-fires
        // cleanly. (The within-window case is documented as known
        // suppression; the helm-style fix is a longer rearm window
        // server-side, not a client-side bypass.)
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "anchor-1", FullPermissions);
        a.Tick(); b.Tick();
        await a.Manager.DismissAsync(a.Manager.ActiveAlarms[0]);
        a.Tick(); b.Tick();
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(0);

        // Step past the dismiss cooldown (default 30s for non-CPA).
        a.Clock.Now = a.Clock.Now.AddSeconds(AlarmManager.DismissCooldownSeconds + 5);
        b.Clock.Now = b.Clock.Now.AddSeconds(AlarmManager.DismissCooldownSeconds + 5);

        // Server re-arms the same path with a new id (fresh generation).
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "Anchor dragging again", "anchor-2", FullPermissions);
        a.Tick(); b.Tick();

        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);
        var ackA = a.Manager.ActiveAlarms[0].Acknowledger
            as SignalKNotificationAcknowledger;
        var ackB = b.Manager.ActiveAlarms[0].Acknowledger
            as SignalKNotificationAcknowledger;
        await Assert.That(ackA?.Id).IsEqualTo("anchor-2");
        await Assert.That(ackB?.Id).IsEqualTo("anchor-2");
    }

    // -----------------------------------------------------------------
    // Phase B: AlarmPublisher cross-plotter publish flow.
    // PlotterA evaluates a local rule (CPA/SHALLOW/...) -> publishes to
    // server -> server fans delta -> PlotterB sees a banner via the
    // bridge rule.
    // -----------------------------------------------------------------

    /// <summary>Yields control so fire-and-forget RaiseAsync /
    /// ClearAsync continuations queued by AlarmPublisher have a chance
    /// to run before assertions. Three yields covers the worst case:
    /// reserve-slot -> await api -> finalize-or-late-clear.</summary>
    private static async Task SettleAsync()
    {
        await Task.Yield();
        await Task.Yield();
        await Task.Yield();
    }

    [Test]
    public async Task LocalAlarmOnA_AppearsAsBannerOnB()
    {
        // Headline Phase B test: a SHALLOW alarm fires on PlotterA's
        // local rule. AlarmPublisher posts /notifications. Server fans
        // the delta. PlotterB's bridge rule emits an AlarmInfo. PlotterB's
        // banner shows the alarm without B's helm needing to do anything.
        var server = new FakeServer();
        var localShallow = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger)
        {
            Message = "Depth 1.8m < 3.0m",
        };
        var a = new Plotter("PlotterA", server, localShallow);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);

        // Tick A -> local rule fires SHALLOW -> publisher raises ->
        // server fans delta -> B's store has the entry on next Tick.
        a.Tick();
        await SettleAsync();
        b.Tick();

        // PlotterA: shows the original "SHALLOW" banner from the local
        // rule. The bridge's echo on the same path is suppressed by the
        // PublishedAlarmTracker.
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(a.Manager.ActiveAlarms[0].Title).IsEqualTo("SHALLOW");
        // PlotterB: bridge rule renders the path under its derived
        // title ("DEPTH" from the environment.depth.* prefix mapping).
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms[0].Title).IsEqualTo("DEPTH");
        await Assert.That(b.Manager.ActiveAlarms[0].Message).IsEqualTo("Depth 1.8m < 3.0m");
        // B's banner also carries the server's acknowledger so B's
        // helm can ack-clear cross-plotter via the existing Phase A
        // dismiss path.
        var bAck = b.Manager.ActiveAlarms[0].Acknowledger
            as SignalKNotificationAcknowledger;
        await Assert.That(bAck).IsNotNull();
        await Assert.That(bAck!.CanAcknowledge).IsTrue();
    }

    [Test]
    public async Task OriginPlotter_DoesNotDoubleBanner_WithLocalAndBridge()
    {
        // Critical correctness property: PlotterA must not show TWO
        // banners (one from the local rule, one from its own publish
        // echo). The PublishedAlarmTracker drives the suppression.
        var server = new FakeServer();
        var localShallow = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger);
        var a = new Plotter("PlotterA", server, localShallow);
        server.Connect(a);

        a.Tick();
        await SettleAsync();
        a.Tick();    // second tick to let the echo arrive at the store

        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(a.Manager.ActiveAlarms[0].Title).IsEqualTo("SHALLOW");
        // Tracker remembers the path while the alarm is active.
        await Assert.That(a.Tracker.IsOwnedPath(
            "notifications.environment.depth.belowSurface")).IsTrue();
    }

    [Test]
    public async Task LocalAlarmClearsOnA_BannerClearsOnB()
    {
        // PlotterA's local rule stops firing (depth recovers) ->
        // alarm leaves A's active stack -> publisher fires DELETE ->
        // server emits state=normal -> B's store drops the entry,
        // bridge rule stops emitting, B's banner clears.
        var server = new FakeServer();
        var rule = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger);
        var a = new Plotter("PlotterA", server, rule);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);

        a.Tick();
        await SettleAsync();
        b.Tick();
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);

        // Local rule stops firing.
        rule.ShouldFire = false;
        a.Tick();
        await SettleAsync();
        b.Tick();

        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(server.ClearCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task LateJoiner_SeesAlreadyPublishedAlarmOnConnect()
    {
        // PlotterA already published a SHALLOW. PlotterC connects mid-
        // watch (e.g. helm picks up a phone). The "hello" replay of
        // active server notifications brings the alarm into C's store
        // immediately so C's helm doesn't miss it.
        var server = new FakeServer();
        var rule = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger);
        var a = new Plotter("PlotterA", server, rule);
        server.Connect(a);
        a.Tick();
        await SettleAsync();
        await Assert.That(server.RaiseCallCount).IsEqualTo(1);

        var c = new Plotter("PlotterC", server);
        server.Connect(c);
        c.Tick();

        await Assert.That(c.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(c.Manager.ActiveAlarms[0].Title).IsEqualTo("DEPTH");
    }

    [Test]
    public async Task PublishedAlarm_AckOnB_ClearsBridgeViewOnAButLocalRulePersists()
    {
        // PlotterB's helm acks A's published alarm. Server emits ack
        // delta. B's store drops, B's banner clears. A's store also
        // drops (bridge rule was suppressed anyway). A's LOCAL rule
        // keeps firing because the underlying threat hasn't gone away
        // -- A's banner stays. The publisher tracker still holds the
        // path because A's local alarm is still active.
        //
        // This semantics is intentional: ack from B is "I see this on
        // my station", not "the threat is resolved". The threat-source
        // helm (A) still needs to clear when the condition resolves
        // or they decide to dismiss.
        var server = new FakeServer();
        var localShallow = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger);
        var a = new Plotter("PlotterA", server, localShallow);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);
        a.Tick();
        await SettleAsync();
        b.Tick();
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);

        // B's helm acks. AlarmManager.DismissAsync fires Acknowledge.
        await b.Manager.DismissAsync(b.Manager.ActiveAlarms[0]);

        a.Tick(); b.Tick();
        await SettleAsync();
        // B cleared.
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(0);
        // A's local SHALLOW banner still up.
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(a.Manager.ActiveAlarms[0].Title).IsEqualTo("SHALLOW");
    }

    [Test]
    public async Task CpaPerTarget_TwoVesselsTwoBanners()
    {
        // CPA's per-target path scheme means two simultaneous threats
        // produce two distinct server notifications, two banners on B,
        // independent ack semantics.
        var server = new FakeServer();
        var aurora = new LocalStubRule("CPA", 200, AlarmSeverity.Danger,
            targetKey: "vessels.urn:mrn:imo:mmsi:111")
        { Message = "Aurora: CPA 0.20nm in 4min" };
        var beluga = new LocalStubRule("CPA", 200, AlarmSeverity.Danger,
            targetKey: "vessels.urn:mrn:imo:mmsi:222")
        { Message = "Beluga: CPA 0.10nm in 2min" };
        var a = new Plotter("PlotterA", server, aurora, beluga);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);

        a.Tick();
        await SettleAsync();
        b.Tick();

        // B sees two distinct COLLISION banners (one per target path).
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(2);
        await Assert.That(b.Manager.ActiveAlarms.All(x => x.Title == "COLLISION")).IsTrue();
        var paths = b.Manager.ActiveAlarms.Select(x => x.TargetKey).ToList();
        await Assert.That(paths).Contains(
            "notifications.security.collision.urn_mrn_imo_mmsi_111");
        await Assert.That(paths).Contains(
            "notifications.security.collision.urn_mrn_imo_mmsi_222");
    }

    [Test]
    public async Task BothPlottersFireSameLocalAlarm_ServerOverlaysViaSamePath()
    {
        // Both A and B's local SHALLOW rules fire (both boats at the
        // same anchorage seeing 1.8m). Both publishers POST to the
        // same path. The server's path-based id derivation overlays
        // (returns the same id), so each plotter ends up tracking the
        // same id and the system stays in steady state.
        //
        // This is what protects the cross-plotter sync from a thunder-
        // herd -- N plotters with the same threat don't create N
        // distinct server notifications.
        var server = new FakeServer();
        var ruleA = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger);
        var ruleB = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger);
        var a = new Plotter("PlotterA", server, ruleA);
        var b = new Plotter("PlotterB", server, ruleB);
        server.Connect(a); server.Connect(b);

        a.Tick(); b.Tick();
        await SettleAsync();
        a.Tick(); b.Tick();

        // Server saw two raise calls but only one canonical record.
        await Assert.That(server.RaiseCallCount).IsEqualTo(2);
        // Each plotter's local rule is the source of truth for its own
        // banner; the bridge echo is suppressed via tracker.
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(a.Manager.ActiveAlarms[0].Title).IsEqualTo("SHALLOW");
        await Assert.That(b.Manager.ActiveAlarms[0].Title).IsEqualTo("SHALLOW");
    }

    [Test]
    public async Task ServerEmittedAlarm_NotRepublishedByOriginPlotter()
    {
        // Phase A's bridge rule emits AlarmInfo with NotificationId
        // for server-emitted alarms. The publisher must NOT republish
        // those (loop hazard). Verify by raising a server notification
        // directly and counting RaiseCallCount: it should remain at 0.
        var server = new FakeServer();
        var a = new Plotter("PlotterA", server);
        server.Connect(a);
        // Raise from server -> A's bridge rule emits with NotificationId.
        server.Raise("notifications.navigation.anchor.position", "alarm",
            "dragging", "external-anchor-1", FullPermissions);
        a.Tick();
        await SettleAsync();

        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        var ack = a.Manager.ActiveAlarms[0].Acknowledger
            as SignalKNotificationAcknowledger;
        await Assert.That(ack?.Id).IsEqualTo("external-anchor-1");
        // Publisher saw an acknowledger-bearing alarm and skipped it.
        await Assert.That(server.RaiseCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task DismissOnA_WithLocalRuleStillFiring_RaisesAgainOnNextTick()
    {
        // PlotterA dismisses a local SHALLOW alarm. AlarmManager's
        // 30s cooldown silences A's rule re-emit briefly, so the
        // alarm leaves the active set and the publisher fires DELETE.
        // Once the cooldown expires, the local rule re-fires (depth
        // is still shallow), publisher RAISEs again, B sees the
        // banner come back. End-state: A is showing again, B is
        // showing again, server has a single canonical record.
        var server = new FakeServer();
        var rule = new LocalStubRule("SHALLOW", 100, AlarmSeverity.Danger);
        var a = new Plotter("PlotterA", server, rule);
        var b = new Plotter("PlotterB", server);
        server.Connect(a); server.Connect(b);

        a.Tick();
        await SettleAsync();
        b.Tick();
        await Assert.That(server.RaiseCallCount).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);

        // A dismisses.
        await a.Manager.DismissAsync(a.Manager.ActiveAlarms[0]);
        await SettleAsync();
        b.Tick();
        // Cleared on A locally, cleared on server, cleared on B.
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(0);
        await Assert.That(server.ClearCallCount).IsEqualTo(1);

        // Step past A's cooldown; A's rule re-fires.
        a.Clock.Now = a.Clock.Now.AddSeconds(AlarmManager.DismissCooldownSeconds + 5);
        a.Tick();
        await SettleAsync();
        b.Tick();
        await Assert.That(server.RaiseCallCount).IsEqualTo(2);
        await Assert.That(a.Manager.ActiveAlarms.Count).IsEqualTo(1);
        await Assert.That(b.Manager.ActiveAlarms.Count).IsEqualTo(1);
    }
}
