using System.Text.Json;
using OnaPlotter.Services;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the AtoN payload and the push coalescing. AtonStore and
/// AtonTypeCatalog have their own coverage; these tests verify the
/// wiring between them and the JS-side push.
/// </summary>
public class AtonLayerControllerTests
{
    private sealed class FakeAisJs : IMapAisJs
    {
        public List<AtonPayload[]> AtonPushes { get; } = [];
        public bool ThrowNext;

        public Task<bool> UpdateAisTargetsAsync(AisUpdatePayload update) => Task.FromResult(true);

        public Task SetAtonsAsync(AtonPayload[] atons)
        {
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new Microsoft.JSInterop.JSException("simulated JS failure");
            }
            AtonPushes.Add(atons);
            return Task.CompletedTask;
        }

        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static void SeedAton(AtonStore store, string ctx, double lat, double lon)
        => store.Apply(ctx, "navigation.position",
            Parse($"{{\"latitude\":{lat},\"longitude\":{lon}}}"));

    [Test]
    public async Task Empty_Store_Pushes_Empty_Array()
    {
        var store = new AtonStore();
        var js = new FakeAisJs();
        var svc = new AtonLayerController(js, store);

        await svc.PushAsync();

        await Assert.That(js.AtonPushes.Count).IsEqualTo(1);
        await Assert.That(js.AtonPushes[0].Length).IsEqualTo(0);
    }

    [Test]
    public async Task Atons_Without_Position_Are_Filtered_Out()
    {
        var store = new AtonStore();
        store.Apply("atons.urn:mrn:imo:mmsi:1", "name", Parse("\"NO_POS\""));
        SeedAton(store, "atons.urn:mrn:imo:mmsi:2", 47.0, 8.0);
        var js = new FakeAisJs();
        var svc = new AtonLayerController(js, store);

        await svc.PushAsync();

        await Assert.That(js.AtonPushes[0].Length).IsEqualTo(1);
    }

    [Test]
    public async Task Payload_Resolves_Symbol_Side_And_Popup_Text()
    {
        var store = new AtonStore();
        SeedAton(store, "atons.urn:mrn:imo:mmsi:111", 47.0, 8.0);
        store.Apply("atons.urn:mrn:imo:mmsi:111", "atonType",
            Parse("{\"id\":1,\"name\":\"Reference\"}"));
        var js = new FakeAisJs();
        var svc = new AtonLayerController(js, store);

        await svc.PushAsync();

        var entry = js.AtonPushes[0][0];
        var info = AtonTypeCatalog.Lookup(1);
        await Assert.That(entry.Context).IsEqualTo("atons.urn:mrn:imo:mmsi:111");
        await Assert.That(entry.Lat).IsEqualTo(47.0);
        await Assert.That(entry.Symbol).IsEqualTo(info.Symbol.ToString());
        await Assert.That(entry.Side).IsEqualTo(info.Side.ToString());
        await Assert.That(entry.Subtitle).IsEqualTo("Reference");
    }

    [Test]
    [Arguments("Buoy 4", "992111111", false, "Buoy 4")]
    [Arguments(null, "992111111", false, "992111111")]
    [Arguments("", null, false, "AtoN")]
    [Arguments("Wreck", null, true, "Wreck (virtual)")]
    public async Task Title_NameThenMmsiThenAtoN(string? name, string? mmsi, bool isVirtual, string expected)
    {
        await Assert.That(AtonLayerController.Title(name, mmsi, isVirtual)).IsEqualTo(expected);
    }

    [Test]
    public async Task Detail_ShowsMmsiOnlyWhenTitleIsTheName()
    {
        var store = new AtonStore();
        SeedAton(store, "atons.urn:mrn:imo:mmsi:1", 47.0, 8.0);
        store.Apply("atons.urn:mrn:imo:mmsi:1", "name", Parse("\"Buoy\""));
        store.Apply("atons.urn:mrn:imo:mmsi:1", "mmsi", Parse("\"992000001\""));
        SeedAton(store, "atons.urn:mrn:imo:mmsi:2", 47.1, 8.1);

        var snap = AtonLayerController.BuildSnapshot(store.GetAtons());
        var named = snap.First(a => a.Context.EndsWith(":1"));
        var bare = snap.First(a => a.Context.EndsWith(":2"));

        await Assert.That(named.Detail).IsEqualTo("MMSI 992000001");
        await Assert.That(bare.Detail).IsNull();
        await Assert.That(bare.Subtitle).IsNull();
    }

    [Test]
    public async Task ReconnectBurst_IsCoalescedIntoOnePush()
    {
        // A reconnect replays every AtoN as separate deltas. Each marks
        // the service dirty; the AIS tick then pushes once.
        var store = new AtonStore();
        var js = new FakeAisJs();
        var svc = new AtonLayerController(js, store);
        store.OnAtonsUpdated += svc.MarkDirty;

        for (int i = 0; i < 50; i++) SeedAton(store, $"atons.urn:mrn:imo:mmsi:{i}", 47 + i * 0.001, 8.0);
        await svc.PushIfDirtyAsync();
        await svc.PushIfDirtyAsync();

        await Assert.That(js.AtonPushes.Count).IsEqualTo(1);
        await Assert.That(js.AtonPushes[0].Length).IsEqualTo(50);
    }

    [Test]
    public async Task PushIfDirty_NotDirty_DoesNothing()
    {
        var store = new AtonStore();
        var js = new FakeAisJs();
        var svc = new AtonLayerController(js, store);

        await svc.PushIfDirtyAsync();

        await Assert.That(js.AtonPushes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Push_UnchangedStore_IsSkipped()
    {
        var store = new AtonStore();
        SeedAton(store, "atons.urn:mrn:imo:mmsi:1", 47.0, 8.0);
        var js = new FakeAisJs();
        var svc = new AtonLayerController(js, store);

        await svc.PushAsync();
        svc.MarkDirty();
        await svc.PushIfDirtyAsync();

        await Assert.That(js.AtonPushes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FailedPush_StaysDirty_AndRetries()
    {
        var store = new AtonStore();
        SeedAton(store, "atons.urn:mrn:imo:mmsi:1", 47.0, 8.0);
        var js = new FakeAisJs { ThrowNext = true };
        var svc = new AtonLayerController(js, store);

        await svc.PushAsync();
        await svc.PushIfDirtyAsync();

        await Assert.That(js.AtonPushes.Count).IsEqualTo(1);
    }
}
