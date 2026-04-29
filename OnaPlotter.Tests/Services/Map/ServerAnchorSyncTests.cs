using OnaPlotter.Models;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the diff-and-watchdog logic of <see cref="ServerAnchorSync"/>.
/// Hand-rolled fakes for <see cref="IMapAnchorJs"/> + the connection
/// gate keep these tests free of bUnit and IJSRuntime; we drive the
/// controller with NavigationData and inspect the captured interop
/// calls.
/// </summary>
public class ServerAnchorSyncTests
{
    private sealed class FakeAnchorJs : IMapAnchorJs
    {
        public List<(double lat, double lon, double r)> Drops { get; } = [];
        public int Clears { get; private set; }
        public List<bool> Raisings { get; } = [];
        public List<double> RadiusUpdates { get; } = [];

        public Task SetAnchorAsync(double lat, double lon, double radiusMeters)
        {
            Drops.Add((lat, lon, radiusMeters));
            return Task.CompletedTask;
        }

        public Task ClearAnchorAsync()
        {
            Clears++;
            return Task.CompletedTask;
        }

        public Task SetAnchorRaisingAsync(bool raising)
        {
            Raisings.Add(raising);
            return Task.CompletedTask;
        }

        public Task UpdateAnchorRadiusAsync(double radiusMeters)
        {
            RadiusUpdates.Add(radiusMeters);
            return Task.CompletedTask;
        }
    }

    private sealed class TimeBox
    {
        public DateTime Now { get; set; } = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    private static (ServerAnchorSync sync, FakeAnchorJs js, List<string> warnings, TimeBox time) NewSync(
        bool connected = true)
    {
        var js = new FakeAnchorJs();
        var time = new TimeBox();
        var warnings = new List<string>();
        var sync = new ServerAnchorSync(
            js,
            isSignalKConnected: () => connected,
            raiseTimeoutWarning: msg => warnings.Add(msg),
            utcNow: () => time.Get());
        return (sync, js, warnings, time);
    }

    private static NavigationData WithAnchor(double lat, double lon, double maxRadius)
    {
        var data = new NavigationData();
        data.ApplyAnchorPosition(lat, lon);
        data.Apply("navigation.anchor.maxRadius", maxRadius);
        return data;
    }

    [Test]
    public async Task First_Sync_With_Server_Anchor_Draws_It()
    {
        // Plugin reports an anchor; controller draws it on first tick.
        var (sync, js, _, _) = NewSync();
        var data = WithAnchor(54.5, 11.2, 30);

        await sync.SyncAsync(data);

        await Assert.That(js.Drops.Count).IsEqualTo(1);
        await Assert.That(js.Drops[0]).IsEqualTo((54.5, 11.2, 30.0));
        await Assert.That(sync.ServerAnchorDrawn).IsTrue();
    }

    [Test]
    public async Task Default_Radius_Is_30m_When_Plugin_Omits_It()
    {
        // signalk-anchoralarm-plugin doesn't always publish maxRadius
        // (older versions skip it); the controller falls back to 30 m
        // so the on-map ring isn't drawn at zero.
        var (sync, js, _, _) = NewSync();
        var data = new NavigationData();
        data.ApplyAnchorPosition(54.5, 11.2);   // no max radius

        await sync.SyncAsync(data);

        await Assert.That(js.Drops[0].r).IsEqualTo(30.0);
    }

    [Test]
    public async Task Repeat_Sync_Same_Radius_Does_Not_Push_Again()
    {
        // Diff-by-state: once drawn, an unchanged delta is a no-op.
        var (sync, js, _, _) = NewSync();
        var data = WithAnchor(54.5, 11.2, 30);
        await sync.SyncAsync(data);
        await sync.SyncAsync(data);

        await Assert.That(js.Drops.Count).IsEqualTo(1);
        await Assert.That(js.RadiusUpdates).IsEmpty();
    }

    [Test]
    public async Task Radius_Change_Above_Threshold_Pushes_Update()
    {
        // Helm dials the radius up; controller forwards via the diff-only
        // updateAnchorRadius path (cheap; doesn't re-create the marker).
        var (sync, js, _, _) = NewSync();
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 35));

        await Assert.That(js.RadiusUpdates).IsEquivalentTo([35.0]);
    }

    [Test]
    public async Task Radius_Change_Below_Threshold_Skipped()
    {
        // GPS jitter makes the published radius fluctuate by mm; the
        // 0.1 m floor keeps that quiet.
        var (sync, js, _, _) = NewSync();
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30.05));

        await Assert.That(js.RadiusUpdates).IsEmpty();
    }

    [Test]
    public async Task Server_Anchor_Cleared_Tears_Down_The_Visualisation()
    {
        // Anchor was up; helm raised it via the plugin / button. Next
        // tick has no anchor data; the ring goes away.
        var (sync, js, _, _) = NewSync();
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));
        await sync.SyncAsync(new NavigationData());

        await Assert.That(js.Clears).IsEqualTo(1);
        await Assert.That(sync.ServerAnchorDrawn).IsFalse();
    }

    [Test]
    public async Task Clearing_When_Never_Drawn_Is_A_No_Op()
    {
        // Page reloads while there's no anchor; controller starts at
        // ServerAnchorDrawn=false. A no-anchor sync stays quiet.
        var (sync, js, _, _) = NewSync();

        await sync.SyncAsync(new NavigationData());

        await Assert.That(js.Clears).IsEqualTo(0);
        await Assert.That(sync.ServerAnchorDrawn).IsFalse();
    }

    [Test]
    public async Task On_Server_Anchor_Appeared_Callback_Runs_Before_Draw()
    {
        // Page wires a callback to clear any in-progress manual drop
        // before the server one renders, so the helm doesn't see two
        // overlapping rings.
        var (sync, js, _, _) = NewSync();
        bool callbackRan = false;
        sync.OnServerAnchorAppeared = () => { callbackRan = true; return Task.CompletedTask; };

        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));

        await Assert.That(callbackRan).IsTrue();
    }

    [Test]
    public async Task Raise_Pending_Watchdog_Fires_After_Timeout()
    {
        // Helm tapped raise; controller arms the watchdog. SK delta
        // doesn't clear the anchor within the 5 s window; controller
        // surfaces the warning + un-dims the marker.
        var (sync, js, warnings, time) = NewSync();
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));
        sync.MarkRaisePending();

        // Advance past the timeout. Sync again with anchor still active.
        time.Now = time.Now.AddSeconds(6);
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0]).Contains("not confirmed");
        // Un-dim is the false flip, sent after the warning.
        await Assert.That(js.Raisings).IsEquivalentTo([false]);
    }

    [Test]
    public async Task Raise_Pending_Watchdog_Suppressed_While_Disconnected()
    {
        // WebSocket is down; the timer re-arms each tick instead of
        // false-firing the moment we reconnect.
        var (sync, js, warnings, time) = NewSync(connected: false);
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));
        sync.MarkRaisePending();

        time.Now = time.Now.AddSeconds(20);
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));

        await Assert.That(warnings).IsEmpty();
        await Assert.That(js.Raisings).IsEmpty();
    }

    [Test]
    public async Task Raise_Pending_Cleared_When_Server_Confirms()
    {
        // Happy path: helm tapped raise; the SK delta clears the
        // anchor before the watchdog fires. Pending flag follows truth.
        var (sync, js, warnings, time) = NewSync();
        await sync.SyncAsync(WithAnchor(54.5, 11.2, 30));
        sync.MarkRaisePending();
        await sync.SyncAsync(new NavigationData());      // server cleared anchor

        // Even after the timeout window, no warning should fire because
        // the pending flag was cleared along with the anchor.
        time.Now = time.Now.AddSeconds(20);
        await sync.SyncAsync(new NavigationData());

        await Assert.That(warnings).IsEmpty();
    }
}
