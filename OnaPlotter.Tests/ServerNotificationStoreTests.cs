using OnaPlotter.Models;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

public class ServerNotificationStoreTests
{
    [Test]
    public async Task Apply_ArmedAlarm_Stores()
    {
        var s = new ServerNotificationStore();
        bool changed = s.Apply(
            "notifications.environment.depth.belowTransducer",
            "alarm",
            "Below safe depth");

        await Assert.That(changed).IsTrue();
        await Assert.That(s.Count).IsEqualTo(1);
        var n = s.Active.Single();
        await Assert.That(n.Path).IsEqualTo("notifications.environment.depth.belowTransducer");
        await Assert.That(n.State).IsEqualTo("alarm");
        await Assert.That(n.Message).IsEqualTo("Below safe depth");
        await Assert.That(n.Severity).IsEqualTo(AlarmSeverity.Danger);
    }

    [Test]
    public async Task Apply_Normal_Clears()
    {
        var s = new ServerNotificationStore();
        s.Apply("notifications.foo", "alarm", "msg");
        bool changed = s.Apply("notifications.foo", "normal", null);

        await Assert.That(changed).IsTrue();
        await Assert.That(s.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_Cleared_Clears()
    {
        // Some plugins use "cleared" instead of "normal" for the
        // cleared state. Both have to map to a remove.
        var s = new ServerNotificationStore();
        s.Apply("notifications.foo", "alarm", "msg");
        s.Apply("notifications.foo", "cleared", null);

        await Assert.That(s.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_NullState_Clears()
    {
        var s = new ServerNotificationStore();
        s.Apply("notifications.foo", "alarm", null);
        bool changed = s.Apply("notifications.foo", null, null);

        await Assert.That(changed).IsTrue();
        await Assert.That(s.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_OnAlreadyClearedPath_NoChange()
    {
        // Calling Apply with state=normal on a path that wasn't armed
        // returns false: nothing to remove, no event needed.
        var s = new ServerNotificationStore();
        bool changed = s.Apply("notifications.foo", "normal", null);

        await Assert.That(changed).IsFalse();
    }

    [Test]
    public async Task Apply_Idempotent_NoChangeOnRepeat()
    {
        // Re-applying the SAME notification (same state + message) is
        // a no-op. The feeder is going to call Apply on every delta;
        // we only want a "changed" signal when the visible state
        // actually moved.
        var s = new ServerNotificationStore();
        s.Apply("notifications.foo", "alarm", "msg");
        bool secondChanged = s.Apply("notifications.foo", "alarm", "msg");

        await Assert.That(secondChanged).IsFalse();
    }

    [Test]
    public async Task Apply_DifferentMessage_Updates()
    {
        var s = new ServerNotificationStore();
        s.Apply("notifications.foo", "alarm", "first");
        bool changed = s.Apply("notifications.foo", "alarm", "second");

        await Assert.That(changed).IsTrue();
        await Assert.That(s.Active.Single().Message).IsEqualTo("second");
    }

    [Test]
    public async Task SeverityMapping_StatesMapToCoarseBuckets()
    {
        // Equivalence-class coverage for MapSeverity. emergency + alarm
        // are danger; warn + alert are warn; normal/cleared/null clear
        // (returns null sentinel); unknown states fail safe to danger.
        await Assert.That(ServerNotificationStore.MapSeverity("emergency")).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(ServerNotificationStore.MapSeverity("alarm")).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(ServerNotificationStore.MapSeverity("warn")).IsEqualTo(AlarmSeverity.Warn);
        await Assert.That(ServerNotificationStore.MapSeverity("alert")).IsEqualTo(AlarmSeverity.Warn);
        await Assert.That(ServerNotificationStore.MapSeverity("normal")).IsNull();
        await Assert.That(ServerNotificationStore.MapSeverity("cleared")).IsNull();
        await Assert.That(ServerNotificationStore.MapSeverity(null)).IsNull();
        await Assert.That(ServerNotificationStore.MapSeverity("")).IsNull();
        // Unknown strings: surface rather than drop. A plugin that
        // emits "critical" should still ring the bell, not be silently
        // discarded.
        await Assert.That(ServerNotificationStore.MapSeverity("critical")).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(ServerNotificationStore.MapSeverity("BANG")).IsEqualTo(AlarmSeverity.Danger);
    }

    [Test]
    public async Task Reset_DropsEverything()
    {
        var s = new ServerNotificationStore();
        s.Apply("notifications.a", "alarm", "x");
        s.Apply("notifications.b", "warn", "y");
        s.Reset();

        await Assert.That(s.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Active_IsSnapshot_NotLive()
    {
        // Iteration safety: callers should be able to iterate Active
        // while another caller is mutating the store. The Apply call
        // during iteration shouldn't surface as "collection modified".
        var s = new ServerNotificationStore();
        s.Apply("notifications.a", "alarm", "x");
        var snap = s.Active;

        s.Apply("notifications.b", "warn", "y");

        // Snapshot should still contain only the original entry.
        await Assert.That(snap.Count).IsEqualTo(1);
        await Assert.That(s.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MultiplePaths_AllStored()
    {
        // Multi-source scenario: anchor + depth + collision all
        // armed simultaneously. Each is a separate banner entry.
        var s = new ServerNotificationStore();
        s.Apply("notifications.environment.depth.belowTransducer", "alarm", "shallow");
        s.Apply("notifications.navigation.anchor.position", "alarm", "dragging");
        s.Apply("notifications.security.collision.alert", "warn", "approaching");

        await Assert.That(s.Count).IsEqualTo(3);
    }

    // --- v2 id + status -----------------------------------------------
    //
    // signalk-server >= 2.21 enriches every notifications.* delta with
    // a stable UUID id and a status block (silenced / acknowledged /
    // canSilence / canAcknowledge / canClear). The store must preserve
    // both so the alarm pipeline can drive cross-plotter ack via
    // INotificationsApi.AcknowledgeAsync(id) and the banner can hide
    // the Acknowledge button when canAcknowledge is false (life-safety
    // notifications the spec forbids silencing).

    [Test]
    public async Task Apply_V2WithIdAndStatus_StoresBoth()
    {
        var s = new ServerNotificationStore();
        var status = new NotificationStatus(
            Silenced: false, Acknowledged: false,
            CanSilence: true, CanAcknowledge: true, CanClear: true);

        s.Apply("notifications.navigation.anchor.position", "alarm",
            "dragging", id: "anchor-uuid-1", status: status);

        var n = s.Active.Single();
        await Assert.That(n.Id).IsEqualTo("anchor-uuid-1");
        await Assert.That(n.Status).IsNotNull();
        await Assert.That(n.Status!.CanAcknowledge).IsTrue();
        await Assert.That(n.Status.Acknowledged).IsFalse();
    }

    [Test]
    public async Task Apply_V1WithoutIdOrStatus_StoresNulls()
    {
        // Pre-2.21 server (or non-v2-aware plugin): id and status are
        // null. The entry still lands; the alarm pipeline degrades to
        // local-only dismiss (no server ack POST).
        var s = new ServerNotificationStore();
        s.Apply("notifications.foo", "alarm", "msg");

        var n = s.Active.Single();
        await Assert.That(n.Id).IsNull();
        await Assert.That(n.Status).IsNull();
    }

    [Test]
    public async Task Apply_StatusAcknowledgedTrue_ClearsEntry()
    {
        // Cross-plotter ack flow: helm acks on plotter A -> server marks
        // status.acknowledged=true -> server re-emits delta -> every
        // plotter (including A) sees the ack and drops the banner.
        var s = new ServerNotificationStore();
        var armed = new NotificationStatus(false, false, true, true, true);
        s.Apply("notifications.navigation.anchor.position", "alarm",
            "dragging", id: "anchor-uuid-1", status: armed);
        await Assert.That(s.Count).IsEqualTo(1);

        var acked = new NotificationStatus(
            Silenced: false, Acknowledged: true,
            CanSilence: true, CanAcknowledge: true, CanClear: true);
        bool changed = s.Apply("notifications.navigation.anchor.position",
            "alarm", "dragging", id: "anchor-uuid-1", status: acked);

        await Assert.That(changed).IsTrue();
        await Assert.That(s.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_IdempotentWithStatus_NoChangeOnRepeat()
    {
        // Same path + state + message + id + status => no change. The
        // record-equality on ServerNotification covers Status because
        // NotificationStatus is itself a record. Without this the store
        // would fire a "changed" event on every wire echo of the same
        // notification, which would re-render the banner needlessly.
        var s = new ServerNotificationStore();
        var status = new NotificationStatus(false, false, true, true, true);
        s.Apply("notifications.foo", "alarm", "msg", "id-1", status);

        bool secondChanged = s.Apply("notifications.foo", "alarm", "msg", "id-1", status);

        await Assert.That(secondChanged).IsFalse();
    }

    [Test]
    public async Task Apply_DifferentStatus_Updates()
    {
        // status.silenced flips on a server-side silence call; the store
        // should pick that up so a re-render reflects the new state.
        var s = new ServerNotificationStore();
        var unsilenced = new NotificationStatus(false, false, true, true, true);
        s.Apply("notifications.foo", "alarm", "msg", "id-1", unsilenced);

        var silenced = new NotificationStatus(true, false, true, true, true);
        bool changed = s.Apply("notifications.foo", "alarm", "msg", "id-1", silenced);

        await Assert.That(changed).IsTrue();
        await Assert.That(s.Active.Single().Status!.Silenced).IsTrue();
    }
}
