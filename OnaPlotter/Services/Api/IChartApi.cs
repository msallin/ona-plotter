using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Reads available chart layers from the SignalK server, prefixed by
/// the built-in basemaps. Server failures degrade to the built-ins
/// alone; only caller cancellation throws.
/// </summary>
public interface IChartApi
{
    Task<List<SignalkChart>> GetAllAsync(CancellationToken ct = default);
}
