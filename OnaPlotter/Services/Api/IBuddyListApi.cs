using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Thin client for the REST API exposed by
/// <a href="https://github.com/sbender9/signalk-buddylist-plugin">
/// sbender9/signalk-buddylist-plugin</a>. The plugin is optional - callers
/// should check <see cref="IsAvailableAsync"/> (or treat a null response
/// from <see cref="GetAllAsync"/>) before wiring UI that depends on it.
/// </summary>
public interface IBuddyListApi
{
    /// <summary>
    /// True if the plugin is installed and reachable. Result is cached
    /// for the lifetime of the process - call <see cref="InvalidateAsync"/>
    /// to re-probe after the user enables a plugin at runtime.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current buddy list, or null if the plugin is not
    /// installed. Wraps a GET to <see cref="SignalKUrls.BuddiesPath"/>.
    /// </summary>
    Task<IReadOnlyList<SignalkBuddy>?> GetAllAsync(CancellationToken ct = default);

    /// <summary>Forces the next call to re-probe instead of using the cache.</summary>
    void InvalidateAsync();

    /// <summary>Adds a buddy by its SignalK URN (e.g.
    /// <c>urn:mrn:imo:mmsi:338246284</c>) and friendly name. Returns true on
    /// success, false if the plugin isn't installed or the server rejected
    /// the write (e.g. auth required).</summary>
    Task<bool> AddAsync(string urn, string name, CancellationToken ct = default);

    /// <summary>Removes a buddy by URN. Same return contract as
    /// <see cref="AddAsync"/>.</summary>
    Task<bool> RemoveAsync(string urn, CancellationToken ct = default);
}
