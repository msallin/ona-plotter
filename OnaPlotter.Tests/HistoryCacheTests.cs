using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="HistoryCache"/>'s contract: stable round-trip,
/// LRU eviction order, the live-tail skip rule, and bbox key
/// distinction. Each test lives in its own cache so leaked state
/// between tests can't mask a regression.
/// </summary>
public class HistoryCacheTests
{
    /// <summary>Minimal test TimeProvider. Frozen "now" lets the
    /// live-tail rule be exercised deterministically without
    /// pulling the Microsoft.Extensions.TimeProvider.Testing
    /// package just for two test files. Mutable so a single test
    /// can advance time after a Set to verify the entry survives.</summary>
    private sealed class FrozenTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public FrozenTime(DateTimeOffset now) { Now = now; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TrackPoint NewPoint(DateTime ts, double lat = 47.4, double lon = 8.5) =>
        new(Timestamp: DateTime.SpecifyKind(ts, DateTimeKind.Utc),
            Latitude: lat, Longitude: lon,
            SpeedOverGround: null, CourseOverGround: null, Heading: null,
            WindAngleApparent: null, WindSpeedApparent: null,
            WindAngleTrue: null, WindSpeedTrue: null);

    private static HistoryCache.Key KeyAt(DateTime fromUtc, DateTime toUtc) =>
        HistoryCache.Key.From(
            paths: "navigation.position",
            resolution: "30s",
            fromUtc: fromUtc,
            toUtc: toUtc,
            bbox: null);

    [Test]
    public async Task Set_Then_TryGet_ReturnsSameArray()
    {
        // Sanity: round-trip a chunk through the cache. The same
        // reference should come back; the cache doesn't clone for
        // perf reasons (chunk arrays are treated as immutable).
        var time = new FrozenTime(DateTimeOffset.Parse("2026-04-25T18:00:00Z"));
        var cache = new HistoryCache(time);
        var arr = new[] { NewPoint(new(2026, 4, 25, 12, 0, 0)) };
        var key = KeyAt(new(2026, 4, 25, 12, 0, 0), new(2026, 4, 25, 16, 0, 0));

        cache.Set(key, arr);
        var got = cache.TryGet(key);

        await Assert.That(got).IsSameReferenceAs(arr);
        await Assert.That(cache.Hits).IsEqualTo(1);
        await Assert.That(cache.Misses).IsEqualTo(0);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryGet_OnEmptyCache_ReturnsNullAndCountsMiss()
    {
        var time = new FrozenTime(DateTimeOffset.Parse("2026-04-25T18:00:00Z"));
        var cache = new HistoryCache(time);
        var key = KeyAt(new(2026, 4, 25, 12, 0, 0), new(2026, 4, 25, 16, 0, 0));

        var got = cache.TryGet(key);

        await Assert.That(got).IsNull();
        await Assert.That(cache.Misses).IsEqualTo(1);
        await Assert.That(cache.Hits).IsEqualTo(0);
    }

    [Test]
    public async Task DifferentKeyFields_ProduceDistinctEntries()
    {
        // Each key field affects the API response shape, so each
        // must distinguish entries. Pinned with one entry per
        // varying field to catch a future record-equality regression.
        var time = new FrozenTime(DateTimeOffset.Parse("2026-04-25T18:00:00Z"));
        var cache = new HistoryCache(time);
        var arr = new[] { NewPoint(new(2026, 4, 25, 12, 0, 0)) };

        var basePaths = "navigation.position";
        var baseRes = "30s";
        var baseFrom = new DateTime(2026, 4, 25, 12, 0, 0, DateTimeKind.Utc);
        var baseTo = new DateTime(2026, 4, 25, 16, 0, 0, DateTimeKind.Utc);

        cache.Set(HistoryCache.Key.From(basePaths, baseRes, baseFrom, baseTo, null), arr);
        cache.Set(HistoryCache.Key.From("navigation.position,navigation.speedOverGround", baseRes, baseFrom, baseTo, null), arr);
        cache.Set(HistoryCache.Key.From(basePaths, "1m", baseFrom, baseTo, null), arr);
        cache.Set(HistoryCache.Key.From(basePaths, baseRes, baseFrom.AddMinutes(1), baseTo, null), arr);
        cache.Set(HistoryCache.Key.From(basePaths, baseRes, baseFrom, baseTo.AddMinutes(1), null), arr);
        cache.Set(HistoryCache.Key.From(basePaths, baseRes, baseFrom, baseTo,
            new TrackBbox(South: 47.0, West: 8.0, North: 48.0, East: 9.0)), arr);

        await Assert.That(cache.Count).IsEqualTo(6);
    }

    [Test]
    public async Task Bbox_KeyedByInvariantCultureNumeric_NotByLocaleString()
    {
        // Two identical bboxes built from doubles must collide on
        // the cache key. If a future bbox-key change accidentally
        // ToString()s with the ambient locale (de-CH would render
        // 47,3 instead of 47.3), the key would no longer collide
        // and the cache would silently double-store every entry.
        var time = new FrozenTime(DateTimeOffset.Parse("2026-04-25T18:00:00Z"));
        var cache = new HistoryCache(time);
        var arr = new[] { NewPoint(new(2026, 4, 25, 12, 0, 0)) };
        var bbox1 = new TrackBbox(South: 47.30, West: 8.40, North: 47.50, East: 8.60);
        var bbox2 = new TrackBbox(South: 47.30, West: 8.40, North: 47.50, East: 8.60);
        var k1 = HistoryCache.Key.From("path", "30s",
            new(2026, 4, 25, 12, 0, 0, DateTimeKind.Utc),
            new(2026, 4, 25, 16, 0, 0, DateTimeKind.Utc), bbox1);
        var k2 = HistoryCache.Key.From("path", "30s",
            new(2026, 4, 25, 12, 0, 0, DateTimeKind.Utc),
            new(2026, 4, 25, 16, 0, 0, DateTimeKind.Utc), bbox2);

        cache.Set(k1, arr);
        var got = cache.TryGet(k2);

        await Assert.That(got).IsSameReferenceAs(arr);
    }

    [Test]
    public async Task LiveTail_Set_IsNoOp()
    {
        // A chunk whose `to` is within HeadFreshness of "now" is
        // still being filled. Storing it would cache "you haven't
        // moved" data that's wrong by tomorrow's reload. Pin: Set
        // returns silently AND the entry isn't stored.
        var now = DateTimeOffset.Parse("2026-04-25T18:00:00Z");
        var time = new FrozenTime(now);
        var cache = new HistoryCache(time);
        var arr = new[] { NewPoint(new(2026, 4, 25, 17, 0, 0)) };
        // chunkTo is 30 s before "now" -- well inside HeadFreshness
        // (60 s); the cache must reject this write.
        var key = KeyAt(
            new(2026, 4, 25, 17, 0, 0),
            now.UtcDateTime.AddSeconds(-30));

        cache.Set(key, arr);

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LiveTail_TryGet_ReturnsNull_EvenIfPreviouslyStored()
    {
        // A chunk that was cached when it was old can age into the
        // live-tail window if the helm re-loads MUCH later. Pin
        // that the freshness rule applies on read too: even if the
        // entry is in the dictionary, a key whose `to` overlaps
        // the live-tail must miss.
        //
        // Build the entry with "now" far in the future so Set
        // accepts it, then advance the clock so the same entry is
        // now in the live tail.
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var cache = new HistoryCache(time);
        var arr = new[] { NewPoint(new(2026, 4, 25, 16, 0, 0)) };
        var key = KeyAt(
            new(2026, 4, 25, 12, 0, 0),
            new(2026, 4, 25, 16, 0, 0));
        cache.Set(key, arr);
        await Assert.That(cache.Count).IsEqualTo(1);

        // Advance to a "now" that's 30 s after the chunk's `to`:
        // inside HeadFreshness (60 s).
        time.Now = new DateTimeOffset(new DateTime(2026, 4, 25, 16, 0, 30, DateTimeKind.Utc));

        var got = cache.TryGet(key);

        await Assert.That(got).IsNull();
        await Assert.That(cache.Misses).IsEqualTo(1);
    }

    [Test]
    public async Task LRU_EvictsOldestWhenOverCap()
    {
        // Bound the eviction rule with a small cap so the test
        // doesn't have to allocate MaxEntries+1 entries. The
        // contract: oldest-by-access is evicted; recently-touched
        // entries survive.
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var cache = new HistoryCache(time);

        // Insert MaxEntries+1 distinct keys. Each entry is tiny;
        // the test only cares about the eviction order.
        for (int i = 0; i <= HistoryCache.MaxEntries; i++)
        {
            var key = KeyAt(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i + 1));
            cache.Set(key, new[] { NewPoint(new DateTime(2026, 1, 1).AddHours(i)) });
        }

        // Cap holds; oldest (index 0) is gone, newest (last) is in.
        await Assert.That(cache.Count).IsEqualTo(HistoryCache.MaxEntries);
        var firstKey = KeyAt(
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
        await Assert.That(cache.TryGet(firstKey)).IsNull();
        var lastKey = KeyAt(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(HistoryCache.MaxEntries),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(HistoryCache.MaxEntries + 1));
        await Assert.That(cache.TryGet(lastKey)).IsNotNull();
    }

    [Test]
    public async Task LRU_PromotesOnAccess_SoTouchedEntriesSurviveEviction()
    {
        // Critical eviction-order test: an entry that's been READ
        // recently must not be evicted before entries that were
        // only WRITTEN earlier. Without promote-on-access, a
        // re-loaded long window (which reads the same head chunks
        // repeatedly) would lose its tail to eviction first.
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var cache = new HistoryCache(time);

        // Insert MaxEntries entries.
        for (int i = 0; i < HistoryCache.MaxEntries; i++)
        {
            var key = KeyAt(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i + 1));
            cache.Set(key, new[] { NewPoint(new DateTime(2026, 1, 1).AddHours(i)) });
        }
        // Touch the oldest entry (index 0) -- should promote to MRU.
        var oldestKey = KeyAt(
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
        cache.TryGet(oldestKey);
        // Insert ONE more, which should evict the SECOND-oldest
        // (index 1), not the touched oldest.
        var newKey = KeyAt(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(HistoryCache.MaxEntries),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(HistoryCache.MaxEntries + 1));
        cache.Set(newKey, new[] { NewPoint(new DateTime(2026, 1, 1).AddHours(HistoryCache.MaxEntries)) });

        // Touched-oldest survives; second-oldest is gone.
        await Assert.That(cache.TryGet(oldestKey)).IsNotNull();
        var secondOldestKey = KeyAt(
            new(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            new(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        await Assert.That(cache.TryGet(secondOldestKey)).IsNull();
    }

    [Test]
    public async Task Clear_ResetsEverything()
    {
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var cache = new HistoryCache(time);
        var arr = new[] { NewPoint(new(2026, 4, 25, 12, 0, 0)) };
        var key = KeyAt(new(2026, 4, 25, 12, 0, 0), new(2026, 4, 25, 16, 0, 0));
        cache.Set(key, arr);
        cache.TryGet(key);             // 1 hit
        cache.TryGet(KeyAt(            // 1 miss
            new(2025, 1, 1, 0, 0, 0),
            new(2025, 1, 1, 1, 0, 0)));

        cache.Clear();

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.Hits).IsEqualTo(0);
        await Assert.That(cache.Misses).IsEqualTo(0);
        await Assert.That(cache.TryGet(key)).IsNull();
    }
}
