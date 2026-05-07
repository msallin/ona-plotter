using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapAnchorJs wrapper around the leafletInterop.js
/// anchor-watch surface. Tests hit the four happy paths via a
/// recording fake JSObjectReference; the JSDisconnected /
/// ObjectDisposed swallows are pinned via the <c>MarkDisposed</c>
/// path (subsequent calls become no-ops without throwing).
/// </summary>
public class MapAnchorJsTests
{
    /// <summary>
    /// Recording fake. Captures (identifier, args) tuples per call.
    /// Throws on dispose so calls AFTER dispose surface the
    /// expected ObjectDisposed which the wrapper must swallow.
    /// </summary>
    private sealed class RecordingJsRef : IJSObjectReference
    {
        public List<(string id, object?[] args)> Calls { get; } = new();
        public bool ThrowDisconnectedNext { get; set; }
        public bool ThrowDisposedNext { get; set; }
        public bool ThrowJsExceptionNext { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? Array.Empty<object?>()));
            if (ThrowDisconnectedNext)
            {
                ThrowDisconnectedNext = false;
                throw new JSDisconnectedException("test disconnect");
            }
            if (ThrowDisposedNext)
            {
                ThrowDisposedNext = false;
                throw new ObjectDisposedException("test");
            }
            if (ThrowJsExceptionNext)
            {
                ThrowJsExceptionNext = false;
                throw new JSException("test js error");
            }
            return new ValueTask<TValue>(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task SetAnchorAsync_PassesAllArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAnchorJs(fake);

        await sut.SetAnchorAsync(54.5, 11.2, 30);

        await Assert.That(fake.Calls.Count).IsEqualTo(1);
        await Assert.That(fake.Calls[0].id).IsEqualTo("setAnchor");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(3);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(11.2);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(30.0);
    }

    [Test]
    public async Task ClearAnchorAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAnchorJs(fake);

        await sut.ClearAnchorAsync();

        await Assert.That(fake.Calls.Count).IsEqualTo(1);
        await Assert.That(fake.Calls[0].id).IsEqualTo("clearAnchor");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SetAnchorRaisingAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAnchorJs(fake);

        await sut.SetAnchorRaisingAsync(true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setAnchorRaising");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsTrue();
    }

    [Test]
    public async Task UpdateAnchorRadiusAsync_PassesDouble()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAnchorJs(fake);

        await sut.UpdateAnchorRadiusAsync(45.5);

        await Assert.That(fake.Calls[0].id).IsEqualTo("updateAnchorRadius");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(45.5);
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAnchorJs(fake);

        sut.MarkDisposed();
        await sut.SetAnchorAsync(1, 2, 3);
        await sut.ClearAnchorAsync();
        await sut.SetAnchorRaisingAsync(true);
        await sut.UpdateAnchorRadiusAsync(10);

        // Without the MarkDisposed gate every method would have hit
        // the JS module reference; with it, all four become silent.
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        // Page is unmounting - the wrapper must not bubble
        // JSDisconnectedException to the caller, since every call
        // site would need the same try/catch otherwise.
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapAnchorJs(fake);

        await sut.SetAnchorAsync(1, 2, 3);
        // No exception propagated; first call recorded.
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapAnchorJs(fake);

        await sut.ClearAnchorAsync();
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        // A real JS-side bug must surface so the regression doesn't
        // hide behind the lifecycle swallow. The wrapper only
        // tolerates "page is going away" noise.
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapAnchorJs(fake);

        await Assert.ThrowsAsync<JSException>(() => sut.SetAnchorAsync(1, 2, 3));
    }
}
