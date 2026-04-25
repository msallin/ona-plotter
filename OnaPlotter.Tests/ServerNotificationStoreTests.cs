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
}
