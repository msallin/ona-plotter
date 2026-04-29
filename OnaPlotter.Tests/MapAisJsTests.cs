using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapAisJs wrapper around the leafletInterop.js AIS / AtoN
/// surface. Same shape as <see cref="MapAnchorJsTests"/>: happy paths
/// via the recording fake, dispose gate via MarkDisposed, swallow /
/// propagate behaviour via the throw-next flags.
/// </summary>
public class MapAisJsTests
{
    [Test]
    public async Task UpdateAisTargetsAsync_ForwardsArrayAsSingleArg()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);
        var vessels = new object[] { new { id = "abc" }, new { id = "def" } };

        await sut.UpdateAisTargetsAsync(vessels);

        await Assert.That(fake.Calls.Count).IsEqualTo(1);
        await Assert.That(fake.Calls[0].id).IsEqualTo("updateAisTargets");
        // Array is passed as a single object argument so the JS side
        // sees one parameter, not N spread args.
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(vessels);
    }

    [Test]
    public async Task SetAtonsAsync_ForwardsArrayAsSingleArg()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);
        var atons = new object[] { new { id = "buoy1" } };

        await sut.SetAtonsAsync(atons);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setAtons");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(atons);
    }

    [Test]
    public async Task SetAtonsVisibleAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        await sut.SetAtonsVisibleAsync(true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setAtonsVisible");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsTrue();
    }

    [Test]
    public async Task SetOwnMmsiAsync_PassesString()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        await sut.SetOwnMmsiAsync("123456789");

        await Assert.That(fake.Calls[0].id).IsEqualTo("setOwnMmsi");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("123456789");
    }

    [Test]
    public async Task SetHarborModeAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        await sut.SetHarborModeAsync(false);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setHarborMode");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsFalse();
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        sut.MarkDisposed();
        await sut.UpdateAisTargetsAsync(new object[] { });
        await sut.SetAtonsAsync(new object[] { });
        await sut.SetAtonsVisibleAsync(true);
        await sut.SetOwnMmsiAsync("x");
        await sut.SetHarborModeAsync(true);

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapAisJs(fake);

        await sut.SetAtonsVisibleAsync(true);
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapAisJs(fake);

        await sut.SetOwnMmsiAsync("x");
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        // A real JS-side regression must surface so it doesn't hide
        // behind the lifecycle swallow.
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapAisJs(fake);

        await Assert.ThrowsAsync<JSException>(() => sut.SetHarborModeAsync(true));
    }

    [Test]
    public async Task FocusVesselAsync_PassesContextAndReturnsJsResult()
    {
        var fake = new RecordingJsRef();
        fake.Returns["focusVessel"] = true;
        var sut = new MapAisJs(fake);

        bool result = await sut.FocusVesselAsync("vessels.urn:mrn:imo:mmsi:123");

        await Assert.That(fake.Calls[0].id).IsEqualTo("focusVessel");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("vessels.urn:mrn:imo:mmsi:123");
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task FocusVesselAsync_AfterDisposed_ReturnsFalseWithoutCall()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);
        sut.MarkDisposed();

        bool result = await sut.FocusVesselAsync("anything");

        await Assert.That(result).IsFalse();
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FocusVesselAsync_OnDisconnected_ReturnsFalse()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapAisJs(fake);

        bool result = await sut.FocusVesselAsync("anything");

        await Assert.That(result).IsFalse();
    }
}
