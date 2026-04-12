using System.Text.Json;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class AisStoreTests
{
    private readonly AisStore _store = new();

    [Fact]
    public void Empty_CountIsZero()
    {
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void Empty_GetVessels_ReturnsEmpty()
    {
        Assert.Empty(_store.GetVessels());
    }

    [Fact]
    public void Apply_NewContext_CreatesVessel()
    {
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        _store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public void Apply_SameContext_UpdatesExistingVessel()
    {
        var ctx = "vessels.urn:mrn:imo:mmsi:222222222";
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        _store.Apply(ctx, "navigation.position", pos);

        var name = JsonSerializer.SerializeToElement("TestVessel");
        _store.Apply(ctx, "name", name);

        Assert.Equal(1, _store.Count);
        var vessels = _store.GetVessels();
        Assert.Single(vessels);
        Assert.Equal("TestVessel", vessels[0].Name);
    }

    [Fact]
    public void GetVessels_OnlyReturnsVesselsWithPosition()
    {
        var ctx1 = "vessels.urn:mrn:imo:mmsi:111111111";
        var ctx2 = "vessels.urn:mrn:imo:mmsi:222222222";

        // ctx1: has position
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        _store.Apply(ctx1, "navigation.position", pos);

        // ctx2: no position, only name
        var name = JsonSerializer.SerializeToElement("NoPosition");
        _store.Apply(ctx2, "name", name);

        var vessels = _store.GetVessels();
        Assert.Single(vessels);
        Assert.Equal(ctx1, vessels[0].Context);
    }

    [Fact]
    public void Apply_ExtractsMmsiFromContext()
    {
        var ctx = "vessels.urn:mrn:imo:mmsi:333333333";
        var pos = JsonSerializer.SerializeToElement(new { latitude = 1.0, longitude = 2.0 });
        _store.Apply(ctx, "navigation.position", pos);

        var vessels = _store.GetVessels();
        Assert.Equal("333333333", vessels[0].Mmsi);
    }

    [Fact]
    public void GetVessels_ReturnsCachedSnapshot()
    {
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        _store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);

        var snap1 = _store.GetVessels();
        var snap2 = _store.GetVessels();

        // Should be same reference when no changes occurred.
        Assert.Same(snap1, snap2);
    }

    [Fact]
    public void GetVessels_RefreshesCacheAfterUpdate()
    {
        var pos1 = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        _store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos1);
        var snap1 = _store.GetVessels();

        var sog = JsonSerializer.SerializeToElement(3.5);
        _store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.speedOverGround", sog);
        var snap2 = _store.GetVessels();

        Assert.NotSame(snap1, snap2);
    }

    [Fact]
    public void Apply_RaisesOnAisUpdated()
    {
        int raised = 0;
        _store.OnAisUpdated += () => raised++;

        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        _store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);

        Assert.Equal(1, raised);
    }
}
