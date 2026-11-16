using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class AtonStoreTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Apply_UnseenContext_AddsToStore()
    {
        var s = new AtonStore();
        s.Apply("atons.urn:mrn:imo:mmsi:992111234", "name", Parse("\"BUOY\""));
        await Assert.That(s.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_FiresOnAtonsUpdated_OnChange()
    {
        var s = new AtonStore();
        int fires = 0;
        s.OnAtonsUpdated += () => fires++;
        s.Apply("atons.urn:mrn:imo:mmsi:992111234", "name", Parse("\"BUOY\""));
        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_NoOp_DoesNotFireEvent()
    {
        // Re-applying the same value: Aton.Apply returns false, store
        // skips the event. UI doesn't re-render on every tick.
        var s = new AtonStore();
        s.Apply("atons.urn:mrn:imo:mmsi:992111234", "name", Parse("\"BUOY\""));
        int fires = 0;
        s.OnAtonsUpdated += () => fires++;
        s.Apply("atons.urn:mrn:imo:mmsi:992111234", "name", Parse("\"BUOY\""));
        await Assert.That(fires).IsEqualTo(0);
    }

    [Test]
    public async Task GetAtons_OnlyReturnsEntriesWithPosition()
    {
        // Store keeps every context that ever had a delta, but the
        // map only renders the ones with a position. GetAtons filters
        // to render-ready entries.
        var s = new AtonStore();
        s.Apply("atons.urn:mrn:imo:mmsi:1", "name", Parse("\"NO_POS\""));
        s.Apply("atons.urn:mrn:imo:mmsi:2", "navigation.position",
            Parse("{\"latitude\":1.0, \"longitude\":2.0}"));

        var result = s.GetAtons();
        await Assert.That(result.Length).IsEqualTo(1);
        await Assert.That(result[0].Mmsi).IsEqualTo("2");
    }

    [Test]
    public async Task GetAtons_CachesUntilVersionBump()
    {
        // Snapshot is rebuilt only on version change. Two consecutive
        // calls without an Apply in between return the same array
        // reference.
        var s = new AtonStore();
        s.Apply("atons.urn:mrn:imo:mmsi:2", "navigation.position",
            Parse("{\"latitude\":1.0, \"longitude\":2.0}"));
        var first = s.GetAtons();
        var second = s.GetAtons();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task GetAtons_RebuildsAfterApply()
    {
        // Conversely, a new Apply that changes the underlying state
        // should bust the cache so GetAtons returns a fresh snapshot.
        var s = new AtonStore();
        s.Apply("atons.urn:mrn:imo:mmsi:2", "navigation.position",
            Parse("{\"latitude\":1.0, \"longitude\":2.0}"));
        var first = s.GetAtons();

        s.Apply("atons.urn:mrn:imo:mmsi:3", "navigation.position",
            Parse("{\"latitude\":3.0, \"longitude\":4.0}"));
        var second = s.GetAtons();

        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        await Assert.That(second.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Reset_ClearsAndFires()
    {
        var s = new AtonStore();
        s.Apply("atons.urn:mrn:imo:mmsi:2", "navigation.position",
            Parse("{\"latitude\":1.0, \"longitude\":2.0}"));
        int fires = 0;
        s.OnAtonsUpdated += () => fires++;

        s.Reset();

        await Assert.That(s.Count).IsEqualTo(0);
        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task Reset_OnEmptyStore_NoEvent()
    {
        // Reset on an already-empty store is a no-op (nothing to clear).
        // Skipping the event spares listeners from spurious "I should
        // re-render now" wake-ups.
        var s = new AtonStore();
        int fires = 0;
        s.OnAtonsUpdated += () => fires++;
        s.Reset();
        await Assert.That(fires).IsEqualTo(0);
    }

    private static (AtonStore store, FakeTimeProvider time) StoreWithBuoy()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var s = new AtonStore(time);
        const string ctx = "atons.urn:mrn:imo:mmsi:992111234";
        s.Apply(ctx, "name", Parse("\"BUOY\""));
        s.Apply(ctx, "navigation.position", Parse("""{"latitude":54.5,"longitude":10.2}"""));
        return (s, time);
    }

    [Test]
    public async Task GetAtons_SilentUnder24h_Kept()
    {
        // A buoy last heard 23 h ago is still on the chart: AIS Type 21
        // gaps of hours happen at the edge of receiver range.
        var (s, time) = StoreWithBuoy();
        time.Advance(TimeSpan.FromHours(23));

        await Assert.That(s.GetAtons().Length).IsEqualTo(1);
    }

    [Test]
    public async Task GetAtons_SilentOver24h_Pruned()
    {
        // Multi-day passage: a buoy seen at departure and silent for a
        // day is astern and out of range; the sweep drops it.
        var (s, time) = StoreWithBuoy();
        time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        await Assert.That(s.GetAtons().Length).IsEqualTo(0);
        await Assert.That(s.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_StampsLastSeenFromTimeProvider()
    {
        var (s, time) = StoreWithBuoy();
        time.Advance(TimeSpan.FromMinutes(5));
        s.Apply("atons.urn:mrn:imo:mmsi:992111234", "name", Parse("\"BUOY 2\""));

        await Assert.That(s.GetAtons()[0].LastSeen)
            .IsEqualTo(new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc));
    }
}
