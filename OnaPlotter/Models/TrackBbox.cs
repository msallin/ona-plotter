namespace OnaPlotter.Models;

/// <summary>
/// Geographic bounding box. Decimal degrees;
/// <see cref="South"/> &lt; <see cref="North"/>,
/// <see cref="West"/> &lt; <see cref="East"/> for a non-anti-meridian-
/// spanning box. Anti-meridian crossings aren't a concern for cruising
/// in inland Europe / Mediterranean / Caribbean where this code runs;
/// if a future passage hits 180°, the helper that builds the bbox can
/// split into two requests.
/// <para>
/// Used as the optional <c>bbox</c> hint on the SignalK History API
/// fetch (<see cref="OnaPlotter.Services.Api.ITrackApi.GetServerTrackPointsAsync"/>),
/// and by the future Stats page to express "the area I'm asking
/// about". Keeping it in <c>Models</c> rather than under
/// <c>Services.Api</c> means new consumers can reach for the type
/// without depending on the API surface.
/// </para>
/// </summary>
public readonly record struct TrackBbox(
    double South,
    double West,
    double North,
    double East)
{
    /// <summary>Default outward-pad fraction used by the History
    /// page's viewport probe. 20 % per axis means the box only "moves"
    /// once the helm pans ~10 % of the viewport off-screen, so a
    /// small pan doesn't trigger a re-fetch.</summary>
    public const double DefaultPadFraction = 0.20;

    /// <summary>Returns a copy expanded outward by
    /// <paramref name="fraction"/> of each axis. <c>0.20</c> grows a
    /// 1°×1° box to 1.4°×1.4°. Used by the History viewport probe so
    /// the bbox hint covers slightly more than the visible map and
    /// the helm can pan a little before a re-fetch is needed.</summary>
    public TrackBbox Pad(double fraction)
    {
        double dLat = (North - South) * fraction;
        double dLon = (East - West) * fraction;
        return new TrackBbox(
            South: South - dLat, West: West - dLon,
            North: North + dLat, East: East + dLon);
    }
}
