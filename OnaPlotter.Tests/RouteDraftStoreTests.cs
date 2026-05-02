using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="RouteDraftStore"/>'s round-trip + null-degrade
/// contract. Backed by an in-memory <see cref="IKeyValueStore"/>
/// fake; the production wiring uses the localStorage-backed
/// implementation but the JS round-trip isn't part of this layer's
/// contract -- it's a transparent passthrough for any string.
/// </summary>
public class RouteDraftStoreTests
{
    private sealed class InMemoryKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _d[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { _d.Remove(key); return Task.CompletedTask; }
    }

    private static RouteDraft NewDraft(int wpCount = 3) => new(
        RouteId: "route-abc",
        Name: "Test passage",
        Coords: Enumerable.Range(0, wpCount)
            .Select(i => new[] { 47.4 + i * 0.01, 8.5 + i * 0.01 })
            .ToArray(),
        SavedAtIso: "2026-04-25T12:00:00Z");

    [Test]
    public async Task LoadAsync_NoDraft_ReturnsNull()
    {
        // Pristine localStorage: nothing to restore. The startup
        // prompt skips silently in this case.
        var store = new RouteDraftStore(new InMemoryKv());
        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task SaveThenLoad_Roundtrips_AllFields()
    {
        // Round-trip every field on the record. JsonPropertyName
        // attributes pin the wire format; this catches a future
        // rename that would silently break drafts saved by older
        // builds.
        var store = new RouteDraftStore(new InMemoryKv());
        var draft = NewDraft();
        await store.SaveAsync(draft);

        var loaded = await store.LoadAsync();

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.RouteId).IsEqualTo("route-abc");
        await Assert.That(loaded.Name).IsEqualTo("Test passage");
        await Assert.That(loaded.Coords.Length).IsEqualTo(3);
        await Assert.That(loaded.Coords[0][0]).IsEqualTo(47.4);
        await Assert.That(loaded.Coords[0][1]).IsEqualTo(8.5);
        await Assert.That(loaded.SavedAtIso).IsEqualTo("2026-04-25T12:00:00Z");
    }

    [Test]
    public async Task ClearAsync_AfterSave_RemovesEntry()
    {
        var store = new RouteDraftStore(new InMemoryKv());
        await store.SaveAsync(NewDraft());
        await Assert.That(await store.LoadAsync()).IsNotNull();

        await store.ClearAsync();

        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task SaveAsync_Twice_LatestWins()
    {
        // Single-slot store: a second Save replaces the first. The
        // route-edit poll fires every sub-second so this is the
        // common path -- pin it so a future "queue drafts" change
        // gets a clear test break.
        var store = new RouteDraftStore(new InMemoryKv());
        await store.SaveAsync(NewDraft());
        var second = NewDraft() with { Name = "Updated" };
        await store.SaveAsync(second);

        var loaded = await store.LoadAsync();

        await Assert.That(loaded!.Name).IsEqualTo("Updated");
    }

    [Test]
    public async Task LoadAsync_MalformedJson_ReturnsNull()
    {
        // localStorage tampering, browser-extension corruption, or
        // a future schema bump that broke backward compat. Don't
        // crash; treat as "no draft" so the next save overwrites.
        var kv = new InMemoryKv();
        await kv.SetAsync(RouteDraftStore.StorageKey, "{ this is not valid json");
        var store = new RouteDraftStore(kv);

        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task LoadAsync_EmptyCoords_ReturnsNull()
    {
        // Defensive: a draft with no coords would land the helm in
        // an empty edit panel and read as a confusing "what was I
        // editing?" prompt. Treat as "nothing to restore".
        var store = new RouteDraftStore(new InMemoryKv());
        await store.SaveAsync(NewDraft() with { Coords = Array.Empty<double[]>() });

        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task StorageKey_Versioned()
    {
        // Pinned: the key ends in v1 so a future non-backward-
        // compatible model change can bump to v2 without resurrecting
        // old drafts in a new schema. Test this here so a developer
        // who renames the constant sees the contract spelled out.
        await Assert.That(RouteDraftStore.StorageKey).IsEqualTo("route.draft.v1");
    }

    [Test]
    public async Task LoadAsync_CoordOutOfRange_Lat_ReturnsNull()
    {
        // Browser extension or schema-skew tampering can land a
        // lat outside [-90, 90]. Restoring it would render NaN-
        // arithmetic in the polyline path and the route would
        // silently disappear; the helm sees a "restore" prompt
        // followed by an empty map. Better to drop the draft
        // entirely.
        var store = new RouteDraftStore(new InMemoryKv());
        var bad = NewDraft() with
        {
            Coords = [[91.0, 8.5], [47.4, 8.5]],     // first row bad
        };
        await store.SaveAsync(bad);
        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task LoadAsync_CoordOutOfRange_Lon_ReturnsNull()
    {
        // Lon outside [-180, 180]. Same null-degrade behaviour.
        var store = new RouteDraftStore(new InMemoryKv());
        var bad = NewDraft() with
        {
            Coords = [[47.4, 8.5], [47.4, 200.0]],     // second row bad
        };
        await store.SaveAsync(bad);
        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task LoadAsync_CoordOverflowToInfinity_ReturnsNull()
    {
        // A JSON number literal large enough to overflow double
        // (1e400) deserialises to PositiveInfinity in
        // System.Text.Json -- IsFinite catches it and we drop the
        // draft. Pinned because the alternative would be a polyline
        // anchored at (Inf, 8.5), which the renderer either NaN-
        // arithmetics into invisibility or, worse, draws a degenerate
        // line that loses the rest of the draft visually.
        var kv = new InMemoryKv();
        await kv.SetAsync(RouteDraftStore.StorageKey,
            """{"RouteId":"r","Name":"n","Coords":[[1e400,8.5],[47.4,8.5]],"SavedAtIso":"2026-04-25T12:00:00Z"}""");
        var store = new RouteDraftStore(kv);
        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task LoadAsync_CoordWrongShape_ReturnsNull()
    {
        // A coord row with only one value (or three) is malformed.
        // Treat as no draft.
        var kv = new InMemoryKv();
        // Hand-craft json so we can put a length-1 row in (the C#
        // type would reject this at the model level if double[][]
        // were length-checked, but it isn't -- the validation
        // belongs in LoadAsync).
        await kv.SetAsync(RouteDraftStore.StorageKey,
            """{"RouteId":"r","Name":"n","Coords":[[47.4]],"SavedAtIso":"2026-04-25T12:00:00Z"}""");
        var store = new RouteDraftStore(kv);
        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task LoadAsync_BoundaryCoord_AccepedAtPolesAndDateline()
    {
        // The exact ±90 / ±180 boundaries are valid (north pole,
        // south pole, antimeridian). Pin so a future "off by one"
        // tightening of the validation doesn't reject an otherwise-
        // legitimate sub-Antarctic / antimeridian-crossing draft.
        var store = new RouteDraftStore(new InMemoryKv());
        var ok = NewDraft() with
        {
            Coords = [[-90.0, -180.0], [90.0, 180.0]],
        };
        await store.SaveAsync(ok);
        var loaded = await store.LoadAsync();
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Coords.Length).IsEqualTo(2);
    }

    [Test]
    public async Task LoadAsync_NewRouteDraft_WithNullRouteId_Roundtrips()
    {
        // Fresh-route drafts (never-saved-before) carry null
        // RouteId. JsonPropertyName + nullable ref must round-trip
        // null cleanly so the restore path can decide between
        // "edit existing" and "create new".
        var store = new RouteDraftStore(new InMemoryKv());
        await store.SaveAsync(NewDraft() with { RouteId = null });

        var loaded = await store.LoadAsync();

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.RouteId).IsNull();
    }
}
