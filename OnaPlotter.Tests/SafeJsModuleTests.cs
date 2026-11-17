using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>Pins the lifecycle-safe interop helpers every wrapper and
/// component goes through.</summary>
public class SafeJsModuleTests
{
    [Test]
    public async Task InvokeVoid_ForwardsIdentifierAndArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new SafeJsModule(fake);

        await sut.InvokeVoidAsync("f", 1, "two");

        await Assert.That(fake.Calls.Count).IsEqualTo(1);
        await Assert.That(fake.Calls[0].id).IsEqualTo("f");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(2);
    }

    [Test]
    public async Task InvokeVoid_AfterMarkDisposed_DoesNotCallModule()
    {
        var fake = new RecordingJsRef();
        var sut = new SafeJsModule(fake);
        sut.MarkDisposed();

        await sut.InvokeVoidAsync("f");

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvokeVoid_SwallowsDisconnectedAndObjectDisposed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new SafeJsModule(fake);
        await sut.InvokeVoidAsync("f");

        fake.ThrowDisposedNext = true;
        await sut.InvokeVoidAsync("f");

        await Assert.That(fake.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task InvokeVoid_JsException_PropagatesWithoutHandler()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new SafeJsModule(fake);

        await Assert.That(async () => await sut.InvokeVoidAsync("f")).Throws<JSException>();
    }

    [Test]
    public async Task InvokeVoid_JsException_GoesToHandlerWhenGiven()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        string? seen = null;
        var sut = new SafeJsModule(fake, (id, _) => seen = id);

        await sut.InvokeVoidAsync("boom");

        await Assert.That(seen).IsEqualTo("boom");
    }

    [Test]
    public async Task Invoke_ReturnsModuleValue()
    {
        var fake = new RecordingJsRef();
        fake.Returns["g"] = 42;
        var sut = new SafeJsModule(fake);

        await Assert.That(await sut.InvokeAsync(-1, "g")).IsEqualTo(42);
    }

    [Test]
    public async Task Invoke_WhenDisposedOrDisconnected_ReturnsFallback()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new SafeJsModule(fake);
        await Assert.That(await sut.InvokeAsync(-1, "g")).IsEqualTo(-1);

        fake.ThrowDisposedNext = true;
        await Assert.That(await sut.InvokeAsync(-1, "g")).IsEqualTo(-1);

        sut.MarkDisposed();
        fake.Calls.Clear();
        await Assert.That(await sut.InvokeAsync(-1, "g")).IsEqualTo(-1);
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Invoke_JsException_Propagates()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new SafeJsModule(fake);

        await Assert.That(async () => await sut.InvokeAsync(0, "g")).Throws<JSException>();
    }

    [Test]
    public async Task QuietExtensions_NullModuleIsNoOp()
    {
        IJSObjectReference? module = null;

        await module.InvokeVoidQuietAsync("f");
        await module.DisposeQuietlyAsync();
    }

    [Test]
    public async Task InvokeVoidQuiet_SwallowsLifecycleExceptionsOnly()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        await ((IJSObjectReference)fake).InvokeVoidQuietAsync("f");
        fake.ThrowDisposedNext = true;
        await ((IJSObjectReference)fake).InvokeVoidQuietAsync("f");

        fake.ThrowJsExceptionNext = true;
        await Assert.That(async () => await ((IJSObjectReference)fake).InvokeVoidQuietAsync("f"))
            .Throws<JSException>();
    }

    [Test]
    public async Task DisposeQuietly_SwallowsDisconnectedAndDisposed()
    {
        await new ThrowingDisposeRef(new JSDisconnectedException("x")).DisposeQuietlyAsync();
        await new ThrowingDisposeRef(new ObjectDisposedException("x")).DisposeQuietlyAsync();
    }

    private sealed class ThrowingDisposeRef(Exception toThrow) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
        public ValueTask DisposeAsync() => throw toThrow;
    }
}
