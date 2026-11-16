using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Routes own-vessel deltas. Most paths are plain numbers that
/// <see cref="NavigationData.Apply"/> understands; the ones whose wire
/// shape needs interpretation (positions, course parents, anchor
/// clears, course-provider notification flags) have an entry in
/// <see cref="ExactPaths"/>. Two subtrees are forwarded to other
/// handlers: <c>radars.*</c> (ARPA targets into the AIS store) and
/// <c>notifications.*</c> (server alarms).
/// <para>Lookup order per value:</para>
/// <list type="number">
/// <item><see cref="ExactPaths"/>; a handler may decline a shape it
/// doesn't own (<see cref="Outcome.NotHandled"/>) and let the value fall
/// through.</item>
/// <item><c>radars.</c> / <c>notifications.</c> prefixes.</item>
/// <item>Generic: bool -> <see cref="NavigationData.ApplyBool"/>, string
/// -> <see cref="NavigationData.ApplyString"/>, then number ->
/// <see cref="NavigationData.Apply"/>.</item>
/// </list>
/// When adding a self path:
/// 1. Subscribe it in the matching tier in <c>SignalkClient.Tiers</c>.
/// 2. If it is a number / bool / string, add the case to the matching
///    <c>NavigationData.Apply*</c> switch; nothing to do here.
/// 3. Otherwise add an <see cref="ExactPaths"/> entry.
/// </summary>
internal sealed class SelfDeltaHandler
{
    private enum Outcome
    {
        /// <summary>Shape not owned by this handler; try the generic path.</summary>
        NotHandled,
        /// <summary>Consumed without changing navigation state.</summary>
        Handled,
        /// <summary>Consumed and navigation state changed.</summary>
        Changed,
    }

    private delegate Outcome PathHandler(SelfDeltaHandler self, string path, DeltaValue value);

    private const string CoursePreviousPoint = "navigation.course.previousPoint";
    private const string PerpendicularPassedNotification = "notifications.navigation.course.perpendicularPassed";
    private const string ArrivalCircleEnteredNotification = "notifications.navigation.course.arrivalCircleEntered";

    private static readonly Dictionary<string, PathHandler> ExactPaths = new(StringComparer.Ordinal)
    {
        ["communication.callsignVhf"] = static (h, _, v) => h.OnCallsign(v),
        [SkPaths.Navigation.Position] = static (h, _, v) => h.OnPosition(v),
        [SkPaths.Navigation.Anchor.Position] = static (h, _, v) => h.OnAnchorPosition(v),
        [SkPaths.Navigation.Anchor.MaxRadius] = static (h, _, v) => h.OnAnchorMaxRadius(v),
        [SkPaths.Navigation.Course.ActiveRoute] = static (h, _, v) => h.OnActiveRoute(v),
        [SkPaths.Navigation.Course.ActiveRouteHref] = static (h, _, v) => h.ClearCourseIfNull(v),
        [SkPaths.Navigation.Course.NextPoint] = static (h, _, v) => h.OnNextPoint(v),
        [SkPaths.Navigation.Course.NextPointPosition] = static (h, _, v) => h.OnNextPointPosition(v),
        [CoursePreviousPoint] = static (h, _, v) => h.OnPreviousPoint(v),
        [SkPaths.Navigation.Course.PreviousPointPosition] = static (h, _, v) => h.OnPreviousPointPosition(v),
        [PerpendicularPassedNotification] = static (h, p, v) => h.OnCourseNotification(p, v),
        [ArrivalCircleEnteredNotification] = static (h, p, v) => h.OnCourseNotification(p, v),
    };

    private readonly NavigationData _data;
    private readonly AisDeltaHandler _radar;
    private readonly ServerNotificationDeltaHandler _notifications;
    private readonly ILogger _logger;

    public SelfDeltaHandler(NavigationData data, AisDeltaHandler radar,
        ServerNotificationDeltaHandler notifications, ILogger logger)
    {
        _data = data;
        _radar = radar;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>VHF callsign for the helm vessel from
    /// <c>communication.callsignVhf</c>. Identity, not navigation, so it
    /// lives here rather than on <see cref="NavigationData"/> and its
    /// arrival does not count as a navigation change.</summary>
    public string? OwnCallsign { get; private set; }

    /// <summary>Applies every value of a self-context delta. Returns true
    /// when navigation state (or the server notification set) changed.</summary>
    public bool Apply(SignalkDelta delta)
    {
        bool changed = false;
        foreach (var update in delta.Updates!)
        {
            _data.MarkDataReceived();
            if (update.Values is null) continue;

            foreach (var val in update.Values)
            {
                if (val.Path is null) continue;
                changed |= ApplyValue(val.Path, val.Value);
            }
        }
        return changed;
    }

    private bool ApplyValue(string path, DeltaValue value)
    {
        if (ExactPaths.TryGetValue(path, out var handler))
        {
            var outcome = handler(this, path, value);
            if (outcome != Outcome.NotHandled) return outcome == Outcome.Changed;
        }

        if (path.StartsWith("radars.", StringComparison.Ordinal))
        {
            _radar.ApplyRadar(path, value);
            return false;
        }

        // Generic server-side notifications land in the store so the
        // ServerNotificationsAlarmRule can surface them in the banner
        // stack on the next Evaluate tick. The course-provider flags
        // are exact paths above and never reach the store.
        if (path.StartsWith("notifications.", StringComparison.Ordinal))
        {
            return _notifications.Route(path, value);
        }

        // Plain booleans (leg-advance fallbacks and similar). ApplyBool
        // returns false for unknown paths so random bool fields don't
        // bind anywhere unexpected; the value then still gets a chance
        // below.
        if (value.TryGetBool(out var b) && _data.ApplyBool(path, b)) return true;

        // String-valued paths (route href, autopilot state, ...).
        if (value.TryGetString(out var s) && _data.ApplyString(path, s)) return true;

        return _data.Apply(path, value);
    }

    private Outcome OnCallsign(DeltaValue value)
    {
        if (!value.TryGetString(out var callsign)) return Outcome.NotHandled;
        OwnCallsign = callsign;
        return Outcome.Handled;
    }

    private Outcome OnPosition(DeltaValue value)
    {
        if (!value.IsObject) return Outcome.NotHandled;
        if (!value.TryGetLatLon(out var lat, out var lon)) return Outcome.Handled;
        _data.ApplyPosition(lat, lon);
        return Outcome.Changed;
    }

    // Anchor position from signalk-anchoralarm-plugin. Delta shapes
    // observed from the plugin in the field:
    //   1. { lat, lon } object -> drop/update anchor
    //   2. JSON null, or the value property missing (some SK server
    //      versions) -> anchor weighed on another plotter ("Weigh
    //      Anchor" in the plugin UI, or freeboard-sk)
    // Anything else is ignored.
    private Outcome OnAnchorPosition(DeltaValue value)
    {
        if (value.TryGetLatLon(out var lat, out var lon))
        {
            _data.ApplyAnchorPosition(lat, lon);
            return Outcome.Changed;
        }
        if (value.IsNull)
        {
            _data.ClearAnchor();
            return Outcome.Changed;
        }
        return Outcome.Handled;
    }

    // Plugins also deactivate by nulling maxRadius (the alarm is only
    // armed when both position + radius are set), covering servers that
    // don't re-emit a null position on deactivation. A number is the
    // radius itself and goes through NavigationData.Apply.
    private Outcome OnAnchorMaxRadius(DeltaValue value)
    {
        if (!value.IsNull) return Outcome.NotHandled;
        _data.ClearAnchor();
        return Outcome.Changed;
    }

    // Route activation: signalk-server v2's course API publishes the
    // activation as ONE delta on navigation.course.activeRoute with
    // value { href, name, reverse, pointIndex, pointTotal } and does
    // not emit the leaf paths separately. Fan the object out to the
    // leaves so a route activated externally (freeboard, a second
    // plotter, a REST call) reaches ActiveRouteHref.
    private Outcome OnActiveRoute(DeltaValue value)
    {
        if (value.TryGetElement(out var ar) && ar.ValueKind == JsonValueKind.Object)
        {
            if (ar.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String)
                _data.ApplyString(SkPaths.Navigation.Course.ActiveRouteHref, href.GetString());
            if (ar.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                _data.ApplyString(SkPaths.Navigation.Course.ActiveRouteName, name.GetString());
            if (ar.TryGetProperty("pointIndex", out var idx) && idx.ValueKind == JsonValueKind.Number
                && idx.TryGetDouble(out var idxD))
                _data.Apply(SkPaths.Navigation.Course.ActiveRoutePointIndex, idxD);
            if (ar.TryGetProperty("pointTotal", out var tot) && tot.ValueKind == JsonValueKind.Number
                && tot.TryGetDouble(out var totD))
                _data.Apply(SkPaths.Navigation.Course.ActiveRoutePointTotal, totD);
            return Outcome.Changed;
        }
        return ClearCourseIfNull(value);
    }

    // Course next-point: the server publishes either the parent object
    // ({type, position: {...}}) on a state change or the bare position
    // leaf on updates; a null parent means the course was cleared.
    private Outcome OnNextPoint(DeltaValue value)
    {
        if (value.TryGetLatLon(out var lat, out var lon))
        {
            _data.ApplyCourseNextPointPosition(lat, lon);
            return Outcome.Changed;
        }
        return ClearCourseIfNull(value);
    }

    private Outcome OnNextPointPosition(DeltaValue value)
    {
        if (!value.IsObject) return Outcome.NotHandled;
        if (!value.TryGetLatLon(out var lat, out var lon)) return Outcome.Handled;
        _data.ApplyCourseNextPointPosition(lat, lon);
        return Outcome.Changed;
    }

    private Outcome OnPreviousPoint(DeltaValue value)
    {
        if (!value.TryGetLatLon(out var lat, out var lon)) return Outcome.NotHandled;
        _data.ApplyCoursePreviousPointPosition(lat, lon);
        return Outcome.Changed;
    }

    private Outcome OnPreviousPointPosition(DeltaValue value)
    {
        if (!value.IsObject) return Outcome.NotHandled;
        if (!value.TryGetLatLon(out var lat, out var lon)) return Outcome.Handled;
        _data.ApplyCoursePreviousPointPosition(lat, lon);
        return Outcome.Changed;
    }

    // Route deactivation: after DELETE /navigation/course the server
    // publishes one of two shapes depending on version, and both must
    // clear:
    //   (a) navigation.course.activeRoute.href = null (course-provider
    //       plugin; the leaf flips to null)
    //   (b) navigation.course.activeRoute = null OR
    //       navigation.course.nextPoint = null (signalk-server 2.x's
    //       course API nulls the parent instead of every leaf)
    // Other shapes fall through to the generic string / number paths.
    private Outcome ClearCourseIfNull(DeltaValue value)
    {
        if (!value.IsNull) return Outcome.NotHandled;
        _data.ClearCourse();
        return Outcome.Changed;
    }

    // Leg-advance flags from the course-provider plugin. Value is
    // {state, method, message} when armed, JSON null when the plugin
    // clears the alarm on exit. AutoAdvanceService edge-triggers on the
    // armed flag.
    //
    // Fail-safe: any state other than normal / cleared counts as armed,
    // and so does an object WITHOUT a state (some course-provider builds
    // omit it while active) and a bare `true` (some v3 builds). Better
    // to nuisance-fire once than miss a genuine arrival.
    //
    // Logged at Info so a helm who sees auto-advance fail can grep the
    // console for "CourseNotification" and tell plumbing (never arrives)
    // from downstream edge / cooldown logic.
    private Outcome OnCourseNotification(string path, DeltaValue value)
    {
        bool armed;
        if (value.IsObject)
        {
            armed = true;
            if (value.TryGetElement(out var ne)
                && ne.TryGetProperty("state", out var stateEl)
                && stateEl.ValueKind == JsonValueKind.String)
            {
                var state = stateEl.GetString();
                armed = !string.Equals(state, "normal", StringComparison.Ordinal)
                     && !string.Equals(state, "cleared", StringComparison.Ordinal);
            }
        }
        else
        {
            armed = value.TryGetBool(out var bare) && bare;
        }
        _logger.LogInformation("CourseNotification {Path} armed={Armed}", path, armed);
        _data.ApplyBool(path, armed);
        return Outcome.Changed;
    }
}
