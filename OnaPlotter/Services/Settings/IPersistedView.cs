namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for the "remember where the helm was looking"
/// persistence. All three values are nullable - on a fresh device
/// or after a parse failure the loader logs a warning and the map
/// falls back to live-position centre.
///
/// <para>Carved from <see cref="IAppSettings"/> as part of ARCH-003.
/// </para>
/// </summary>
public interface IPersistedView
{
    /// <summary>Last known map view centre latitude.</summary>
    double? MapViewLat { get; }

    /// <summary>Last known map view centre longitude.</summary>
    double? MapViewLon { get; }

    /// <summary>Last known map zoom level (Leaflet integer).</summary>
    int? MapViewZoom { get; }

    /// <summary>Persist the map centre + zoom so the next session
    /// opens where the user left off.</summary>
    Task SetMapViewAsync(double lat, double lon, int zoom);
}
