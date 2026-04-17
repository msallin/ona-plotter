namespace OnaPlotter.Services.Api;

/// <summary>Discovers available SignalK data paths on the own vessel tree.</summary>
public interface IPathApi
{
    Task<List<string>> GetAvailablePathsAsync(CancellationToken ct = default);
}
