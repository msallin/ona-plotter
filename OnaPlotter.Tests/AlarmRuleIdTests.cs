using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="AlarmRuleId"/> as the stable alarm identity. The
/// persisted keys must equal the historical rule titles so a helm's
/// stored "disabledAlarmRules.v1" set keeps switching off the same rules,
/// and server-sourced alarms must carry the id of the local rule they
/// correspond to.
/// </summary>
public class AlarmRuleIdTests
{
    [Test]
    [Arguments(AlarmRuleId.Shallow, "SHALLOW")]
    [Arguments(AlarmRuleId.AnchorDrag, "ANCHOR DRAG")]
    [Arguments(AlarmRuleId.AnchorTide, "ANCHOR TIDE")]
    [Arguments(AlarmRuleId.Deadman, "DEADMAN")]
    [Arguments(AlarmRuleId.Cpa, "CPA")]
    [Arguments(AlarmRuleId.Hazard, "HAZARD")]
    [Arguments(AlarmRuleId.WindShift, "WIND SHIFT")]
    [Arguments(AlarmRuleId.Approach, "APPROACH")]
    [Arguments(AlarmRuleId.Sart, "SART")]
    public async Task PersistedKey_MatchesStoredSettingsValue(AlarmRuleId id, string stored)
    {
        await Assert.That(id.PersistedKey()).IsEqualTo(stored);
    }

    [Test]
    public async Task ServerNotification_IsNotToggleable()
    {
        await Assert.That(AlarmRuleId.ServerNotification.PersistedKey()).IsNull();
        await Assert.That(AlarmRuleId.ServerNotification.IsDisabledIn(new HashSet<string> { "" })).IsFalse();
    }

    [Test]
    public async Task EveryEnumValue_HasAMapping()
    {
        // A new enum value without a switch arm throws at runtime; this
        // walks every value so the omission fails here first.
        foreach (var id in Enum.GetValues<AlarmRuleId>())
            _ = id.PersistedKey();
        await Assert.That(Enum.GetValues<AlarmRuleId>().Length).IsGreaterThan(0);
    }

    [Test]
    public async Task EveryProductionRule_TitleEqualsPersistedKey()
    {
        // Banner title and persisted key coincide today; a rename of
        // the display title must not silently change identity, and a
        // rule must not ship with an id belonging to another rule.
        IAlarmRule[] rules =
        [
            new ShallowAlarmRule(),
            new AnchorDragAlarmRule(),
            new AnchorTideAlarmRule(),
            new DeadmanAlarmRule(new DeadmanTracker(TimeProvider.System)),
            new CpaAlarmRule(new MooredVesselTracker()),
            new HazardousRegionAlarmRule(new RegionStore()),
            new WindShiftAlarmRule(),
            new WaypointApproachAlarmRule(),
            new AisSartAlarmRule(),
        ];
        foreach (var r in rules)
            await Assert.That(r.Id.PersistedKey()).IsEqualTo(r.Title).Because(r.GetType().Name);
        await Assert.That(rules.Select(r => r.Id).Distinct().Count()).IsEqualTo(rules.Length);
    }

    [Test]
    [Arguments("notifications.navigation.course.arrivalCircleEntered", AlarmRuleId.Approach)]
    [Arguments("notifications.navigation.course.perpendicularPassed", AlarmRuleId.Approach)]
    [Arguments("notifications.navigation.arrivalCircleEntered", AlarmRuleId.Approach)]
    [Arguments("notifications.navigation.perpendicularPassed", AlarmRuleId.Approach)]
    [Arguments("notifications.navigation.routeComplete", AlarmRuleId.Approach)]
    [Arguments("notifications.environment.depth.belowKeel", AlarmRuleId.ServerNotification)]
    [Arguments("notifications.security.collision.urn_mrn_imo_mmsi_123", AlarmRuleId.ServerNotification)]
    [Arguments("notifications.mob", AlarmRuleId.ServerNotification)]
    [Arguments("notifications.custom.plugin.alert", AlarmRuleId.ServerNotification)]
    public async Task ServerAlarm_CarriesMatchingRuleId(string path, AlarmRuleId expected)
    {
        var store = new ServerNotificationStore();
        store.Apply(path, "alert", "msg");
        var rule = new ServerNotificationsAlarmRule(store, NoopNotificationsApi.Instance, NoopPublishedAlarmTracker.Instance);
        var ctx = new AlarmEvaluationContext(new NavigationData(), [], new FakeSettings(),
            TestClock.FixedUtcNow, _ => false);

        var alarm = rule.CheckMany(ctx).Single();

        await Assert.That(alarm.RuleId).IsEqualTo(expected);
    }

    [Test]
    public async Task ServerApproachIds_AgreeWithApproachTitle()
    {
        // DeriveRuleId and the title table are separate matchers; pin
        // that every path titled APPROACH also carries the Approach id.
        string[] paths =
        [
            "notifications.navigation.course.nextPoint",
            "notifications.navigation.arrivalCircleEntered",
            "notifications.navigation.perpendicularPassed",
            "notifications.navigation.routeComplete",
        ];
        foreach (var p in paths)
        {
            await Assert.That(ServerNotificationsAlarmRule.DeriveTitle(p)).IsEqualTo("APPROACH");
            await Assert.That(ServerNotificationsAlarmRule.DeriveRuleId(p)).IsEqualTo(AlarmRuleId.Approach);
        }
    }
}
