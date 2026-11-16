using System.Text.Json;
using OnaPlotter.Utilities;
using Microsoft.Extensions.Logging;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// One-shot REST fetch of the SignalK v2 Course API
/// (<c>/signalk/v2/api/vessels/self/navigation/course</c>). The v1
/// self-tree doesn't expose this endpoint at all, which is why a
/// route activated BEFORE OnaPlotter connects (another plotter, a
/// previous browser session, freeboard-sk in a second tab) stayed
/// invisible - the subscription stream only replays deltas on
/// change, and the activeRoute href hadn't changed since we
/// connected.
///
/// <para>
/// Response shape (per SignalK Course API v2 spec): flat object,
/// unwrapped scalars.
/// <code>
/// {
///   "startTime": "...", "targetArrivalTime": "...", "arrivalCircle": 4000,
///   "activeRoute": {"href": "...", "pointIndex": 0, "pointTotal": 5,
///                    "reverse": false, "name": "..."},
///   "nextPoint": {"type": "RoutePoint", "position": {"latitude": ..., "longitude": ...}},
///   "previousPoint": {"position": {...}}
/// }
/// </code>
/// activeRoute / nextPoint / previousPoint are null (or absent) when no
/// course is active. The seed is authoritative for those three: a field
/// that is absent or null clears the matching local state, because a
/// course cleared on the server while this plotter was offline produces
/// no delta to replay. calcValues.* numbers are NOT in this body - they
/// come from the delta stream which the course-provider plugin re-emits
/// every tick as the boat moves, so they trickle in normally; they are
/// left untouched while a course stays active so the auto-advance edge
/// detector doesn't see a reset leg flag as a fresh edge.
/// </para>
/// </summary>
public sealed class SignalkCourseSeeder
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly NavigationData _data;
    private readonly ILogger<SignalkCourseSeeder> _logger;
    private readonly Action _onDataChanged;

    public SignalkCourseSeeder(
        HttpClient http,
        ISignalKBaseUrl baseUrl,
        NavigationData data,
        ILogger<SignalkCourseSeeder> logger,
        Action onDataChanged)
    {
        _http = http;
        _baseUrl = baseUrl;
        _data = data;
        _logger = logger;
        _onDataChanged = onDataChanged;
    }

    /// <summary>
    /// Fetch the v2 course endpoint and apply activeRoute / nextPoint /
    /// previousPoint to <see cref="NavigationData"/>. Best-effort: 404
    /// (no v2 Course API or no course-provider plugin) is silent at
    /// Debug level; an unexpected exception is logged at Warning.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        JsonDocument? doc = null;
        try
        {
            var url = _baseUrl.Combine("/signalk/v2/api/vessels/self/navigation/course");
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                // 404 is expected on servers without the v2 Course API
                // or course-provider plugin; log at Debug to keep the
                // noise floor low.
                _logger.LogDebug("v2 course endpoint {Url} returned {Status}", url, (int)res.StatusCode);
                return;
            }

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            bool seeded = false;

            // activeRoute: an object when a route is active, null when
            // cleared. Members mirror the SK v2 spec above; OnaPlotter
            // tracks href / name / pointIndex / pointTotal.
            bool hasActiveRoute = doc.RootElement.TryGetProperty("activeRoute", out var ar)
                && ar.ValueKind == JsonValueKind.Object;
            if (hasActiveRoute)
            {
                if (ar.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String)
                    seeded |= _data.ApplyString(OnaPlotter.Utilities.SkPaths.Navigation.Course.ActiveRouteHref, href.GetString());
                if (ar.TryGetProperty("name", out var rn) && rn.ValueKind == JsonValueKind.String)
                    seeded |= _data.ApplyString(OnaPlotter.Utilities.SkPaths.Navigation.Course.ActiveRouteName, rn.GetString());
                if (ar.TryGetProperty("pointIndex", out var pi) && pi.ValueKind == JsonValueKind.Number)
                    seeded |= _data.Apply(OnaPlotter.Utilities.SkPaths.Navigation.Course.ActiveRoutePointIndex, pi);
                if (ar.TryGetProperty("pointTotal", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    seeded |= _data.Apply(OnaPlotter.Utilities.SkPaths.Navigation.Course.ActiveRoutePointTotal, pt);
            }

            // nextPoint / previousPoint: objects carrying position and
            // a type discriminator ("RoutePoint" / "Waypoint" / ...).
            // Only position matters to the HUD; type is informational.
            bool hasNext = false, hasPrevious = false;
            if (doc.RootElement.TryGetProperty("nextPoint", out var np)
                && np.TryGetLatLon(out var npLat, out var npLon))
            {
                _data.ApplyCourseNextPointPosition(npLat, npLon);
                hasNext = true;
                seeded = true;
            }

            if (doc.RootElement.TryGetProperty("previousPoint", out var pp)
                && pp.TryGetLatLon(out var ppLat, out var ppLon))
            {
                _data.ApplyCoursePreviousPointPosition(ppLat, ppLon);
                hasPrevious = true;
                seeded = true;
            }

            // arrivalCircle: per-leg radius in metres set by the route
            // author / course-provider. Seeding it lets the chart's
            // arrival ring + the client APPROACH alarm threshold use
            // the server's value on the very first frame after connect,
            // rather than waiting for the next delta to land.
            if (doc.RootElement.TryGetProperty("arrivalCircle", out var ac)
                && ac.ValueKind == JsonValueKind.Number)
            {
                seeded |= _data.Apply(
                    OnaPlotter.Utilities.SkPaths.Navigation.Course.ArrivalCircle, ac);
            }

            // Absent course fields clear local state (see class summary).
            // No next point means no course at all: drop everything,
            // calcValues included. Otherwise clear only the parts the
            // server no longer reports.
            if (!hasNext)
            {
                bool hadCourse = _data.HasActiveCourse || _data.HasPreviousPoint
                    || _data.ActiveRouteHref is not null || _data.ActiveRoutePointIndex is not null;
                if (hadCourse)
                {
                    _data.ClearCourse();
                    seeded = true;
                }
            }
            else
            {
                if (!hasActiveRoute) seeded |= _data.ClearActiveRoute();
                if (!hasPrevious) seeded |= _data.ClearCoursePreviousPoint();
            }

            if (seeded)
            {
                _onDataChanged.Invoke();
                _logger.LogInformation("Seeded active course from v2 REST (href={Href}, nextPoint={HasNext})",
                    _data.ActiveRouteHref ?? "(none)",
                    _data.HasActiveCourse);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "v2 course REST seed failed (active route won't appear until the next delta)");
        }
        finally { doc?.Dispose(); }
    }
}
