using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

public class AlarmManagerTests
{
    // --- test scaffolding ----------------------------------------------

    private sealed class FixedSettings : IAppSettings
    {
        public bool NightMode => false;
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
        public IReadOnlySet<string> EnabledChartIds => new HashSet<string>();
        public IReadOnlySet<string> EnabledRouteIds => new HashSet<string>();
        public event Action? OnSettingsChanged { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SetNightModeAsync(bool v) => Task.CompletedTask;
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
        public Task SetEnabledChartsAsync(IEnumerable<string> ids) => Task.CompletedTask;
        public Task SetEnabledRoutesAsync(IEnumerable<string> ids) => Task.CompletedTask;
    }

    private static AisVessel Vessel(string ctx, double lat, double lon,
        double? cogRad = 0, double? sogMs = 5.0, bool buddy = false)
    {
        var v = new AisVessel(ctx);
        v.Latitude = lat; v.Longitude = lon;
        v.CourseOverGround = cogRad; v.SpeedOverGround = sogMs;
        v.IsBuddy = buddy;
        return v;
    }

    private static NavigationData Nav(double? depth = null,
        double? lat = null, double? lon = null,
        double? cogRad = null, double? sogMs = null)
    {
        var n = new NavigationData();
        if (depth is not null) n.Apply("environment.depth.belowTransducer", depth);
        if (lat is not null && lon is not null) n.ApplyPosition(lat.Value, lon.Value);
        if (cogRad is not null) n.Apply("navigation.courseOverGroundTrue", cogRad);
        if (sogMs is not null) n.Apply("navigation.speedOverGround", sogMs);
        return n;
    }

    private static (AlarmManager mgr, MutableClock clock, FixedSettings settings, int fires) NewMgr()
    {
        var clock = new MutableClock();
        var settings = new FixedSettings();
        IAlarmRule[] rules =
        [
            new ShallowAlarmRule(),
            new CpaAlarmRule(),
            new WindShiftAlarmRule(),
        ];
        // Reflection access to the internal clock-injecting constructor.
        var mgr = (AlarmManager)Activator.CreateInstance(
            typeof(AlarmManager),
            bindingAttr: System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public,
            binder: null,
            args: [(IEnumerable<IAlarmRule>)rules, (Func<DateTime>)(() => clock.Now)],
            culture: null)!;
        int fires = 0;
        mgr.OnAlarmChanged += _ => fires++;
        return (mgr, clock, settings, fires);
    }

    private sealed class MutableClock { public DateTime Now { get; set; } = DateTime.UtcNow; }

    // --- depth ----------------------------------------------------------

    [Test]
    public async Task DepthBelowThreshold_FiresShallow()
    {
        var (mgr, clock, settings, _) = NewMgr();
        var data = Nav(depth: 2.0);

        mgr.Evaluate(data, [], settings);

        await Assert.That(mgr.ActiveAlarm).IsNotNull();
        await Assert.That(mgr.ActiveAlarm!.Title).IsEqualTo("SHALLOW");
        await Assert.That(mgr.ActiveAlarm.Severity).IsEqualTo(AlarmSeverity.Danger);
    }

    [Test]
    public async Task DepthBackAboveThreshold_AlarmClears()
    {
        var (mgr, clock, settings, _) = NewMgr();
        mgr.Evaluate(Nav(depth: 2.0), [], settings);
        clock.Now = clock.Now.AddSeconds(2);

        mgr.Evaluate(Nav(depth: 5.0), [], settings);

        await Assert.That(mgr.ActiveAlarm).IsNull();
    }

    // --- CPA ------------------------------------------------------------

    [Test]
    public async Task CpaInsideGuardZone_FiresCpa()
    {
        var (mgr, clock, settings, _) = NewMgr();
        // Own at origin, going north at 5 kn. Target 1/60 deg north going south at 5 kn.
        var data = Nav(lat: 0, lon: 0, cogRad: 0, sogMs: 2.57);   // ~5 kn
        var target = Vessel("vessels.ctx1", 1.0/60.0, 0, cogRad: Math.PI, sogMs: 2.57);

        mgr.Evaluate(data, [target], settings);

        await Assert.That(mgr.ActiveAlarm).IsNotNull();
        await Assert.That(mgr.ActiveAlarm!.Title).IsEqualTo("CPA");
        await Assert.That(mgr.ActiveAlarm.TargetKey).IsEqualTo("vessels.ctx1");
    }

    [Test]
    public async Task BuddyVessel_NeverTriggersCpa()
    {
        var (mgr, clock, settings, _) = NewMgr();
        var data = Nav(lat: 0, lon: 0, cogRad: 0, sogMs: 2.57);
        var buddy = Vessel("vessels.friend", 1.0/60.0, 0, cogRad: Math.PI, sogMs: 2.57, buddy: true);

        mgr.Evaluate(data, [buddy], settings);

        await Assert.That(mgr.ActiveAlarm).IsNull();
    }

    [Test]
    public async Task MooredVessel_NeverTriggersCpa()
    {
        var (mgr, clock, settings, _) = NewMgr();
        var data = Nav(lat: 0, lon: 0, cogRad: 0, sogMs: 2.57);
        // Nearly-stopped vessel on our bow. Advance the clock past the
        // moored-hold window so the tracker tags it as moored.
        var moored = Vessel("vessels.harbourtug", 1.0/60.0, 0, cogRad: 0, sogMs: 0.1);

        mgr.Evaluate(data, [moored], settings);       // seen slow (t=0)
        clock.Now = clock.Now.AddSeconds(5); mgr.Evaluate(data, [moored], settings);
        clock.Now = clock.Now.AddSeconds(120); mgr.Evaluate(data, [moored], settings);

        // First two calls might have fired CPA before the 120s hold; clear
        // any residual snooze-free alarm.
        await mgr.DismissAsync();
        clock.Now = clock.Now.AddSeconds(2);

        mgr.Evaluate(data, [moored], settings);

        await Assert.That(mgr.ActiveAlarm).IsNull();
    }

    [Test]
    public async Task Snooze_SilencesSpecificTargetOnly()
    {
        var (mgr, clock, settings, _) = NewMgr();
        var data = Nav(lat: 0, lon: 0, cogRad: 0, sogMs: 2.57);
        var a = Vessel("vessels.a", 1.0/60.0, 0, cogRad: Math.PI, sogMs: 2.57);
        var b = Vessel("vessels.b", 1.0/60.0, 0.0001, cogRad: Math.PI, sogMs: 2.57);

        mgr.Evaluate(data, [a], settings);
        await Assert.That(mgr.ActiveAlarm!.TargetKey).IsEqualTo("vessels.a");

        await mgr.SnoozeActiveAsync();
        clock.Now = clock.Now.AddSeconds(2);

        // Same target comes back -> still silenced.
        mgr.Evaluate(data, [a], settings);
        await Assert.That(mgr.ActiveAlarm).IsNull();

        // Different target -> alarm fires.
        clock.Now = clock.Now.AddSeconds(2);
        mgr.Evaluate(data, [b], settings);
        await Assert.That(mgr.ActiveAlarm).IsNotNull();
        await Assert.That(mgr.ActiveAlarm!.TargetKey).IsEqualTo("vessels.b");
    }

    [Test]
    public async Task Debounce_IgnoresSubSecondRepeats()
    {
        var (mgr, clock, settings, _) = NewMgr();
        int calls = 0;
        mgr.OnAlarmChanged += _ => calls++;

        var data = Nav(depth: 2.0);
        mgr.Evaluate(data, [], settings);
        mgr.Evaluate(data, [], settings);            // 200 ms later - skipped
        clock.Now = clock.Now.AddMilliseconds(200);
        mgr.Evaluate(data, [], settings);            // still inside 1 s window
        clock.Now = clock.Now.AddMilliseconds(900);  // now past the window

        // Message changes to make SetAlarm fire again.
        var data2 = Nav(depth: 1.5);
        mgr.Evaluate(data2, [], settings);

        // Fires: first alarm (1), message update (2). Two total.
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Dismiss_ClearsAndFires()
    {
        var (mgr, clock, settings, _) = NewMgr();
        mgr.Evaluate(Nav(depth: 2.0), [], settings);
        int firesBefore = 0;
        mgr.OnAlarmChanged += _ => firesBefore++;

        await mgr.DismissAsync();

        await Assert.That(mgr.ActiveAlarm).IsNull();
        await Assert.That(firesBefore).IsEqualTo(1);
    }
}
