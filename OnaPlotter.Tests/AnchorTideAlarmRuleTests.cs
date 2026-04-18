using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

public class AnchorTideAlarmRuleTests
{
    private sealed class FixedSettings : IAppSettings
    {
        public bool NightMode => false;
        public string NightModePreset => "soft";
        public string Theme => "dark";
        public string MapOrientation => "north";
        public bool FollowBoat => true;
        public bool LaylinesVisible => false;
        public double DepthAlarmThreshold { get; set; } = 3.0;
        public double CpaAlarmThreshold { get; set; } = 0.5;
        public double GuardZoneLookaheadMinutes { get; set; } = 10.0;
        public double GuardZoneWarningFactor { get; set; } = 2.0;
        public double WindShiftAlarmThreshold { get; set; } = 15.0;
        public double WindShiftLookbackMinutes { get; set; } = 5.0;
        public double BoatDraftMeters { get; set; } = 1.5;
        public double AnchorTideSafetyMargin { get; set; } = 1.0;
        public IReadOnlySet<string> EnabledChartIds => new HashSet<string>();
        public IReadOnlySet<string> EnabledRouteIds => new HashSet<string>();
        public event Action? OnSettingsChanged { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SetNightModeAsync(bool v) => Task.CompletedTask;
        public Task SetNightModePresetAsync(string v) => Task.CompletedTask;
        public Task SetThemeAsync(string v) => Task.CompletedTask;
        public Task SetMapOrientationAsync(string v) => Task.CompletedTask;
        public Task SetFollowBoatAsync(bool v) => Task.CompletedTask;
        public Task SetLaylinesVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetDepthAlarmThresholdAsync(double v) => Task.CompletedTask;
        public Task SetCpaAlarmThresholdAsync(double v) => Task.CompletedTask;
        public Task SetGuardZoneLookaheadMinutesAsync(double v) => Task.CompletedTask;
        public Task SetGuardZoneWarningFactorAsync(double v) => Task.CompletedTask;
        public Task SetWindShiftAlarmThresholdAsync(double v) => Task.CompletedTask;
        public Task SetWindShiftLookbackMinutesAsync(double v) => Task.CompletedTask;
        public Task SetBoatDraftMetersAsync(double v) => Task.CompletedTask;
        public Task SetAnchorTideSafetyMarginAsync(double v) => Task.CompletedTask;
        public Task SetEnabledChartsAsync(IEnumerable<string> ids) => Task.CompletedTask;
        public Task SetEnabledRoutesAsync(IEnumerable<string> ids) => Task.CompletedTask;
    }

    private static NavigationData BuildNav(
        bool anchored,
        double? depth = null,
        double? heightNow = null,
        double? heightLow = null,
        DateTime? timeLow = null)
    {
        var nav = new NavigationData();
        if (anchored) nav.ApplyAnchorPosition(47.4, 8.5);
        if (depth is not null) nav.Apply("environment.depth.belowTransducer", depth);
        if (heightNow is not null) nav.Apply("environment.tide.heightNow", heightNow);
        if (heightLow is not null) nav.Apply("environment.tide.heightLow", heightLow);
        if (timeLow is not null)
            nav.ApplyString("environment.tide.timeLow",
                timeLow.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        return nav;
    }

    private static AlarmEvaluationContext Ctx(NavigationData nav, IAppSettings settings, DateTime now)
        => new(nav, [], settings, now, _ => false);

    [Test]
    public async Task NotAnchored_NoAlarm()
    {
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: false, depth: 3.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(3));
        await Assert.That(rule.Check(Ctx(nav, new FixedSettings(), now))).IsNull();
    }

    [Test]
    public async Task Anchored_NoTideData_NoAlarm()
    {
        // Without tide data, the rule can't predict LW and goes silent.
        // This is the default state for servers without a tide plugin.
        var rule = new AnchorTideAlarmRule();
        var nav = BuildNav(anchored: true, depth: 3.0);
        await Assert.That(rule.Check(Ctx(nav, new FixedSettings(), DateTime.UtcNow))).IsNull();
    }

    [Test]
    public async Task Anchored_SafeClearanceAtLw_NoAlarm()
    {
        // 8m depth now, tide drops 2m -> 6m at LW. Draft 1.5m, margin 1m.
        // Clearance 4.5m >> 1m margin. No alarm.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 8.0,
            heightNow: 2.5, heightLow: 0.5, timeLow: now.AddHours(3));
        await Assert.That(rule.Check(Ctx(nav, new FixedSettings(), now))).IsNull();
    }

    [Test]
    public async Task Anchored_ThinClearance_Warns()
    {
        // 3m depth now, tide drops 1.5m -> 1.5m at LW. Draft 0.8m, margin 1m.
        // Clearance 0.7m < margin 1m but > 0 -> Warn (not Danger).
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var settings = new FixedSettings { BoatDraftMeters = 0.8, AnchorTideSafetyMargin = 1.0 };
        var nav = BuildNav(anchored: true, depth: 3.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(2));

        var alarm = rule.Check(Ctx(nav, settings, now));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("ANCHOR TIDE");
        await Assert.That(alarm.Severity).IsEqualTo(AlarmSeverity.Warn);
        await Assert.That(alarm.Message).Contains("clearance");
    }

    [Test]
    public async Task Anchored_GroundingExpected_Dangers()
    {
        // 2m depth now, tide drops 1.5m -> 0.5m at LW. Draft 1.5m.
        // Clearance -1m (keel below bottom by 1m) -> Danger.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4));

        var alarm = rule.Check(Ctx(nav, new FixedSettings(), now));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(alarm.Message).Contains("touches");
    }

    [Test]
    public async Task RisingTide_NoAlarm()
    {
        // heightNow < heightLow means the next "low water" is actually
        // higher than current height -- i.e. tide is rising into its
        // next low or we're already past it. No grounding risk.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 0.4, heightLow: 0.5, timeLow: now.AddHours(2));
        await Assert.That(rule.Check(Ctx(nav, new FixedSettings(), now))).IsNull();
    }

    [Test]
    public async Task LwTooFarInFuture_NoAlarm()
    {
        // LW is 10 hours away -- beyond the 6h lookahead. Don't alarm
        // on things the user has time to wake up for.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(10));
        await Assert.That(rule.Check(Ctx(nav, new FixedSettings(), now))).IsNull();
    }

    [Test]
    public async Task LwInPast_NoAlarm()
    {
        // Stale tide data (timeLow already passed) shouldn't alarm.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(-1));
        await Assert.That(rule.Check(Ctx(nav, new FixedSettings(), now))).IsNull();
    }
}
