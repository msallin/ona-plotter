using System.Text.Json;
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
/// invisible -- the subscription stream only replays deltas on
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
/// activeRoute / nextPoint / previousPoint are nulls when no course
/// is active; we no-op in that case. calcValues.* numbers are NOT in
/// this body -- they come from the delta stream which the
/// course-provider plugin re-emits every tick as the boat moves, so
/// they trickle in normally.
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
            if (doc.RootElement.TryGetProperty("activeRoute", out var ar)
                && ar.ValueKind == JsonValueKind.Object)
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
            if (doc.RootElement.TryGetProperty("nextPoint", out var np)
                && np.ValueKind == JsonValueKind.Object
                && np.TryGetProperty("position", out var npPos)
                && npPos.ValueKind == JsonValueKind.Object
                && npPos.TryGetProperty("latitude", out var npLat)
                && npPos.TryGetProperty("longitude", out var npLon)
                && npLat.ValueKind == JsonValueKind.Number
                && npLon.ValueKind == JsonValueKind.Number)
            {
                _data.ApplyCourseNextPointPosition(npLat.GetDouble(), npLon.GetDouble());
                seeded = true;
            }

            if (doc.RootElement.TryGetProperty("previousPoint", out var pp)
                && pp.ValueKind == JsonValueKind.Object
                && pp.TryGetProperty("position", out var ppPos)
                && ppPos.ValueKind == JsonValueKind.Object
                && ppPos.TryGetProperty("latitude", out var ppLat)
                && ppPos.TryGetProperty("longitude", out var ppLon)
                && ppLat.ValueKind == JsonValueKind.Number
                && ppLon.ValueKind == JsonValueKind.Number)
            {
                _data.ApplyCoursePreviousPointPosition(ppLat.GetDouble(), ppLon.GetDouble());
                seeded = true;
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
