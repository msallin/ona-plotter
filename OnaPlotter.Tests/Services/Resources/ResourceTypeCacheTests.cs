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

    // --- Content-equality dedup --------------------------------------
    //
    // These tests pin the "skip Changed when the incoming entry is
    // byte-identical to the cached one" behaviour that the route-storm
    // crash fix relies on. A regression here would let the per-handler
    // drains in Map.razor still receive N events on reconnect even
    // though the content didn't change.

    [Test]
    public async Task Apply_Skips_Changed_When_Content_Equal()
    {
        var cache = new ResourceTypeCache<Entry>(
            "test",
            NullLogger<ResourceTypeCache<Entry>>.Instance,
            (a, b) => a.Name == b.Name);
        cache.Apply("a", new Entry("a", "Alpha"));

        var changed = new List<string>();
        cache.Changed += changed.Add;

        // Same Name -> equality function returns true -> no Changed.
        cache.Apply("a", new Entry("a", "Alpha"));

        await Assert.That(changed).IsEmpty();
        await Assert.That(cache.Get("a")?.Name).IsEqualTo("Alpha");
    }

    [Test]
    public async Task Apply_Fires_Changed_When_Content_Differs()
    {
        var cache = new ResourceTypeCache<Entry>(
            "test",
            NullLogger<ResourceTypeCache<Entry>>.Instance,
            (a, b) => a.Name == b.Name);
        cache.Apply("a", new Entry("a", "Alpha"));

        var changed = new List<string>();
        cache.Changed += changed.Add;

        cache.Apply("a", new Entry("a", "Alpha-prime"));

        await Assert.That(changed).Contains("a");
        await Assert.That(cache.Get("a")?.Name).IsEqualTo("Alpha-prime");
    }

    [Test]
    public async Task Replace_Skips_Changed_For_Unchanged_Entries()
    {
        // The reconnect-edge use case: server returns the SAME entries
        // it already had cached. With dedup, Replace should not fire
        // Changed for any of them - the page-side drain receives no
        // events, so no fire-and-forget redraw tasks launch.
        var cache = new ResourceTypeCache<Entry>(
            "test",
            NullLogger<ResourceTypeCache<Entry>>.Instance,
            (a, b) => a.Name == b.Name);
        cache.Apply("a", new Entry("a", "Alpha"));
        cache.Apply("b", new Entry("b", "Bravo"));
        cache.Apply("c", new Entry("c", "Charlie"));

        var changed = new List<string>();
        cache.Changed += changed.Add;

        var fresh = new List<Entry>
        {
            new("a", "Alpha"),
            new("b", "Bravo"),
            new("c", "Charlie"),
        };
        var counts = cache.Replace(fresh, e => e.Id, new HashSet<string>());

        await Assert.That(changed).IsEmpty();
        // All three are tracked as updated (they ARE in the fresh
        // snapshot) so the reconcile log still reports the right
        // total.
        await Assert.That(counts.Updated).IsEqualTo(3);
        await Assert.That(counts.Added).IsEqualTo(0);
        await Assert.That(counts.Removed).IsEqualTo(0);
    }

    [Test]
    public async Task Replace_Fires_Changed_Only_For_Genuine_Diffs()
    {
        // Mixed batch: some entries are unchanged, one mutated, one
        // is new. Changed should fire only for the mutated id and the
        // new id.
        var cache = new ResourceTypeCache<Entry>(
            "test",
            NullLogger<ResourceTypeCache<Entry>>.Instance,
            (a, b) => a.Name == b.Name);
        cache.Apply("a", new Entry("a", "Alpha"));
        cache.Apply("b", new Entry("b", "Bravo"));

        var changed = new List<string>();
        cache.Changed += changed.Add;

        var fresh = new List<Entry>
        {
            new("a", "Alpha"),         // unchanged - no Changed
            new("b", "Bravo-prime"),   // mutated - fires Changed
            new("c", "Charlie"),       // new - fires Changed
        };
        cache.Replace(fresh, e => e.Id, new HashSet<string>());

        await Assert.That(changed).Contains("b");
        await Assert.That(changed).Contains("c");
        await Assert.That(changed).DoesNotContain("a");
        await Assert.That(changed.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Apply_Without_Equality_Comparer_Always_Fires_Changed()
    {
        // Backward-compat: callers that don't pass a comparer (the old
        // signature) get the original "fire Changed on every upsert"
        // behaviour. Tests + non-resource consumers should still work.
        var cache = NewCache();
        cache.Apply("a", new Entry("a", "Alpha"));

        var changed = new List<string>();
        cache.Changed += changed.Add;

        cache.Apply("a", new Entry("a", "Alpha"));

        await Assert.That(changed).Contains("a");
    }
}
