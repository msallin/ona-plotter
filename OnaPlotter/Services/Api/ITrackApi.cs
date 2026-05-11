using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

// TrackBbox moved to OnaPlotter.Models - it's a domain value type
// (geographic bounds), not an API-specific shape. Old call-sites
// imported it from this namespace; the using above + the relocated
// definition keeps them compiling.

/// <summary>
/// Fetches the server-stored historical track for own vessel from the
/// SignalK History API v2 (<c>/signalk/v2/api/history/values</c>).
/// Returns null on transport failure / empty windows / missing
/// provider so callers can show a "no data" hint instead of a stack
/// trace.
/// </summary>
public interface ITrackApi
{
    /// <summary>
    /// Position-only fetch, light-weight, used by the Map page's
    /// own-vessel track overlay. Returns coordinates as Leaflet-
    /// ordered <c>[lat, lon]</c> pairs.
    /// </summary>
    Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m", CancellationToken ct = default);

    /// <summary>
    /// Rich fetch including timestamps + SOG + (when the server has
    /// the path) wind. Used by the History page for segmentation
    /// (stationary vs moving) and per-segment stats. Either pass
    /// <paramref name="from"/> + <paramref name="to"/> for an absolute
    /// window, or pass <paramref name="timespan"/> for a relative one
    /// (matches the dropdown shorthand on the History page).
    /// <para>
    /// Multi-path query: the API returns time-aligned samples for
    /// every requested path in a single round-trip. Paths whose
    /// values are absent for a given tick come back as null at that
    /// row index; the parser keeps the position fix and drops just
    /// the missing field, so a sporadic SOG feed doesn't punch holes
    /// in the track polyline.
    /// </para>
    /// </summary>
    /// <param name="from">Inclusive start of the window. Pass null
    /// alongside null <paramref name="to"/> to fall back to
    /// <paramref name="timespan"/>.</param>
    /// <param name="to">Exclusive end of the window. When null with
    /// non-null <paramref name="from"/>, defaults to "now".</param>
    /// <param name="timespan">Relative duration shorthand (1h / 6h /
    /// 1d / 3d / 7d) used when <paramref name="from"/> is null.
    /// Ignored when <paramref name="from"/> is supplied.</param>
    /// <param name="resolution">Sampling cadence (30s, 1m, ...). Drives
    /// how aggressively the server downsamples; 30s is the helm-page
    /// default.</param>
    /// <param name="bbox">Optional geographic bounding box. When
    /// supplied, the History API call includes
    /// <c>&amp;bbox=south,west,north,east</c> as a hint so a server
    /// that supports it (signalk-parquet has the extension; others
    /// ignore unknown query params) can return only points inside
    /// the box. The History page passes the current viewport with a
    /// ~20 % outward pad so a small pan doesn't trigger an immediate
    /// re-fetch. Null = no bbox hint, server returns the full
    /// time-windowed track.</param>
    Task<TrackPoint[]?> GetServerTrackPointsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? timespan,
        string resolution = "30s",
        TrackBbox? bbox = null,
        TrackFetchPathSet pathSet = TrackFetchPathSet.Rich,
        CancellationToken ct = default);
}

/// <summary>Which SignalK paths the history fetch should request.
/// The server returns one column per requested path; everything that
/// isn't asked for stays off the wire AND off the parser. Pick
/// whichever variant covers what the caller actually consumes - the
/// Map's server-track overlay only colours by SOG, while the History
/// + Stats pages need COG / heading / wind too.</summary>
public enum TrackFetchPathSet
{
    /// <summary>Position + SOG only. Used by the Map's server-track
    /// overlay (<c>ServerTrackController</c>) where the JS renderer
    /// colours each segment by speed bucket. Cuts the request payload
    /// + the server response by ~4 columns per row on a busy harbour
    /// fetch and stops the parser from materialising fields the caller
    /// discards.</summary>
    MapTrack,
    /// <summary>Position + SOG + COG + heading + true wind speed +
    /// true wind angle. Used by the History page (segmenter, trip
    /// detail panel) and the Stats page (wind avg per segment).
    /// Default so an unsuspecting new caller gets the full shape
    /// rather than a half-populated <see cref="TrackPoint"/>.</summary>
    Rich,
}

// TrackBbox is now in OnaPlotter.Models (see TrackBbox.cs).
