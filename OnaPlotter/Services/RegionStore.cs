using OnaPlotter.Models;
using OnaPlotter.Services.Resources;

namespace OnaPlotter.Services;

/// <summary>
/// In-memory <see cref="IRegionStore"/>. Single-threaded WASM context,
/// so no locking. The list reference is replaced on every
/// <see cref="SetRegions"/> call rather than mutated in place; consumers
/// that hold a reference between ticks see the snapshot they captured
/// (no torn read).
///
/// <para><b>Auto-sync from <see cref="ResourceStore"/></b>: when a
/// <see cref="ResourceStore"/> is injected at construction, this store
/// subscribes to its region change / remove / reload events and
/// updates <see cref="_regions"/> automatically. The legacy
/// <see cref="SetRegions"/> path stays for transitional callers (e.g.
/// Map.razor mirroring on local CRUD), but a future cleanup can drop
/// the manual mirror entirely. The subscription means
/// <see cref="OnaPlotter.Services.Alarms.HazardousRegionAlarmRule"/>
/// sees the latest regions even when Map.razor is unmounted - the
/// previous shape (mirror only from Map.razor) silently broke the
/// alarm pipeline whenever the helm was on Settings / Wind / etc.
/// during a remote region edit.</para>
/// </summary>
public sealed class RegionStore : IRegionStore, IAsyncDisposable
{
    private readonly ResourceStore? _resources;
    private IReadOnlyList<SignalkRegion> _regions = Array.Empty<SignalkRegion>();

    public IReadOnlyList<SignalkRegion> Regions => _regions;

    public RegionStore() { }

    public RegionStore(ResourceStore resources)
    {
        _resources = resources;
        // Seed from the store's current state in case regions are
        // already cached (initial REST load may have completed
        // before this constructor ran). Subscribe for further
        // updates so the alarm rule sees them whether Map.razor is
        // mounted or not.
        _regions = resources.Regions;
        resources.OnRegionChanged += HandleRegionChanged;
        resources.OnRegionRemoved += HandleRegionRemoved;
        resources.OnReloaded += HandleReloaded;
    }

    public void SetRegions(IReadOnlyList<SignalkRegion>? regions)
    {
        _regions = regions ?? Array.Empty<SignalkRegion>();
    }

    private void HandleRegionChanged(string id) => RefreshFromResources();
    private void HandleRegionRemoved(string id) => RefreshFromResources();
    private void HandleReloaded() => RefreshFromResources();

    private void RefreshFromResources()
    {
        if (_resources is null) return;
        _regions = _resources.Regions;
    }

    public ValueTask DisposeAsync()
    {
        if (_resources is not null)
        {
            _resources.OnRegionChanged -= HandleRegionChanged;
            _resources.OnRegionRemoved -= HandleRegionRemoved;
            _resources.OnReloaded -= HandleReloaded;
        }
        return ValueTask.CompletedTask;
    }
}
