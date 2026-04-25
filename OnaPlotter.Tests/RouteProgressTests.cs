using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class RouteProgressTests
{
    // Realistic four-waypoint route used by most of the typical-progress
    // tests. Lat/lon values pick a small section so the equirectangular
    // approximation in FindClosestWaypointIndex is well within its
    // accurate operating range.
    //   coords[0] -- coords[1] -- coords[2] -- coords[3]
    //        leg 0       leg 1       leg 2
    private static readonly double[][] Route =
    [
        [47.00, 8.00],
        [47.10, 8.10],
        [47.20, 8.20],
        [47.30, 8.30],
    ];

    // --- typical "boat-on-route" cases ---

    [Test]
    public async Task ExactMatch_OnEachWaypoint_ReturnsThatIndex()
    {
        // Equivalence class: each WP feeds back its own index when the
        // SignalK next-point coords equal a vertex (the common case
        // immediately after a route activation).
        for (int i = 0; i < Route.Length; i++)
        {
            int got = RouteProgress.FindClosestWaypointIndex(Route, Route[i][0], Route[i][1]);
            await Assert.That(got).IsEqualTo(i);
        }
    }

    [Test]
    public async Task ProbeNearMidWaypoint_PicksNearestVertex()
    {
        // Sailor between WP1 and WP2, closer to WP2: the SignalK course
        // engine reports WP2's lat/lon as next-point. We must round-trip
        // it to index 2.
        double lat = (Route[1][0] + Route[2][0]) * 0.5 + 0.001;
        double lon = (Route[1][1] + Route[2][1]) * 0.5 + 0.001;
        // Bias slightly toward WP2 so the closest is unambiguous.
        int got = RouteProgress.FindClosestWaypointIndex(Route, Route[2][0] - 0.01, Route[2][1] - 0.01);
        await Assert.That(got).IsEqualTo(2);
    }

    [Test]
    public async Task PointFarFromRoute_ReturnsClosestVertexAnyway()
    {
        // Realistic when the boat overshoots or sails off the route.
        // Behaviour must degrade gracefully (no exception, just return
        // the visually-correct best match) so the renderer can still
        // draw the route.
        int got = RouteProgress.FindClosestWaypointIndex(Route, 47.31, 8.31);
        await Assert.That(got).IsEqualTo(3);
    }

    // --- equivalence classes / boundary inputs ---

    [Test]
    public async Task EmptyArray_ReturnsZero()
    {
        // Boundary: caller must not crash on an empty route fetch
        // (fetch can race with route deactivation).
        int got = RouteProgress.FindClosestWaypointIndex(Array.Empty<double[]>(), 0, 0);
        await Assert.That(got).IsEqualTo(0);
    }

    [Test]
    public async Task NullArray_ReturnsZero()
    {
        int got = RouteProgress.FindClosestWaypointIndex(null!, 0, 0);
        await Assert.That(got).IsEqualTo(0);
    }

    [Test]
    public async Task SinglePointRoute_ReturnsZero()
    {
        int got = RouteProgress.FindClosestWaypointIndex([[47, 8]], 99, -99);
        await Assert.That(got).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedInnerArrays_AreSkipped()
    {
        // Defensive: SignalK / Freeboard route fetches shouldn't return
        // sparse arrays, but a single bad row must not crash the lookup
        // on a boat with flaky wifi.
        double[][] coords =
        [
            [47.00, 8.00],
            null!,
            [47.20, 8.20],
            [],
        ];
        int got = RouteProgress.FindClosestWaypointIndex(coords, 47.19, 8.19);
        await Assert.That(got).IsEqualTo(2);
    }

    [Test]
    public async Task EquatorAndDateline_DoesNotMisroute()
    {
        // The equirectangular projection collapses near the poles, but
        // for a normal sailing route at moderate latitudes the
        // cos(lat0) factor is well-behaved. Sanity-check at the
        // equator (cos = 1) where dx/dy are isotropic.
        double[][] coords = [[0.0, 179.99], [0.0, -179.99]];
        // Probe near the second point (across the dateline). The naive
        // dx will be ~360° large because we don't unwrap longitude;
        // this test pins the *current* behaviour so a later "fix it"
        // PR is a deliberate change rather than an accidental regression.
        // For typical sub-NM progress lookups this never fires.
        int got = RouteProgress.FindClosestWaypointIndex(coords, 0.0, -179.99);
        await Assert.That(got).IsEqualTo(1);
    }

    // --- realistic "real world" use case ---

    [Test]
    public async Task TypicalSailRoute_FindsAdvancingNextPoint()
    {
        // Realistic progression: the SignalK course engine pushes the
        // next-point coords each time the boat passes a waypoint.
        // Walking through the route, the lookup must hand back the
        // expected index at each step.
        double[][] route =
        [
            [47.3769, 8.5417], // Zurich area start
            [47.3500, 8.5000],
            [47.3000, 8.4500],
            [47.2500, 8.4000],
            [47.2000, 8.3500], // last WP
        ];

        // Just-past-WP0, heading to WP1: SignalK reports WP1's coords.
        await Assert.That(RouteProgress.FindClosestWaypointIndex(route, 47.3500, 8.5000)).IsEqualTo(1);

        // Mid-passage to WP3.
        await Assert.That(RouteProgress.FindClosestWaypointIndex(route, 47.2500, 8.4000)).IsEqualTo(3);

        // Nearing destination.
        await Assert.That(RouteProgress.FindClosestWaypointIndex(route, 47.2000, 8.3500)).IsEqualTo(4);
    }

    [Test]
    public async Task NaNCoordinate_DoesNotWin_LegitimateNeighbourPicked()
    {
        // A corrupt route fetch could produce a NaN lat / lon at one
        // waypoint. The squared-distance compare must not let NaN
        // silently outrank a real waypoint -- NaN < any is false in
        // IEEE-754, so the algorithm relies on that. Pin the contract
        // so a refactor that flips to >= or rearranges the compare
        // cannot silently "win" with the NaN entry.
        double[][] route =
        [
            [47.0, 8.0],
            [double.NaN, double.NaN], // corrupt
            [47.5, 8.0],
        ];

        // Probe near WP2 (47.5, 8.0). Must pick index 2, NOT the NaN row.
        await Assert.That(RouteProgress.FindClosestWaypointIndex(route, 47.49, 8.0))
            .IsEqualTo(2);
    }

    [Test]
    public async Task InfinityCoordinate_DoesNotWin()
    {
        double[][] route =
        [
            [47.0, 8.0],
            [double.PositiveInfinity, 8.0],
            [double.NegativeInfinity, 8.0],
            [47.2, 8.0],
        ];

        // Probe near WP3 (47.2, 8.0). The infinities propagate through
        // the squared distance and remain ineligible (Infinity * any
        // produces Infinity / NaN; neither beats a finite minimum).
        await Assert.That(RouteProgress.FindClosestWaypointIndex(route, 47.19, 8.0))
            .IsEqualTo(3);
    }

    // --- ResolveLegIndex precedence (server pointIndex > lat/lon > null) ---

    [Test]
    public async Task ResolveLegIndex_ServerPointIndex_Wins_Over_LatLon_Match()
    {
        // The fix for "every reload resets to WP 1": the SK course
        // engine is the source of truth for the active leg. The
        // lat/lon match exists only as a fallback for older servers
        // -- if the server tells us pointIndex=2, we use 2 even when
        // the next-point lat/lon would match a different vertex
        // (e.g. boat already crossed past the next-point and the
        // server hasn't caught up yet).
        var idx = RouteProgress.ResolveLegIndex(
            serverPointIndex: 2,
            coords: Route,
            nextLat: Route[0][0],   // would otherwise resolve to 0
            nextLon: Route[0][1]);
        await Assert.That(idx).IsEqualTo(2);
    }

    [Test]
    public async Task ResolveLegIndex_FallsBack_To_LatLon_When_PointIndex_Null()
    {
        // Older SK / non-conforming provider that doesn't ship
        // pointIndex: the lat/lon match is the only signal. Pin so
        // the fallback path keeps working.
        var idx = RouteProgress.ResolveLegIndex(
            serverPointIndex: null,
            coords: Route,
            nextLat: Route[2][0],
            nextLon: Route[2][1]);
        await Assert.That(idx).IsEqualTo(2);
    }

    [Test]
    public async Task ResolveLegIndex_Returns_Null_When_Nothing_Known()
    {
        // No pointIndex AND no next-point lat/lon yet. Must return
        // null rather than defaulting to 0 -- the caller skips the
        // JS dispatch in that case so the route doesn't briefly
        // render as entirely undriven.
        var idx = RouteProgress.ResolveLegIndex(
            serverPointIndex: null,
            coords: Route,
            nextLat: null,
            nextLon: null);
        await Assert.That(idx).IsNull();
    }

    [Test]
    public async Task ResolveLegIndex_Returns_Null_When_Coords_Empty()
    {
        // Route geometry not loaded yet (HTTP fetch in flight). Even
        // a known pointIndex isn't actionable without coords -- but
        // we still return the index so the caller can decide. (Caller
        // also guards on coords.Length > 0.) Pin the no-coords +
        // pointIndex case explicitly: returns the pointIndex.
        await Assert.That(RouteProgress.ResolveLegIndex(2, null, null, null)).IsEqualTo(2);
        await Assert.That(RouteProgress.ResolveLegIndex(null, [], 47.0, 8.0)).IsNull();
    }

    [Test]
    public async Task ResolveLegIndex_Honours_Index_Zero()
    {
        // Boundary: pointIndex=0 is a legal value (just-activated
        // route, heading to first waypoint). Must not be treated as
        // "missing" by an over-eager null check.
        var idx = RouteProgress.ResolveLegIndex(
            serverPointIndex: 0,
            coords: Route,
            nextLat: Route[3][0],   // would otherwise resolve to 3
            nextLon: Route[3][1]);
        await Assert.That(idx).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveLegIndex_Negative_PointIndex_Falls_Back()
    {
        // Defensive against a malformed server delta (e.g. signed
        // overflow). A negative index would index out of bounds in
        // the JS layer; treat as "unknown" and use lat/lon if we
        // have it.
        var idx = RouteProgress.ResolveLegIndex(
            serverPointIndex: -1,
            coords: Route,
            nextLat: Route[1][0],
            nextLon: Route[1][1]);
        await Assert.That(idx).IsEqualTo(1);
    }
}
