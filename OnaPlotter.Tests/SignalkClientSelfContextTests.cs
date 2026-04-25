using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the self-context normalisation + AIS-eviction contract that
/// protects against own-boat showing up as an AIS target. The helpers
/// are internal; the test project has InternalsVisibleTo so we can
/// exercise them directly without spinning up a websocket.
/// </summary>
public class SignalkClientSelfContextTests
{
    private static SignalkClient BuildClient(AisStore store) =>
        new(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: store,
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: new OnaPlotter.Services.AtonStore());

    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public string RadarBaseUrl => BaseUrl;
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public string Combine(string path) => BaseUrl + path;
        public string CombineRadar(string path) => BaseUrl + path;
    }

    [Test]
    public async Task IsSelfContext_Empty_Or_SelfLiteral_Always_True()
    {
        var client = BuildClient(new AisStore());
        await Assert.That(client.IsSelfContext(null)).IsTrue();
        await Assert.That(client.IsSelfContext("")).IsTrue();
        await Assert.That(client.IsSelfContext("vessels.self")).IsTrue();
    }

    [Test]
    public async Task IsSelfContext_Unknown_Context_Before_Self_Resolved_Is_False()
    {
        // Before SetSelfContext is called, any non-self-literal context
        // looks like a third-party AIS vessel and routes there.
        var client = BuildClient(new AisStore());
        await Assert.That(client.IsSelfContext("vessels.urn:mrn:imo:mmsi:244123456")).IsFalse();
    }

    [Test]
    public async Task SetSelfContext_Normalises_Both_Shapes()
    {
        // Some servers publish the self URN with the "vessels." prefix,
        // others without. Both forms must end up matching delta contexts
        // that ALWAYS have the prefix.
        var prefixed = BuildClient(new AisStore());
        prefixed.SetSelfContext("vessels.urn:mrn:imo:mmsi:244000001");
        await Assert.That(prefixed.IsSelfContext("vessels.urn:mrn:imo:mmsi:244000001")).IsTrue();

        var bare = BuildClient(new AisStore());
        bare.SetSelfContext("urn:mrn:imo:mmsi:244000002");
        await Assert.That(bare.IsSelfContext("vessels.urn:mrn:imo:mmsi:244000002")).IsTrue();
    }

    [Test]
    public async Task SetSelfContext_Retro_Evicts_Own_Boat_From_AisStore()
    {
        // The "self arrived late" scenario: an AIS-looking delta for
        // own-boat landed in AisStore before we knew who self was.
        // SetSelfContext must sweep it out so the Map doesn't render a
        // ghost own-boat in the vessel list.
        var store = new AisStore();
        var pos = System.Text.Json.JsonSerializer.SerializeToElement(
            new { latitude = 47.4, longitude = 8.5 });
        store.Apply("vessels.urn:mrn:imo:mmsi:244000003", "navigation.position", pos);
        await Assert.That(store.Count).IsEqualTo(1);

        var client = BuildClient(store);
        client.SetSelfContext("urn:mrn:imo:mmsi:244000003");

        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetSelfContext_Empty_String_Leaves_State_Unset()
    {
        // Server-sent empty string must not crash; subsequent
        // IsSelfContext queries for a real URN should return false
        // (no over-matching).
        var client = BuildClient(new AisStore());
        client.SetSelfContext("");
        await Assert.That(client.IsSelfContext("vessels.urn:mrn:imo:mmsi:foo")).IsFalse();
    }
}
