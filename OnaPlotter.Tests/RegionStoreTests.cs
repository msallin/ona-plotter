using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pin the contract <see cref="HazardousRegionAlarmRule"/> depends on:
/// <see cref="RegionStore.SetRegions"/> replaces the snapshot reference
/// (no in-place mutation, so a rule that captured the previous list
/// for a tick still sees a consistent view), and a null push collapses
/// to empty without throwing.
/// </summary>
public class RegionStoreTests
{
    [Test]
    public async Task DefaultsToEmpty()
    {
        var store = new RegionStore();
        await Assert.That(store.Regions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetRegions_ReplacesSnapshot()
    {
        var store = new RegionStore();
        var first = new[] { new SignalkRegion { Id = "a" } };
        store.SetRegions(first);
        await Assert.That(store.Regions.Count).IsEqualTo(1);
        await Assert.That(store.Regions[0].Id).IsEqualTo("a");

        var second = new[]
        {
            new SignalkRegion { Id = "x" },
            new SignalkRegion { Id = "y" },
        };
        store.SetRegions(second);
        await Assert.That(store.Regions.Count).IsEqualTo(2);
        await Assert.That(store.Regions[0].Id).IsEqualTo("x");
    }

    [Test]
    public async Task SetRegions_NullCollapsesToEmpty()
    {
        // Failed REST round-trip yields null; the store must not
        // throw and the next consumer read must be safe.
        var store = new RegionStore();
        store.SetRegions(new[] { new SignalkRegion { Id = "a" } });
        store.SetRegions(null);
        await Assert.That(store.Regions.Count).IsEqualTo(0);
    }
}
