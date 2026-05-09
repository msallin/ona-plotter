using System.Net;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

public class ServerNotificationsAlarmRuleTests
{
    /// <summary>Convenience: build a no-op INotificationsApi for tests
    /// that only care about the acknowledger's CanAcknowledge / Id
    /// projection from a ServerNotification, not the actual ack call.
    /// Returns success on every verb so the bridge rule treats it as
    /// "yes, I have somewhere to dispatch acks".</summary>
    private static INotificationsApi NoopApi()
    {
        var http = new HttpClient(new NoopHandler());
        return new NotificationsApi(http, new TestBaseUrl());
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("\"noop-id\"") });
    }

    private sealed class TestBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public event Action? OnBaseUrlChanged { add { } remove { } }
        public string Combine(string path) => BaseUrl + path;
    }
    [Test]
    public async Task DeriveTitleAndDefault_Depth()
    {
        // Default message humanised: drops the "environment.depth."
        // prefix, splits camelCase into "below transducer". Used
        // as fallback when the upstream notification's `message`
        // field is empty (focus-group field report 2026-04: a
        // bridged notification rendered "DEPTH environment.depth.
        // belowTransducer" because the originator didn't include
        // a message; humanised default reads as "DEPTH below
        // transducer" instead).
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.environment.depth.belowTransducer");
        await Assert.That(title).IsEqualTo("DEPTH");
        await Assert.That(msg).IsEqualTo("below transducer");
    }

    [Test]
    public async Task DeriveTitleAndDefault_Anchor()
    {
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.navigation.anchor.position");
        await Assert.That(title).IsEqualTo("ANCHOR");
        await Assert.That(msg).IsEqualTo("position");
    }

    [Test]
    public async Task DeriveTitleAndDefault_WindShift()
    {
        // The exact path the focus-group screenshot showed
        // mis-rendered as "WIND environment.wind.shift". With the
        // humanised default it reads "WIND shift" instead.
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.environment.wind.shift");
        await Assert.That(title).IsEqualTo("WIND");
        await Assert.That(msg).IsEqualTo("shift");
    }

    [Test]
    public async Task DeriveTitleAndDefault_Mob()
    {
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.mob");
        await Assert.That(title).IsEqualTo("MOB");
        await Assert.That(msg).IsEqualTo("Man overboard");
    }

    [Test]
    public async Task DeriveTitleAndDefault_Collision()
    {
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.security.collision.proximity");
        await Assert.That(title).IsEqualTo("COLLISION");
        await Assert.That(msg).IsEqualTo("collision proximity");
    }

    [Test]
    public async Task DeriveTitleAndDefault_CourseProvider_ArrivalCircleEntered()
    {
        // signalk-course-data publishes the arrival cue under
        // notifications.navigation.course.arrivalCircleEntered (or
        // the bare notifications.navigation.arrivalCircleEntered on
        // some builds). Both must surface as title "APPROACH" so
        // MainLayout.razor's `a.Title == "APPROACH"` test still
        // renders the "Next WP" advance button on the banner. Pin
        // both shapes here so a future schema flip is caught.
        var (title1, msg1) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.navigation.course.arrivalCircleEntered");
        await Assert.That(title1).IsEqualTo("APPROACH");
        await Assert.That(msg1).IsEqualTo("arrival circle entered");

        var (title2, msg2) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.navigation.arrivalCircleEntered");
        await Assert.That(title2).IsEqualTo("APPROACH");
        await Assert.That(msg2).IsEqualTo("arrival circle entered");
    }

    [Test]
    public async Task DeriveTitleAndDefault_CourseProvider_PerpendicularPassed()
    {
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.navigation.perpendicularPassed");
        await Assert.That(title).IsEqualTo("APPROACH");
        await Assert.That(msg).IsEqualTo("perpendicular passed");
    }

    [Test]
    public async Task DeriveTitleAndDefault_CourseProvider_RouteComplete()
    {
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.navigation.routeComplete");
        await Assert.That(title).IsEqualTo("APPROACH");
        await Assert.That(msg).IsEqualTo("route complete");
    }

    [Test]
    public async Task DeriveTitleAndDefault_UnknownPath_FallsBackToLeafSegment()
    {
        // Plugin that doesn't fit a known prefix: leaf segment
        // uppercased becomes the title, the full tail (humanised -
        // dots replaced with spaces, camelCase split) becomes the
        // default message.
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.plugin.somePluginAlert.detail");
        await Assert.That(title).IsEqualTo("DETAIL");
        await Assert.That(msg).IsEqualTo("plugin some plugin alert detail");
    }

    [Test]
    public async Task DeriveTitleAndDefault_PathWithoutPrefix_StillWorks()
    {
        // Defensive: if a future caller passes a path without the
        // "notifications." prefix, the helper shouldn't choke.
        var (title, _) = ServerNotificationsAlarmRule.DeriveTitleAndDefault("foo.bar");
        await Assert.That(title).IsEqualTo("BAR");
    }

    [Test]
    public async Task DeriveTitleAndDefault_SingleSegmentUnknown_NoDotInTail()
    {
        // Edge case: plugin emits a single-segment notification with no
        // dotted hierarchy, e.g. "notifications.heartbeat". The tail
        // "heartbeat" contains no '.' so lastIndexOf returns -1, and
        // the fallback branch must not crash on the slice. Pin the
        // outcome: leaf == full tail, title is uppercased.
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.heartbeat");
        await Assert.That(title).IsEqualTo("HEARTBEAT");
        await Assert.That(msg).IsEqualTo("heartbeat");
    }

    [Test]
    public async Task DeriveTitleAndDefault_BarePrefixOnly_GracefulFallback()
    {
        // Pathological: someone publishes literally "notifications." with
        // an empty tail. Should still produce something the banner can
        // render rather than throwing. Empty string title is acceptable;
        // the assertion is "no exception, message round-trips".
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.");
        await Assert.That(title).IsEqualTo("");
        await Assert.That(msg).IsEqualTo("");
    }

    [Test]
    public async Task BuildAlarmInfo_UsesPathAsTargetKey()
    {
        // TargetKey = path so two notifications under the same Title
        // (e.g. DEPTH at belowTransducer + DEPTH at belowSurface)
        // dedup as separate entries instead of overwriting each other.
        var n = new ServerNotification(
            "notifications.environment.depth.belowTransducer",
            "alarm",
            "shallow",
            AlarmSeverity.Danger);
        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n, api: null);

        await Assert.That(info.Title).IsEqualTo("DEPTH");
        await Assert.That(info.Message).IsEqualTo("shallow");
        await Assert.That(info.Severity).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(info.TargetKey).IsEqualTo("notifications.environment.depth.belowTransducer");
        await Assert.That(info.Snoozeable).IsTrue();
    }

    [Test]
    public async Task BuildAlarmInfo_FallsBackToDefaultMessageWhenServerMessageMissing()
    {
        // When the SignalK plugin doesn't include a "message" string
        // in the notification, BuildAlarmInfo uses the humanised
        // path-tail default so the banner reads as "DEPTH below
        // transducer" rather than "DEPTH environment.depth.belowTransducer"
        // (focus-group field report 2026-04: the raw-path render was
        // unhelpful; helms read words faster than dotted paths).
        var n = new ServerNotification(
            "notifications.environment.depth.belowTransducer",
            "alarm",
            null,
            AlarmSeverity.Danger);
        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n, api: null);

        await Assert.That(info.Message).IsEqualTo("below transducer");
    }

    [Test]
    public async Task CheckMany_EmptyStore_YieldsNothing()
    {
        var store = new ServerNotificationStore();
        var rule = new ServerNotificationsAlarmRule(store);
        var ctx = MakeContext();

        var hits = rule.CheckMany(ctx).ToArray();

        await Assert.That(hits.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CheckMany_OneNotification_OneAlarm()
    {
        var store = new ServerNotificationStore();
        store.Apply("notifications.navigation.anchor.position", "alarm", "Dragging anchor");
        var rule = new ServerNotificationsAlarmRule(store);

        var hits = rule.CheckMany(MakeContext()).ToArray();

        await Assert.That(hits.Length).IsEqualTo(1);
        await Assert.That(hits[0].Title).IsEqualTo("ANCHOR");
        await Assert.That(hits[0].Message).IsEqualTo("Dragging anchor");
    }

    [Test]
    public async Task CheckMany_MultipleNotifications_AllSurface()
    {
        // Multi-output proof: simultaneous depth + anchor + collision
        // all reach the alarm stack as distinct entries. Single-output
        // rules (Check) couldn't return them all.
        var store = new ServerNotificationStore();
        store.Apply("notifications.environment.depth.belowTransducer", "alarm", "shallow");
        store.Apply("notifications.navigation.anchor.position", "alarm", "dragging");
        store.Apply("notifications.security.collision.proximity", "warn", "close");
        var rule = new ServerNotificationsAlarmRule(store);

        var hits = rule.CheckMany(MakeContext()).ToArray();

        await Assert.That(hits.Length).IsEqualTo(3);
        await Assert.That(hits.Any(a => a.Title == "DEPTH")).IsTrue();
        await Assert.That(hits.Any(a => a.Title == "ANCHOR")).IsTrue();
        await Assert.That(hits.Any(a => a.Title == "COLLISION")).IsTrue();
    }

    [Test]
    public async Task Check_AlwaysReturnsNull()
    {
        // Rule contract: single-output Check is the IAlarmRule default
        // path, but ServerNotificationsAlarmRule speaks via CheckMany.
        // Check must return null so the manager doesn't double-add a
        // bogus alarm with the placeholder title.
        var store = new ServerNotificationStore();
        store.Apply("notifications.foo", "alarm", "msg");
        var rule = new ServerNotificationsAlarmRule(store);

        await Assert.That(rule.Check(MakeContext())).IsNull();
    }

    [Test]
    public async Task AutoClear_True_SoStackEntriesDropWhenStoreClears()
    {
        // The rule's contract with AlarmManager: when CheckMany no
        // longer yields a path, the manager auto-drops it from the
        // active stack. Required for the "server cleared the
        // notification" case to remove the banner without user action.
        var store = new ServerNotificationStore();
        var rule = new ServerNotificationsAlarmRule(store);
        await Assert.That(rule.AutoClear).IsTrue();
    }

    // --- v2 acknowledger propagation ---------------------------------

    [Test]
    public async Task BuildAlarmInfo_V2_AttachesAcknowledgerWithIdAndCanAcknowledge()
    {
        // Server >= 2.21 path: id + status.canAcknowledge=true. The
        // bridge rule must construct a SignalKNotificationAcknowledger
        // pointing at that id so AlarmManager.DismissAsync can invoke
        // it (and the banner can display the Acknowledge button).
        var status = new NotificationStatus(
            Silenced: false, Acknowledged: false,
            CanSilence: true, CanAcknowledge: true, CanClear: true);
        var n = new ServerNotification(
            "notifications.navigation.anchor.position",
            "alarm", "Dragging anchor", AlarmSeverity.Danger,
            Id: "anchor-uuid-1", Status: status);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n, NoopApi());
        var ack = info.Acknowledger as SignalKNotificationAcknowledger;

        await Assert.That(ack).IsNotNull();
        await Assert.That(ack!.Id).IsEqualTo("anchor-uuid-1");
        await Assert.That(ack.CanAcknowledge).IsTrue();
    }

    [Test]
    public async Task BuildAlarmInfo_PreV2_AcknowledgerStaysNull()
    {
        // No id, no status: pre-v2 server or non-v2-aware plugin. With
        // no id to address there's nothing to ack remotely; the
        // acknowledger field stays null and the dismiss path falls
        // back to local-only.
        var n = new ServerNotification(
            "notifications.environment.depth.belowTransducer",
            "alarm", "shallow", AlarmSeverity.Danger);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n, NoopApi());

        await Assert.That(info.Acknowledger).IsNull();
    }

    [Test]
    public async Task BuildAlarmInfo_ServerForbidsAcknowledge_CanAcknowledgeFalse()
    {
        // Spec: emergency-state notifications cannot be acknowledged
        // (silenced) by the helm. Server signals this via
        // status.canAcknowledge=false; the bridge rule attaches an
        // acknowledger that mirrors that flag so the banner can hide
        // the Acknowledge button (the dismiss path also short-circuits
        // when CanAcknowledge=false).
        var status = new NotificationStatus(
            Silenced: false, Acknowledged: false,
            CanSilence: false, CanAcknowledge: false, CanClear: false);
        var n = new ServerNotification(
            "notifications.mob", "emergency", "Man overboard",
            AlarmSeverity.Danger,
            Id: "mob-1", Status: status);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n, NoopApi());
        var ack = info.Acknowledger as SignalKNotificationAcknowledger;

        await Assert.That(ack).IsNotNull();
        await Assert.That(ack!.Id).IsEqualTo("mob-1");
        await Assert.That(ack.CanAcknowledge).IsFalse();
    }

    [Test]
    public async Task BuildAlarmInfo_NoApiWired_AcknowledgerStaysNull()
    {
        // Production wires the API via DI; legacy test ctors don't.
        // When api is null the bridge rule cannot build the
        // acknowledger (no endpoint to dispatch to) so the field
        // stays null. Pin this so the dismiss path's null-guard
        // remains the documented contract.
        var status = new NotificationStatus(
            Silenced: false, Acknowledged: false,
            CanSilence: true, CanAcknowledge: true, CanClear: true);
        var n = new ServerNotification(
            "notifications.navigation.anchor.position",
            "alarm", "Dragging anchor", AlarmSeverity.Danger,
            Id: "anchor-uuid-1", Status: status);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n, api: null);

        await Assert.That(info.Acknowledger).IsNull();
    }

    [Test]
    public async Task CheckMany_OwnedPath_Skipped()
    {
        // Phase B: when our own AlarmPublisher raised a notification,
        // the bridge rule must skip the echo. Otherwise the same
        // alarm would render twice - once from the originating
        // client-side rule, once from the publish echo via the
        // bridge.
        var store = new ServerNotificationStore();
        store.Apply("notifications.environment.depth.belowSurface",
            "alarm", "shallow");
        store.Apply("notifications.environment.wind.shift",
            "warn", "30 deg");
        var owned = new HashSet<string> { "notifications.environment.depth.belowSurface" };
        var tracker = new StubPublishedAlarmTracker(owned);
        // api=null -> bridge rule won't build acknowledgers but
        // the tracker-based skip still runs.
        var rule = new ServerNotificationsAlarmRule(store, api: null, publishedTracker: tracker);

        var hits = rule.CheckMany(MakeContext()).ToArray();

        // depth was suppressed (we own it); wind shift surfaces.
        await Assert.That(hits.Length).IsEqualTo(1);
        await Assert.That(hits[0].TargetKey).IsEqualTo("notifications.environment.wind.shift");
    }

    private sealed class StubPublishedAlarmTracker : IPublishedAlarmTracker
    {
        private readonly HashSet<string> _paths;
        public StubPublishedAlarmTracker(HashSet<string> paths) { _paths = paths; }
        public bool IsOwnedPath(string path) => _paths.Contains(path);
    }

    [Test]
    public async Task CheckMany_SnoozedPath_Skipped()
    {
        // The store stays armed (server side hasn't cleared) but the
        // rule must not surface the alarm while the helm's snooze
        // window is active. Without this the snooze is a no-op for
        // server-emitted notifications because the rule re-asserts
        // the alarm on every Evaluate tick.
        var store = new ServerNotificationStore();
        store.Apply("notifications.environment.depth.belowSurface",
            "warn", "shallow");
        store.Apply("notifications.navigation.anchor.position",
            "alarm", "dragging");
        var rule = new ServerNotificationsAlarmRule(store);
        var snoozed = new HashSet<string> { "notifications.environment.depth.belowSurface" };
        var ctx = new AlarmEvaluationContext(
            new NavigationData(), [], new FakeSettings(),
            DateTime.UtcNow, key => snoozed.Contains(key));

        var hits = rule.CheckMany(ctx).ToArray();

        // Anchor surfaces, depth is silenced.
        await Assert.That(hits.Length).IsEqualTo(1);
        await Assert.That(hits[0].Title).IsEqualTo("ANCHOR");
    }

    [Test]
    public async Task BuildAlarmInfo_IdWithoutStatus_AcknowledgerCanAckFalse()
    {
        // Defensive: a malformed delta with id but no status block
        // should not enable the Acknowledge button. Without the status
        // we can't verify the server actually supports the action; better
        // to omit the affordance than to send a POST that 404s or grants
        // the helm a false sense that they've handled the alarm.
        var n = new ServerNotification(
            "notifications.foo", "alarm", "msg", AlarmSeverity.Warn,
            Id: "id-1", Status: null);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n, NoopApi());
        var ack = info.Acknowledger as SignalKNotificationAcknowledger;

        await Assert.That(ack).IsNotNull();
        await Assert.That(ack!.Id).IsEqualTo("id-1");
        await Assert.That(ack.CanAcknowledge).IsFalse();
    }

    private static AlarmEvaluationContext MakeContext()
    {
        var data = new NavigationData();
        var settings = new FakeSettings();
        return new AlarmEvaluationContext(
            data, [], settings, DateTime.UtcNow, _ => false);
    }
}
