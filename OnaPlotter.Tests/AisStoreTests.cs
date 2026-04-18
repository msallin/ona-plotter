using System.Text.Json;
using OnaPlotter.Models;
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
    public async Task SetName_WhenEmpty_PopulatesAndFiresEvent()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:222222222", "navigation.position", pos);
        int fired = 0;
        store.OnAisUpdated += () => fired++;

        store.SetName("vessels.urn:mrn:imo:mmsi:222222222", "MARCO POLO");

        await Assert.That(store.GetVessels()[0].Name).IsEqualTo("MARCO POLO");
        await Assert.That(fired).IsEqualTo(1);
    }

    [Test]
    public async Task SetName_WhenAlreadySet_DoesNotOverwrite()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:333333333", "navigation.position", pos);
        var nameEl = JsonSerializer.SerializeToElement("ORIGINAL");
        store.Apply("vessels.urn:mrn:imo:mmsi:333333333", "name", nameEl);

        store.SetName("vessels.urn:mrn:imo:mmsi:333333333", "OVERRIDE");

        await Assert.That(store.GetVessels()[0].Name).IsEqualTo("ORIGINAL");
    }

    [Test]
    public async Task SetName_UnknownContext_NoOp()
    {
        var store = new AisStore();
        store.SetName("vessels.urn:mrn:imo:mmsi:444444444", "NOBODY");
        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateBuddies_TagsExistingVessels()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "navigation.position", pos);
        store.Apply("vessels.urn:mrn:imo:mmsi:222", "navigation.position", pos);

        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:111"]);

        var vessels = store.GetVessels();
        var buddy = vessels.First(v => v.Context.EndsWith("111"));
        var other = vessels.First(v => v.Context.EndsWith("222"));
        await Assert.That(buddy.IsBuddy).IsTrue();
        await Assert.That(other.IsBuddy).IsFalse();
    }

    [Test]
    public async Task UpdateBuddies_SeedsVesselsCreatedLater()
    {
        var store = new AisStore();
        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:333"]);

        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:333", "navigation.position", pos);
        store.Apply("vessels.urn:mrn:imo:mmsi:444", "navigation.position", pos);

        var vessels = store.GetVessels();
        await Assert.That(vessels.First(v => v.Context.EndsWith("333")).IsBuddy).IsTrue();
        await Assert.That(vessels.First(v => v.Context.EndsWith("444")).IsBuddy).IsFalse();
    }

    [Test]
    public async Task UpdateBuddies_RemovesBuddyFlag()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:555", "navigation.position", pos);
        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:555"]);
        await Assert.That(store.GetVessels()[0].IsBuddy).IsTrue();

        // User removed the buddy in the plugin UI; refreshing with an empty
        // list must un-tag the vessel.
        store.UpdateBuddies([]);
        await Assert.That(store.GetVessels()[0].IsBuddy).IsFalse();
    }

    [Test]
    public async Task RadarContext_SeedsRadarSource_AndRdrName()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("radar.1.T42", "position", pos);

        var v = store.GetVessels().Single();
        await Assert.That(v.Source).IsEqualTo(TargetSource.Radar);
        await Assert.That(v.Mmsi).IsNull();
        await Assert.That(v.Name).IsEqualTo("RDR-T42");
        await Assert.That(v.Latitude).IsEqualTo(47.0);
        await Assert.That(v.Longitude).IsEqualTo(8.0);
    }

    [Test]
    public async Task RadarTarget_AcceptsCourseAndSpeedShorthand()
    {
        var store = new AisStore();
        store.Apply("radar.1.T1", "position",
            JsonSerializer.SerializeToElement(new { latitude = 0.0, longitude = 0.0 }));
        store.Apply("radar.1.T1", "course", JsonSerializer.SerializeToElement(1.57));
        store.Apply("radar.1.T1", "speed", JsonSerializer.SerializeToElement(4.1));

        var v = store.GetVessels().Single();
        await Assert.That(v.CourseOverGround).IsEqualTo(1.57);
        await Assert.That(v.SpeedOverGround).IsEqualTo(4.1);
    }

    [Test]
    public async Task AisContext_KeepsAisSource()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 1.0, longitude = 2.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:777777777", "navigation.position", pos);

        var v = store.GetVessels().Single();
        await Assert.That(v.Source).IsEqualTo(TargetSource.Ais);
        await Assert.That(v.Mmsi).IsEqualTo("777777777");
    }

    [Test]
    public async Task AisVessel_BuddyPathApplied()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:666", "navigation.position", pos);

        // Live update via the delta stream: the plugin pushes buddy=true.
        var buddyTrue = JsonSerializer.SerializeToElement(true);
        store.Apply("vessels.urn:mrn:imo:mmsi:666", "buddy", buddyTrue);

        await Assert.That(store.GetVessels()[0].IsBuddy).IsTrue();
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

        await Assert.That(snap2).IsSameReferenceAs(snap1);
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

        await Assert.That(snap2).IsNotSameReferenceAs(snap1);
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
