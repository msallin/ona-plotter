using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="AutoAdvanceService"/>: edge-triggered advance on
/// perpendicularPassed / arrivalCircleEntered, the 8 s cooldown, the
/// idempotent absolute pointIndex PUT (relative only on the last leg),
/// APPROACH banner dismissal, and the manual Next WP action.
/// </summary>
public class AutoAdvanceServiceTests
{
    private sealed class FakeCourseApi : ICourseApi
    {
        public List<string> Calls { get; } = [];
        public ApiResult Result { get; set; } = ApiResult.Ok;
        public Action? OnAdvance { get; set; }

        public Task<ApiResult> SetDestinationAsync(string waypointId, CancellationToken ct = default) => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> SetDestinationPositionAsync(double latitude, double longitude, CancellationToken ct = default) => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> SetActiveRouteAsync(string routeId, int pointIndex = 0, bool reverse = false, CancellationToken ct = default) => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(ApiResult.Ok);

        public Task<ApiResult> AdvanceActiveRouteAsync(CancellationToken ct = default)
        {
            Calls.Add("advance");
            OnAdvance?.Invoke();
            return Task.FromResult(Result);
        }

        public Task<ApiResult> SetPointIndexAsync(int pointIndex, CancellationToken ct = default)
        {
            Calls.Add($"pointIndex={pointIndex}");
            return Task.FromResult(Result);
        }
    }

    private sealed class ApproachRule : IAlarmRule
    {
        public AlarmRuleId Id => AlarmRuleId.Approach;
        public string Title => "APPROACH";
        public int Priority => 250;
        public bool AutoClear => false;
        public bool Fire { get; set; } = true;
        public AlarmInfo? Check(AlarmEvaluationContext ctx) =>
            Fire ? new AlarmInfo(Id, Title, "100 m to WP", AlarmSeverity.Warn, TargetKey: "wp") : null;
    }

    private sealed record Fixture(
        AutoAdvanceService Service, SignalkClient Signalk, FakeCourseApi Course,
        AlarmManager Alarms, ToastService Toasts, FakeTimeProvider Time, FakeSettings Settings)
    {
        public NavigationData Data => Signalk.Data;
    }

    private static Fixture NewFixture()
    {
        var time = new FakeTimeProvider(TestClock.FixedUtcNow);
        var settings = new FakeSettings { AutoAdvanceWaypoints = true };
        var signalk = new SignalkClient(
            baseUrl: new OnaPlotter.Tests.Services.Resources.FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            data: new OnaPlotter.Models.NavigationData(),
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: settings,
            serverNotifs: new ServerNotificationStore(),
            atons: new AtonStore(),
            time: time);
        var course = new FakeCourseApi();
        var alarms = new AlarmManager([new ApproachRule()], time);
        var toasts = new ToastService();
        var svc = new AutoAdvanceService(signalk, settings, course, alarms, toasts, time);
        return new Fixture(svc, signalk, course, alarms, toasts, time, settings);
    }

    private static void ActiveLeg(NavigationData d, int index = 1, int total = 5)
    {
        d.ApplyCourseNextPointPosition(47.0, 8.0);
        d.Apply("navigation.course.activeRoute.pointIndex", index);
        d.Apply("navigation.course.activeRoute.pointTotal", total);
    }

    /// <summary>Runs the detector and lets the post-advance settle
    /// delay elapse on the fake clock.</summary>
    private static async Task Tick(Fixture f)
    {
        var t = f.Service.OnDataChanged();
        f.Time.Advance(AutoAdvanceService.ArrivalSettle);
        await t;
    }

    [Test]
    public async Task PerpendicularEdge_AdvancesByAbsolutePointIndex()
    {
        var f = NewFixture();
        ActiveLeg(f.Data, index: 1, total: 5);
        await Tick(f);                                   // flags false: nothing
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);

        await Assert.That(f.Course.Calls).IsEquivalentTo(["pointIndex=2"]);
    }

    [Test]
    public async Task ArrivalCircleEdge_Advances()
    {
        var f = NewFixture();
        ActiveLeg(f.Data);
        f.Data.ApplyBool("navigation.course.calcValues.arrivalCircleEntered", true);
        await Tick(f);

        await Assert.That(f.Course.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FlagStayingTrue_IsNotANewEdge()
    {
        var f = NewFixture();
        ActiveLeg(f.Data);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await Tick(f);

        await Assert.That(f.Course.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SecondEdgeInsideCooldown_IsIgnored_AfterCooldown_Fires()
    {
        var f = NewFixture();
        ActiveLeg(f.Data);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", false);
        await Tick(f);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);                                   // ~0.8 s after the first: cooldown
        await Assert.That(f.Course.Calls.Count).IsEqualTo(1);

        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", false);
        await Tick(f);
        f.Time.Advance(AutoAdvanceService.Cooldown);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);
        await Assert.That(f.Course.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task LastLeg_UsesRelativeAdvance_AndReportsArrival()
    {
        // pointIndex = total would be clamped by the server; only the
        // relative call ends the route.
        var f = NewFixture();
        ActiveLeg(f.Data, index: 4, total: 5);
        f.Course.OnAdvance = () => f.Data.ClearCourse();
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);

        await Assert.That(f.Course.Calls).IsEquivalentTo(["advance"]);
        await Assert.That(f.Toasts.Active.Any(t => t.Message == "Arrived at final waypoint")).IsTrue();
    }

    [Test]
    public async Task UnknownPointIndex_OffersManualAdvance_DoesNotGuess()
    {
        var f = NewFixture();
        f.Data.ApplyCourseNextPointPosition(47.0, 8.0);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);

        await Assert.That(f.Course.Calls.Count).IsEqualTo(0);
        await Assert.That(f.Toasts.Active.Any(t => t.ActionLabel == "Next WP")).IsTrue();
    }

    [Test]
    public async Task Disabled_OrNoCourse_DoesNothing()
    {
        var f = NewFixture();
        f.Settings.AutoAdvanceWaypoints = false;
        ActiveLeg(f.Data);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);
        await Assert.That(f.Course.Calls.Count).IsEqualTo(0);

        // Re-enabling with the flag already true counts as an edge: the
        // detector reset while disabled.
        f.Settings.AutoAdvanceWaypoints = true;
        await Tick(f);
        await Assert.That(f.Course.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SuccessfulAdvance_DismissesApproachBanner()
    {
        var f = NewFixture();
        f.Alarms.Evaluate(f.Data, [], f.Settings);
        await Assert.That(f.Alarms.ActiveAlarm?.RuleId).IsEqualTo(AlarmRuleId.Approach);

        ActiveLeg(f.Data);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);

        await Assert.That(f.Alarms.ActiveAlarm).IsNull();
    }

    [Test]
    public async Task FailedAdvance_KeepsBanner_ToastsError()
    {
        var f = NewFixture();
        f.Alarms.Evaluate(f.Data, [], f.Settings);
        f.Course.Result = ApiResult.Fail("409");
        ActiveLeg(f.Data);
        f.Data.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        await Tick(f);

        await Assert.That(f.Alarms.ActiveAlarm).IsNotNull();
        await Assert.That(f.Toasts.Active.Any(t => t.Message.StartsWith("Auto-advance failed"))).IsTrue();
    }

    [Test]
    public async Task ManualNextWaypoint_AdvancesRelativeAndDismisses()
    {
        var f = NewFixture();
        f.Alarms.Evaluate(f.Data, [], f.Settings);
        var approach = f.Alarms.ActiveAlarm!;
        ActiveLeg(f.Data);

        var t = f.Service.NextWaypointAsync(approach);
        f.Time.Advance(AutoAdvanceService.ArrivalSettle);
        await t;

        await Assert.That(f.Course.Calls).IsEquivalentTo(["advance"]);
        await Assert.That(f.Alarms.ActiveAlarm).IsNull();
    }
}
