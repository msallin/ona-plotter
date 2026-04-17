using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Reads available chart layers from the SignalK server.
/// Throws <see cref="HttpRequestException"/> on network failure; callers decide how to surface it.
/// </summary>
public interface IChartApi
{
    Task<List<SignalkChart>> GetAllAsync(CancellationToken ct = default);
}
