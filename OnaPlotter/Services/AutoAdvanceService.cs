using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services;

/// <summary>
/// Waypoint auto-advance plus the manual "Next WP" action on the APPROACH
/// banner.
/// <para>
/// The SignalK course-provider plugin publishes
/// <c>perpendicularPassed</c> / <c>arrivalCircleEntered</c> on every
/// active leg. On the false -> true edge of either (and with
/// <see cref="IAppSettings.AutoAdvanceWaypoints"/> on) the service moves
/// the route to the next point so the boat flows through the route
/// without a tap per waypoint, like a commercial chartplotter.
/// </para>
/// <para>
/// Edge-triggered: a server re-publishing <c>true</c> for the same leg is
/// not a new arrival. <see cref="Cooldown"/> additionally blocks a second
/// advance while the new-leg delta is still in transit; tighter values get
/// racy on slow links.
/// </para>
/// <para>
/// Multi-plotter idempotency: every plotter on the same server sees the
/// same edge. The relative "advance by 1" PUT is not idempotent (two
/// racing PUTs skip a waypoint); the absolute
/// <c>activeRoute/pointIndex</c> PUT is, so it is used whenever the
/// current index is known. On the last leg the relative call is used
/// instead: the server clamps an out-of-range index and only the relative
/// call ends the route, and there is nothing left to skip.
/// </para>
/// <para>Singleton, resolved at startup so it runs regardless of which
/// page is mounted. Course API calls run from fire-and-forget delta
/// handlers and banner taps; their failures are toasted via
/// <see cref="IToastService.LogException"/> rather than surfacing in
/// Blazor's error UI.</para>
/// </summary>
public sealed class AutoAdvanceService : IDisposable
{
    /// <summary>Minimum gap between two automatic advances: long enough
    /// for advance -> new-leg delta -> flag reset round-trip on a slow
    /// link.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(8);

    /// <summary>Wait after a successful advance before checking whether
    /// the course was cleared (last waypoint reached), so the server's
    /// course delta has time to land.</summary>
    public static readonly TimeSpan ArrivalSettle = TimeSpan.FromMilliseconds(400);

    private readonly ISignalkClient _signalk;
    private readonly IAppSettings _settings;
    private readonly ICourseApi _course;
    private readonly IAlarmManager _alarms;
    private readonly IToastService _toasts;
    private readonly TimeProvider _time;

    private bool _prevPerp;
    private bool _prevArr;
    private DateTimeOffset _lastAdvance = DateTimeOffset.MinValue;

    public AutoAdvanceService(ISignalkClient signalk, IAppSettings settings, ICourseApi course,
        IAlarmManager alarms, IToastService toasts, TimeProvider time)
    {
        _signalk = signalk;
        _settings = settings;
        _course = course;
        _alarms = alarms;
        _toasts = toasts;
        _time = time;
        _signalk.OnDataChanged += HandleDataChanged;
    }

    private void HandleDataChanged() => _ = OnDataChanged();

    /// <summary>Runs the edge detector against the current
    /// <see cref="SignalkClient.Data"/>. Returns the advance in flight, or
    /// a completed task when nothing fired; exposed for tests.</summary>
    internal Task OnDataChanged()
    {
        if (!_settings.AutoAdvanceWaypoints)
        {
            _prevPerp = false; _prevArr = false;
            return Task.CompletedTask;
        }
        var data = _signalk.Data;
        if (!data.HasActiveCourse)
        {
            _prevPerp = false; _prevArr = false;
            return Task.CompletedTask;
        }

        bool perp = data.PerpendicularPassed == true;
        bool arr = data.ArrivalCircleEntered == true;
        bool perpEdge = perp && !_prevPerp;
        bool arrEdge = arr && !_prevArr;
        _prevPerp = perp;
        _prevArr = arr;
        if (!(perpEdge || arrEdge)) return Task.CompletedTask;

        var now = _time.GetUtcNow();
        if (now - _lastAdvance < Cooldown) return Task.CompletedTask;
        _lastAdvance = now;
        return AutoAdvanceAsync(perpEdge ? "perpendicular passed" : "arrival circle");
    }

    private async Task AutoAdvanceAsync(string reason)
    {
        var data = _signalk.Data;
        bool hadCourseBefore = data.HasActiveCourse;
        if (data.ActiveRoutePointIndex is not int currentIdx)
        {
            // No pointIndex: the server hasn't published v2 course state
            // for this leg, so an absolute advance would be a guess and
            // the relative one reintroduces the multi-plotter race. Offer
            // the manual relative advance as a one-tap action instead.
            _toasts.ShowAction(
                "Auto-advance unavailable (route position unknown).",
                "Next WP",
                async () =>
                {
                    try
                    {
                        var r = await _course.AdvanceActiveRouteAsync();
                        if (!r.Success)
                            _toasts.Error($"Next waypoint failed: {r.Error ?? "server rejected"}");
                    }
                    catch (Exception ex)
                    {
                        _toasts.LogException(ex, "Next waypoint");
                    }
                },
                ToastLevel.Warning, durationSec: 8);
            return;
        }
        bool isLastLeg = data.ActiveRoutePointTotal is int total && currentIdx + 1 >= total;
        try
        {
            var r = isLastLeg
                ? await _course.AdvanceActiveRouteAsync()
                : await _course.SetPointIndexAsync(currentIdx + 1);
            if (!r.Success)
            {
                _toasts.Error($"Auto-advance failed: {r.Error ?? "server rejected"}");
                return;
            }
            _toasts.Info($"Next waypoint ({reason})");
            // The APPROACH banner latches (AutoClear=false); once the
            // system advanced on the helm's behalf it refers to a passed
            // waypoint, so clear it the same way the manual Next WP tap
            // does.
            await DismissApproachAlarmsAsync();
            await ReportArrivalIfCourseEndedAsync(hadCourseBefore);
        }
        catch (Exception ex)
        {
            _toasts.LogException(ex, "Auto-advance");
        }
    }

    /// <summary>Manual "Next WP" on the APPROACH banner: advance one leg
    /// (relative; the helm tapped once, on one plotter), dismiss the alarm
    /// that prompted it, and report arrival when that consumed the last
    /// waypoint. Failures toast so a network blip is visible.</summary>
    public async Task NextWaypointAsync(AlarmInfo alarm)
    {
        bool hadCourseBefore = _signalk.Data.HasActiveCourse;
        try
        {
            var r = await _course.AdvanceActiveRouteAsync();
            if (!r.Success)
            {
                _toasts.Error($"Next waypoint failed: {r.Error ?? "server rejected"}");
                return;
            }
            await _alarms.DismissAsync(alarm);
            await ReportArrivalIfCourseEndedAsync(hadCourseBefore);
        }
        catch (Exception ex)
        {
            _toasts.LogException(ex, "Next waypoint");
        }
    }

    private async Task DismissApproachAlarmsAsync()
    {
        // Snapshot: DismissAsync mutates the manager's active set.
        foreach (var alarm in _alarms.AllActiveAlarms.ToArray())
        {
            if (alarm.RuleId == AlarmRuleId.Approach)
                await _alarms.DismissAsync(alarm);
        }
    }

    /// <summary>End-of-route signal: some servers clear the course on the
    /// last leg's arrival, others hold the final waypoint. After a short
    /// settle, a course that went from active to cleared means the helm
    /// just reached the final waypoint; say so explicitly.</summary>
    private async Task ReportArrivalIfCourseEndedAsync(bool hadCourseBefore)
    {
        await Task.Delay(ArrivalSettle, _time);
        if (hadCourseBefore && !_signalk.Data.HasActiveCourse)
            _toasts.Success("Arrived at final waypoint");
    }

    public void Dispose() => _signalk.OnDataChanged -= HandleDataChanged;
}
