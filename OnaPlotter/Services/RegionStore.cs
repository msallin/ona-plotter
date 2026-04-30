using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// In-memory <see cref="IRegionStore"/>. Single-threaded WASM context,
/// so no locking. The list reference is replaced on every
/// <see cref="SetRegions"/> call rather than mutated in place; consumers
/// that hold a reference between ticks see the snapshot they captured
/// (no torn read).
/// </summary>
public sealed class RegionStore : IRegionStore
{
    private IReadOnlyList<SignalkRegion> _regions = Array.Empty<SignalkRegion>();

    public IReadOnlyList<SignalkRegion> Regions => _regions;

    public void SetRegions(IReadOnlyList<SignalkRegion>? regions)
    {
        _regions = regions ?? Array.Empty<SignalkRegion>();
    }
}
