namespace OnaPlotter.Services.Places;

/// <summary>
/// One row in the place-search dropdown. Provider-agnostic so the
/// caller (the topbar SearchBox component) doesn't have to know
/// whether the row came from Photon, Nominatim, or an own-data
/// store like waypoints / regions / notes.
/// </summary>
/// <param name="Name">Short label -- typically the place's primary
/// name. Used as the dropdown row's leading text.</param>
/// <param name="DisplayLabel">Longer human label combining name +
/// city + country (or own-data type + parent context). Renders as
/// the secondary line under <see cref="Name"/>.</param>
/// <param name="Lat">WGS-84 latitude.</param>
/// <param name="Lon">WGS-84 longitude.</param>
/// <param name="Source">Provider tag: <c>"photon"</c>,
/// <c>"nominatim"</c>, <c>"waypoint"</c>, etc. Lets the dropdown
/// render a tiny source-badge so the helm can tell at a glance
/// whether a hit came from their own vault or an online geocoder.</param>
public sealed record PlaceResult(
    string Name,
    string DisplayLabel,
    double Lat,
    double Lon,
    string Source);
