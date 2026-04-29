using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the contract of <see cref="AisPushService"/> on the surface
/// area Map.razor used to own: payload-shape (the JS layer expects
/// specific field names), harbor-mode filter pass-through, and the
/// "no map yet" guard. The detailed CPA / COLREGS / palette /
/// harbour math has its own dedicated test suites.
/// </summary>
public class AisPushServiceTests
{
    private sealed class FakeAisJs : IMapAisJs
    {
        public List<object[]> Pushes { get; } = [];

        public Task UpdateAisTargetsAsync(object[] vessels)
        {
            Pushes.Add(vessels);
            return Task.CompletedTask;
        }

        public Task SetAtonsAsync(object[] atons) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
        public Task SetGuardZoneVisibleAsync(bool visible) => Task.CompletedTask;
    }

    /// <summary>Mutable time provider for the harbor-mode dwell test.
    /// Avoids dragging in <c>Microsoft.Extensions.Time.Testing</c> for
    /// a single Advance() call.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTime(DateTime utc) { _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)); }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static AisPushService NewService(
        AisStore store,
        FakeAisJs js,
        IAppSettings? settings = null,
        IMooredVesselTracker? tracker = null,
        TimeProvider? time = null) =>
        new(js, store, tracker ?? new MooredVesselTracker(), settings ?? new FakeSettings(),
            time ?? new FakeTime(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)));

    private static void SeedVessel(AisStore store, string context, double lat, double lon,
        double? sog = 5.0, double? cog = 1.5, string? name = null)
    {
        var pos = JsonSerializer.SerializeToElement(new { latitude = lat, longitude = lon });
        store.Apply(context, "navigation.position", pos);
        if (sog is double s) store.Apply(context, "navigation.speedOverGround", s);
        if (cog is double c) store.Apply(context, "navigation.courseOverGroundTrue", c);
        if (name is not null) store.Apply(context, "name", name);
    }

    [Test]
    public async Task Push_With_No_Vessels_Sends_Empty_Array()
    {
        var store = new AisStore();
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Pushes.Count).IsEqualTo(1);
        await Assert.That(js.Pushes[0].Length).IsEqualTo(0);
    }

    [Test]
    public async Task Push_Skips_Vessels_Without_Position()
    {
        // AisStore returns vessels with positions only, so a vessel
        // delta that arrives without lat/lon never makes it through.
        // This mirrors HarborAisFilter's bedrock guarantee.
        var store = new AisStore();
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "name", "Ghost");
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.0, 8.0, name: "Real");
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Pushes[0].Length).IsEqualTo(1);
    }

    [Test]
    public async Task Harbor_Mode_Drops_Moored_Vessels()
    {
        // Settings.HarborMode flag enables the moored-vessel filter via
        // HarborAisFilter; a low-SOG vessel after the dwell window
        // should disappear from the snapshot.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, sog: 0.05);
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.1, 8.1, sog: 5.0);

        var settings = new FakeSettings { HarborMode = true };
        var time = new FakeTime(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var tracker = new MooredVesselTracker();
        var js = new FakeAisJs();
        var svc = NewService(store, js, settings, tracker, time);

        await svc.PushAsync(new NavigationData());
        time.Advance(TimeSpan.FromSeconds(75));      // past dwell
        await svc.PushAsync(new NavigationData());

        // The moored vessel drops on the second push; the active one stays.
        await Assert.That(js.Pushes[1].Length).IsEqualTo(1);
    }

    [Test]
    public async Task Payload_Has_The_Expected_Shape_For_JS_Renderer()
    {
        // The JS side reads specific property names off each entry; if
        // the contract drifts, markers vanish silently. Walk one entry
        // via reflection so a renamed field shouts at compile time when
        // someone adds an enum entry.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "Test Boat");
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        var entry = js.Pushes[0][0];
        var t = entry.GetType();
        // Spot-check a representative subset of the fields the JS side
        // expects. Renaming any of them silently breaks marker rendering.
        await Assert.That(t.GetProperty("context")).IsNotNull();
        await Assert.That(t.GetProperty("lat")).IsNotNull();
        await Assert.That(t.GetProperty("lon")).IsNotNull();
        await Assert.That(t.GetProperty("displayName")).IsNotNull();
        await Assert.That(t.GetProperty("source")).IsNotNull();
        await Assert.That(t.GetProperty("cpaThreat")).IsNotNull();
        await Assert.That(t.GetProperty("ageSec")).IsNotNull();
    }

    [Test]
    public async Task Buddy_Vessels_Get_Star_Prefix()
    {
        // displayName = "★ NAME" when the vessel is on the buddy list.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "Friend");
        store.UpdateBuddies(new[] { "vessels.urn:mrn:imo:mmsi:111" });
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        var entry = js.Pushes[0][0];
        var name = (string?)entry.GetType().GetProperty("displayName")?.GetValue(entry);
        await Assert.That(name).IsEqualTo("★ Friend");
    }
}
