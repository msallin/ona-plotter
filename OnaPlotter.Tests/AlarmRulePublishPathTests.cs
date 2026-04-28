using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the cross-plotter publish path declared by each rule's
/// <see cref="IAlarmRule.GetPublishPath"/> override. Centralises the
/// "what path does this rule emit on?" coverage that used to live on
/// AlarmPublisher.TryMapToPath before ARCH-002 moved the mapping onto
/// the rules themselves. Each rule owns its own SK path; renaming a
/// rule's Title no longer breaks publishing because the publisher
/// dispatches off (rule, alarm), not (alarm.Title -> some lookup).
/// </summary>
public class AlarmRulePublishPathTests
{
    [Test]
    public async Task ShallowAlarmRule_PublishesUnderEnvironmentDepthBelowSurface()
    {
        var rule = new ShallowAlarmRule();
        var info = new AlarmInfo("SHALLOW", "Depth 1.8m", AlarmSeverity.Danger);
        await Assert.That(rule.GetPublishPath(info))
            .IsEqualTo("notifications.environment.depth.belowSurface");
    }

    [Test]
    public async Task CpaAlarmRule_PerTargetPath_SanitisesUrnSeparators()
    {
        // SK MMSI URN format: vessels.urn:mrn:imo:mmsi:261006533. The
        // ':' separators must NOT survive in the path -- replace with
        // '_' so the resulting suffix tokenises cleanly on the server.
        var rule = new CpaAlarmRule(new MooredVesselTracker());
        var info = new AlarmInfo("CPA", "MV Aurora: CPA 0.20nm in 4min",
            AlarmSeverity.Danger,
            TargetKey: "vessels.urn:mrn:imo:mmsi:261006533");
        await Assert.That(rule.GetPublishPath(info))
            .IsEqualTo("notifications.security.collision.urn_mrn_imo_mmsi_261006533");
    }

    [Test]
    public async Task CpaAlarmRule_StrictlyReplacesNonSafeChars()
    {
        // Defense-in-depth: any character outside [A-Za-z0-9._-] is
        // replaced. A hostile or malformed AIS context that smuggles
        // path separators ('.', '/', '#', spaces, ...) cannot extend
        // the path past `notifications.security.collision.`. The
        // replacement keeps '_' and '-' as legal SK path tokens.
        var rule = new CpaAlarmRule(new MooredVesselTracker());
        var info = new AlarmInfo("CPA", "Spoof attempt",
            AlarmSeverity.Danger,
            TargetKey: "vessels.urn:mrn:imo:mmsi:9999.environment.depth.belowSurface");
        var path = rule.GetPublishPath(info);
        // The unsafe '.' inside the suffix becomes '_'; the leading '.'
        // we add as a separator stays.
        await Assert.That(path).StartsWith(
            "notifications.security.collision.urn_mrn_imo_mmsi_9999_environment_depth_belowSurface");
        // No '.' should appear in the suffix portion -- only the
        // single '.' that separates "collision" from the suffix.
        var suffix = path!["notifications.security.collision.".Length..];
        await Assert.That(suffix.Contains('.')).IsFalse();
    }

    [Test]
    public async Task CpaAlarmRule_NoTargetKey_ReturnsNull()
    {
        // Defensive: CPA alarms without a target are nonsensical
        // (collision against what?), but if one ever shows up the
        // publisher must NOT synthesize a bogus shared path that
        // would collide with another vessel's notification.
        var rule = new CpaAlarmRule(new MooredVesselTracker());
        var info = new AlarmInfo("CPA", "no target", AlarmSeverity.Danger,
            TargetKey: null);
        await Assert.That(rule.GetPublishPath(info)).IsNull();
    }

    [Test]
    public async Task WindShiftAlarmRule_PublishesUnderEnvironmentWindShift()
    {
        var rule = new WindShiftAlarmRule();
        var info = new AlarmInfo("WIND SHIFT", "TWD shifted 30 deg",
            AlarmSeverity.Warn);
        await Assert.That(rule.GetPublishPath(info))
            .IsEqualTo("notifications.environment.wind.shift");
    }

    [Test]
    public async Task AnchorDragAlarmRule_PublishesUnderNavigationAnchorDragging()
    {
        var rule = new AnchorDragAlarmRule();
        var info = new AlarmInfo("ANCHOR DRAG", "Dragging: 50m / 30m radius",
            AlarmSeverity.Danger);
        await Assert.That(rule.GetPublishPath(info))
            .IsEqualTo("notifications.navigation.anchor.dragging");
    }

    [Test]
    public async Task AnchorTideAlarmRule_PublishesUnderNavigationAnchorTide()
    {
        var rule = new AnchorTideAlarmRule();
        var info = new AlarmInfo("ANCHOR TIDE", "Keel touches bottom at LW in 2h",
            AlarmSeverity.Warn);
        await Assert.That(rule.GetPublishPath(info))
            .IsEqualTo("notifications.navigation.anchor.tide");
    }

    [Test]
    public async Task DeadmanAlarmRule_PublishesUnderHelmDeadman()
    {
        var rule = new DeadmanAlarmRule(new DeadmanTracker());
        var info = new AlarmInfo("DEADMAN", "No interaction for 12 min",
            AlarmSeverity.Danger);
        await Assert.That(rule.GetPublishPath(info))
            .IsEqualTo("notifications.helm.deadman");
    }

    [Test]
    public async Task AisSartAlarmRule_DoesNotPublish()
    {
        // SART / MOB / EPIRB are sourced server-side from the AIS
        // feed; republishing would clash with the server's own path.
        // Default IAlarmRule.GetPublishPath returns null and the rule
        // does not override. Cast through IAlarmRule because
        // GetPublishPath is a default interface method.
        IAlarmRule rule = new AisSartAlarmRule();
        var info = new AlarmInfo("SART", "Beacon", AlarmSeverity.Danger);
        await Assert.That(rule.GetPublishPath(info)).IsNull();
    }

    [Test]
    public async Task WaypointApproachAlarmRule_DoesNotPublish()
    {
        // signalk-server's course-provider plugin emits
        // notifications.navigation.course.* deltas directly when the
        // boat approaches a waypoint. Republishing would race the
        // server's own emission.
        IAlarmRule rule = new WaypointApproachAlarmRule();
        var info = new AlarmInfo("APPROACH", "100m to WP", AlarmSeverity.Warn);
        await Assert.That(rule.GetPublishPath(info)).IsNull();
    }

    [Test]
    public async Task ServerNotificationsAlarmRule_DoesNotPublish()
    {
        // The bridge rule's job is to consume server notifications;
        // its emissions already came from the server. The publisher's
        // primary skip is via Acknowledger != null, but the bridge
        // rule also returns null from GetPublishPath as belt-and-
        // braces in case a future refactor lets a server-emitted
        // alarm reach the publisher's pass-1 loop.
        IAlarmRule rule = new ServerNotificationsAlarmRule(
            new OnaPlotter.Services.ServerNotifications.ServerNotificationStore());
        var info = new AlarmInfo("ANCHOR", "dragging",
            AlarmSeverity.Danger,
            TargetKey: "notifications.navigation.anchor");
        await Assert.That(rule.GetPublishPath(info)).IsNull();
    }
}
