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
    private sealed class MutableClock { public DateTime Now { get; set; } = DateTime.UtcNow; }

    /// <summary>
    /// One plotter instance. Holds the alarm-stack pipeline (store ->
    /// rule -> manager) plus the AlarmManager-injected
    /// <see cref="INotificationsApi"/> -- the latter routes acks through
    /// the shared <see cref="FakeServer"/>.
    /// </summary>
    private sealed class Plotter
    {
        public string Name { get; }
        public ServerNotificationStore Store { get; }
        public AlarmManager Manager { get; }
        public MutableClock Clock { get; }

        public Plotter(string name, FakeServer server)
        {
            Name = name;
            Store = new ServerNotificationStore();
            Clock = new MutableClock();
            var rule = new ServerNotificationsAlarmRule(Store);
            // Per-plotter API stub that delegates to the shared server.
            // Each plotter has its own instance so the server can tell
            // which plotter originated a call (for the "concurrent acks"
            // test) without inferring it from caller stack.
            var api = new ServerBackedApi(server, this);
            Manager = (AlarmManager)Activator.CreateInstance(
                typeof(AlarmManager),
                bindingAttr: System.Reflection.BindingFlags.Instance
                           | System.Reflection.BindingFlags.NonPublic
                           | System.Reflection.BindingFlags.Public,
                binder: null,
                args: [(IEnumerable<IAlarmRule>)new IAlarmRule[] { rule },
                       (Func<DateTime>)(() => Clock.Now),
                       (IKeyValueStore?)null,
                       (IAppSettings?)null,
                       (INotificationsApi?)api],
                culture: null)!;
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
        private readonly List<Plotter> _plotters = new();
        public int AcknowledgeCallCount { get; private set; }

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
            FanOut(path, state, message, id, status);
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

        private void FanOut(string path, string state, string? message,
            string id, NotificationStatus status)
        {
            foreach (var p in _plotters)
                p.Store.Apply(path, state, message, id, status);
        }
    }

    /// <summary>Routes per-plotter API calls back to the shared
    /// <see cref="FakeServer"/>. Only Acknowledge is meaningful for the
    /// current Phase A coverage; the rest are stubbed to satisfy the
    /// interface.</summary>
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
            => Task.FromResult(ApiResult<string>.Ok("phase-b-not-yet"));

        public Task<ApiResult> ClearAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
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
        // The replay carries the v2 fields too, so the late joiner can
        // also ack across plotters from its banner.
        await Assert.That(c.Manager.ActiveAlarms[0].NotificationId).IsEqualTo("anchor-1");
        await Assert.That(c.Manager.ActiveAlarms[0].CanAcknowledge).IsTrue();
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
        await Assert.That(a.Manager.ActiveAlarms[0].NotificationId).IsEqualTo("anchor-2");
        await Assert.That(b.Manager.ActiveAlarms[0].NotificationId).IsEqualTo("anchor-2");
    }
}
