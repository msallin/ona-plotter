using System.Text.Json;
using OnaPlotter.Services;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the AtoN payload-shape contract Map.razor used to own.
/// AtonStore + AtonTypeCatalog have their own coverage; these tests
/// verify the wiring between them and the JS-side push.
/// </summary>
public class AtonPushServiceTests
{
    private sealed class FakeAisJs : IMapAisJs
    {
        public List<object[]> AtonPushes { get; } = [];

        public Task UpdateAisTargetsAsync(object[] vessels) => Task.CompletedTask;

        public Task SetAtonsAsync(object[] atons)
        {
            AtonPushes.Add(atons);
            return Task.CompletedTask;
        }

        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
        public Task SetGuardZoneVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetGuardZoneWarningRingVisibleAsync(bool visible) => Task.CompletedTask;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Empty_Store_Pushes_Empty_Array()
    {
        var store = new AtonStore();
        var js = new FakeAisJs();
        var svc = new AtonPushService(js, store);

        await svc.PushAsync();

        await Assert.That(js.AtonPushes.Count).IsEqualTo(1);
        await Assert.That(js.AtonPushes[0].Length).IsEqualTo(0);
    }

    [Test]
    public async Task Atons_Without_Position_Are_Filtered_Out()
    {
        // The AtonStore keeps every context but the push-side filter
        // matches the original Map.razor contract: only entries with
        // a lat/lon make it to the marker layer.
        var store = new AtonStore();
        store.Apply("atons.urn:mrn:imo:mmsi:1", "name", Parse("\"NO_POS\""));
        store.Apply("atons.urn:mrn:imo:mmsi:2", "navigation.position",
            Parse("{\"latitude\":47.0,\"longitude\":8.0}"));
        var js = new FakeAisJs();
        var svc = new AtonPushService(js, store);

        await svc.PushAsync();

        await Assert.That(js.AtonPushes[0].Length).IsEqualTo(1);
    }

    [Test]
    public async Task Payload_Includes_Symbol_And_Side_Resolution()
    {
        // C# resolves the catalog lookup so JS stays a dumb renderer.
        // Spot-check the shape -- a renamed field would silently break
        // the JS marker layer.
        var store = new AtonStore();
        store.Apply("atons.urn:mrn:imo:mmsi:111", "navigation.position",
            Parse("{\"latitude\":47.0,\"longitude\":8.0}"));
        store.Apply("atons.urn:mrn:imo:mmsi:111", "atonType",
            Parse("{\"id\":1,\"name\":\"Reference\"}"));
        var js = new FakeAisJs();
        var svc = new AtonPushService(js, store);

        await svc.PushAsync();

        var entry = js.AtonPushes[0][0];
        var t = entry.GetType();
        await Assert.That(t.GetProperty("context")).IsNotNull();
        await Assert.That(t.GetProperty("lat")).IsNotNull();
        await Assert.That(t.GetProperty("lon")).IsNotNull();
        await Assert.That(t.GetProperty("typeId")).IsNotNull();
        await Assert.That(t.GetProperty("typeName")).IsNotNull();
        await Assert.That(t.GetProperty("symbol")).IsNotNull();
        await Assert.That(t.GetProperty("side")).IsNotNull();
        // 'virtual' is a C# keyword on the anonymous type; verb prefix kept.
        await Assert.That(t.GetProperty("virtual")).IsNotNull();
    }
}
