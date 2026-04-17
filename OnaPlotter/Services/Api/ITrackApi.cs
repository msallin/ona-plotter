namespace OnaPlotter.Services.Api;

/// <summary>
/// Fetches the server-stored historical track for own vessel.
/// Returns coordinates as Leaflet-ordered [lat, lon] pairs.
/// </summary>
public interface ITrackApi
{
    Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m", CancellationToken ct = default);
}
