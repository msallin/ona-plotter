using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK waypoints.</summary>
public interface IWaypointApi
{
    Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default);
    Task<string?> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
