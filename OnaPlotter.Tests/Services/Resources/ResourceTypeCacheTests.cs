using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services.Resources;

namespace OnaPlotter.Tests.Services.Resources;

/// <summary>
/// Direct tests for the generic <see cref="ResourceTypeCache{T}"/>
/// primitive that ResourceStore composes four instances of (one per
/// resource type). Pinning the primitive's behaviour separately gives
/// us early signal when a future change to the cache shape regresses
/// one of the four type-specific paths.
/// </summary>
public class ResourceTypeCacheTests
{
    private sealed record Entry(string Id, string Name);

    private static ResourceTypeCache<Entry> NewCache(string label = "test") =>
        new(label, NullLogger<ResourceTypeCache<Entry>>.Instance);

    [Test]
    public async Task Apply_Adds_Entry_And_Fires_Changed()
    {
        var cache = NewCache();
        var changed = new List<string>();
        cache.Changed += changed.Add;

        cache.Apply("a", new Entry("a", "Alpha"));

        await Assert.That(cache.Get("a")?.Name).IsEqualTo("Alpha");
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(changed).Contains("a");
    }

    [Test]
    public async Task Apply_Updates_Existing_Entry_And_Fires_Changed_Again()
    {
        var cache = NewCache();
        cache.Apply("a", new Entry("a", "Alpha"));

        var changed = new List<string>();
        cache.Changed += changed.Add;

        cache.Apply("a", new Entry("a", "Alpha-prime"));

        await Assert.That(cache.Get("a")?.Name).IsEqualTo("Alpha-prime");
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(changed).Contains("a");
    }

    [Test]
    public async Task Remove_Drops_Entry_And_Fires_Removed()
    {
        var cache = NewCache();
        cache.Apply("a", new Entry("a", "Alpha"));

        var removed = new List<string>();
        cache.Removed += removed.Add;

        var ok = cache.Remove("a");

        await Assert.That(ok).IsTrue();
        await Assert.That(cache.Get("a")).IsNull();
        await Assert.That(removed).Contains("a");
    }

    [Test]
    public async Task Remove_Unknown_Id_Returns_False_Without_Firing()
    {
        var cache = NewCache();
        var removed = new List<string>();
        cache.Removed += removed.Add;

        var ok = cache.Remove("ghost");

        await Assert.That(ok).IsFalse();
        await Assert.That(removed).IsEmpty();
    }

    [Test]
    public async Task Snapshot_Returns_Same_Reference_Until_Mutation()
    {
        var cache = NewCache();
        cache.Apply("a", new Entry("a", "Alpha"));

        var first = cache.Snapshot;
        var second = cache.Snapshot;
        await Assert.That(ReferenceEquals(first, second)).IsTrue();

        cache.Apply("b", new Entry("b", "Bravo"));
        var third = cache.Snapshot;
        await Assert.That(ReferenceEquals(first, third)).IsFalse();
        await Assert.That(third.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Replace_Reports_Added_Updated_Removed_Counts()
    {
        var cache = NewCache();
        cache.Apply("a", new Entry("a", "Alpha"));
        cache.Apply("b", new Entry("b", "Bravo"));

        // Fresh batch: a stays (updated), b drops (removed),
        // c is new (added).
        var fresh = new List<Entry>
        {
            new("a", "Alpha-prime"),
            new("c", "Charlie"),
        };

        var scratch = new HashSet<string>();
        var counts = cache.Replace(fresh, e => e.Id, scratch);

        await Assert.That(counts.Added).IsEqualTo(1);
        await Assert.That(counts.Updated).IsEqualTo(1);
        await Assert.That(counts.Removed).IsEqualTo(1);
        await Assert.That(cache.Get("a")?.Name).IsEqualTo("Alpha-prime");
        await Assert.That(cache.Get("b")).IsNull();
        await Assert.That(cache.Get("c")?.Name).IsEqualTo("Charlie");
    }

    [Test]
    public async Task Replace_Skips_Empty_Ids()
    {
        var cache = NewCache();
        var fresh = new List<Entry>
        {
            new("", "Nameless"),
            new("a", "Alpha"),
        };

        var counts = cache.Replace(fresh, e => e.Id, new HashSet<string>());

        await Assert.That(counts.Added).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Subscriber_Throw_Is_Logged_And_Swallowed()
    {
        // The cache's contract is "subscriber-throw doesn't propagate
        // out to the WS dispatch loop". Verify by wiring a throwing
        // handler and checking that Apply still completes + the entry
        // is still in the cache.
        var cache = NewCache();
        cache.Changed += _ => throw new InvalidOperationException("subscriber boom");

        cache.Apply("a", new Entry("a", "Alpha"));

        await Assert.That(cache.Get("a")?.Name).IsEqualTo("Alpha");
    }
}
