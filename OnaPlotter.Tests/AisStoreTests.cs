using System.Text.Json;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class AisStoreTests
{
    [Test]
    public async Task Empty_CountIsZero()
    {
        var store = new AisStore();
        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_GetVessels_ReturnsEmpty()
    {
        var store = new AisStore();
        await Assert.That(store.GetVessels()).IsEmpty();
    }

    [Test]
    public async Task Apply_NewContext_CreatesVessel()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_SameContext_UpdatesExistingVessel()
    {
        var store = new AisStore();
        var ctx = "vessels.urn:mrn:imo:mmsi:222222222";
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply(ctx, "navigation.position", pos);

        var name = JsonSerializer.SerializeToElement("TestVessel");
        store.Apply(ctx, "name", name);

        await Assert.That(store.Count).IsEqualTo(1);
        var vessels = store.GetVessels();
        await Assert.That(vessels.Length).IsEqualTo(1);
        await Assert.That(vessels[0].Name).IsEqualTo("TestVessel");
    }

    [Test]
    public async Task GetVessels_OnlyReturnsVesselsWithPosition()
    {
        var store = new AisStore();
        var ctx1 = "vessels.urn:mrn:imo:mmsi:111111111";
        var ctx2 = "vessels.urn:mrn:imo:mmsi:222222222";

        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply(ctx1, "navigation.position", pos);

        var name = JsonSerializer.SerializeToElement("NoPosition");
        store.Apply(ctx2, "name", name);

        var vessels = store.GetVessels();
        await Assert.That(vessels.Length).IsEqualTo(1);
        await Assert.That(vessels[0].Context).IsEqualTo(ctx1);
    }

    [Test]
    public async Task Apply_ExtractsMmsiFromContext()
    {
        var store = new AisStore();
        var ctx = "vessels.urn:mrn:imo:mmsi:333333333";
        var pos = JsonSerializer.SerializeToElement(new { latitude = 1.0, longitude = 2.0 });
        store.Apply(ctx, "navigation.position", pos);

        var vessels = store.GetVessels();
        await Assert.That(vessels[0].Mmsi).IsEqualTo("333333333");
    }

    [Test]
    public async Task GetVessels_ReturnsCachedSnapshot()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);

        var snap1 = store.GetVessels();
        var snap2 = store.GetVessels();

        await Assert.That(snap2).IsSameAs(snap1);
    }

    [Test]
    public async Task GetVessels_RefreshesCacheAfterUpdate()
    {
        var store = new AisStore();
        var pos1 = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos1);
        var snap1 = store.GetVessels();

        var sog = JsonSerializer.SerializeToElement(3.5);
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.speedOverGround", sog);
        var snap2 = store.GetVessels();

        await Assert.That(snap2).IsNotSameAs(snap1);
    }

    [Test]
    public async Task Apply_RaisesOnAisUpdated()
    {
        var store = new AisStore();
        int raised = 0;
        store.OnAisUpdated += () => raised++;

        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);

        await Assert.That(raised).IsEqualTo(1);
    }
}
