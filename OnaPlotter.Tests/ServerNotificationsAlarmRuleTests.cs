using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

public class ServerNotificationsAlarmRuleTests
{
    [Test]
    public async Task DeriveTitleAndDefault_Depth()
    {
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.environment.depth.belowTransducer");
        await Assert.That(title).IsEqualTo("DEPTH");
        await Assert.That(msg).IsEqualTo("environment.depth.belowTransducer");
    }

    [Test]
    public async Task DeriveTitleAndDefault_Anchor()
    {
        var (title, _) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.navigation.anchor.position");
        await Assert.That(title).IsEqualTo("ANCHOR");
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
        var (title, _) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.security.collision.proximity");
        await Assert.That(title).IsEqualTo("COLLISION");
    }

    [Test]
    public async Task DeriveTitleAndDefault_UnknownPath_FallsBackToLeafSegment()
    {
        // Plugin that doesn't fit a known prefix: leaf segment
        // uppercased becomes the title, full tail (no notifications.
        // prefix) becomes the default message.
        var (title, msg) = ServerNotificationsAlarmRule.DeriveTitleAndDefault(
            "notifications.plugin.somePluginAlert.detail");
        await Assert.That(title).IsEqualTo("DETAIL");
        await Assert.That(msg).IsEqualTo("plugin.somePluginAlert.detail");
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
        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n);

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
        // in the notification, BuildAlarmInfo uses the path-tail
        // default so the banner isn't blank.
        var n = new ServerNotification(
            "notifications.environment.depth.belowTransducer",
            "alarm",
            null,
            AlarmSeverity.Danger);
        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n);

        await Assert.That(info.Message).IsEqualTo("environment.depth.belowTransducer");
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

    // --- v2 NotificationId + CanAcknowledge propagation ---------------

    [Test]
    public async Task BuildAlarmInfo_V2_PropagatesIdAndCanAcknowledge()
    {
        // Server >= 2.21 path: id + status.canAcknowledge=true. Both
        // must reach AlarmInfo so AlarmManager.DismissAsync can fire
        // INotificationsApi.AcknowledgeAsync(id) and the banner can
        // display the Acknowledge button.
        var status = new NotificationStatus(
            Silenced: false, Acknowledged: false,
            CanSilence: true, CanAcknowledge: true, CanClear: true);
        var n = new ServerNotification(
            "notifications.navigation.anchor.position",
            "alarm", "Dragging anchor", AlarmSeverity.Danger,
            Id: "anchor-uuid-1", Status: status);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n);

        await Assert.That(info.NotificationId).IsEqualTo("anchor-uuid-1");
        await Assert.That(info.CanAcknowledge).IsTrue();
    }

    [Test]
    public async Task BuildAlarmInfo_PreV2_LeavesIdAndCanAcknowledgeUnset()
    {
        // No id, no status: pre-v2 server or non-v2-aware plugin.
        // CanAcknowledge defaults to false because we have no endpoint
        // to call; the dismiss path falls back to local-only.
        var n = new ServerNotification(
            "notifications.environment.depth.belowTransducer",
            "alarm", "shallow", AlarmSeverity.Danger);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n);

        await Assert.That(info.NotificationId).IsNull();
        await Assert.That(info.CanAcknowledge).IsFalse();
    }

    [Test]
    public async Task BuildAlarmInfo_ServerForbidsAcknowledge_CanAcknowledgeFalse()
    {
        // Spec: emergency-state notifications cannot be acknowledged
        // (silenced) by the helm. Server signals this via
        // status.canAcknowledge=false; the bridge rule must respect it
        // so the banner doesn't expose an Acknowledge button that would
        // 403 anyway.
        var status = new NotificationStatus(
            Silenced: false, Acknowledged: false,
            CanSilence: false, CanAcknowledge: false, CanClear: false);
        var n = new ServerNotification(
            "notifications.mob", "emergency", "Man overboard",
            AlarmSeverity.Danger,
            Id: "mob-1", Status: status);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n);

        await Assert.That(info.NotificationId).IsEqualTo("mob-1");
        await Assert.That(info.CanAcknowledge).IsFalse();
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
    public async Task BuildAlarmInfo_IdWithoutStatus_CanAcknowledgeFalse()
    {
        // Defensive: a malformed delta with id but no status block
        // should not enable the Acknowledge button. Without the status
        // we can't verify the server actually supports the action; better
        // to omit the affordance than to send a POST that 404s or grants
        // the helm a false sense that they've handled the alarm.
        var n = new ServerNotification(
            "notifications.foo", "alarm", "msg", AlarmSeverity.Warn,
            Id: "id-1", Status: null);

        var info = ServerNotificationsAlarmRule.BuildAlarmInfo(n);

        await Assert.That(info.NotificationId).IsEqualTo("id-1");
        await Assert.That(info.CanAcknowledge).IsFalse();
    }

    private static AlarmEvaluationContext MakeContext()
    {
        var data = new NavigationData();
        var settings = new FakeSettings();
        return new AlarmEvaluationContext(
            data, [], settings, DateTime.UtcNow, _ => false);
    }
}
