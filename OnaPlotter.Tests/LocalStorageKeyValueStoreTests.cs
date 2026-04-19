using Bunit;
using Microsoft.JSInterop;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the Prefix + JS-interop contract of LocalStorageKeyValueStore.
/// The store is thin but safety-critical: every persisted setting
/// (night mode, chart order, anchor radius, draft, polar CSV) lives
/// under the "ona." namespace so the wrong prefix silently orphans
/// a user's configuration.
/// </summary>
public class LocalStorageKeyValueStoreTests
{
    [Test]
    public async Task GetAsync_Prefixes_Key_And_Calls_GetItem()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "ona.nightMode")
            .SetResult("true");
        var store = new LocalStorageKeyValueStore(ctx.JSInterop.JSRuntime);

        var value = await store.GetAsync("nightMode");

        await Assert.That(value).IsEqualTo("true");
        // bUnit throws if the setup didn't match, so reaching here
        // already confirms the "ona.nightMode" prefix was passed.
    }

    [Test]
    public async Task GetAsync_Returns_Null_When_Unset()
    {
        // JS localStorage.getItem returns null for unknown keys. The
        // wrapper must surface that as C# null so the caller can fall
        // back to a default rather than blow up parsing "null" as a
        // double or bool.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "ona.nope").SetResult(null);
        var store = new LocalStorageKeyValueStore(ctx.JSInterop.JSRuntime);

        await Assert.That(await store.GetAsync("nope")).IsNull();
    }

    [Test]
    public async Task SetAsync_Prefixes_Key_And_Forwards_Value()
    {
        // Capture the invocation via the setup's matcher so we can assert
        // the args the wrapper forwarded. SetVoidResult() completes the
        // handler -- without it the InvokeVoidAsync task stays pending
        // and await hangs forever (bUnit 1.x semantics: a Setup with no
        // SetResult / SetVoidResult / SetException never fires).
        using var ctx = new Bunit.TestContext();
        object?[]? captured = null;
        ctx.JSInterop.SetupVoid("localStorage.setItem", i =>
        {
            captured = i.Arguments.ToArray();
            return true;
        }).SetVoidResult();
        var store = new LocalStorageKeyValueStore(ctx.JSInterop.JSRuntime);

        await store.SetAsync("depthAlarmThreshold", "3.5");

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Length).IsEqualTo(2);
        await Assert.That((string?)captured[0]).IsEqualTo("ona.depthAlarmThreshold");
        await Assert.That((string?)captured[1]).IsEqualTo("3.5");
    }

    [Test]
    public async Task RemoveAsync_Prefixes_Key_And_Calls_RemoveItem()
    {
        using var ctx = new Bunit.TestContext();
        object?[]? captured = null;
        ctx.JSInterop.SetupVoid("localStorage.removeItem", i =>
        {
            captured = i.Arguments.ToArray();
            return true;
        }).SetVoidResult();
        var store = new LocalStorageKeyValueStore(ctx.JSInterop.JSRuntime);

        await store.RemoveAsync("gone");

        await Assert.That(captured).IsNotNull();
        await Assert.That((string?)captured![0]).IsEqualTo("ona.gone");
    }

    [Test]
    public async Task JsException_Propagates_So_Caller_Can_Recover()
    {
        // LocalStorage throws in private-browsing mode or when quota is
        // exhausted. AppSettingsService.Save() has a catch that swallows
        // JSException; if the KV store silently swallowed too we'd never
        // know a setting failed to persist.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.SetupVoid("localStorage.setItem", "ona.x", "y")
            .SetException(new JSException("quota exceeded"));
        var store = new LocalStorageKeyValueStore(ctx.JSInterop.JSRuntime);

        await Assert.That(async () => await store.SetAsync("x", "y")).Throws<JSException>();
    }

    [Test]
    public async Task IKeyValueStore_GetAsync_Interface_Adapts_To_Task()
    {
        // The interface returns Task<string?>; the concrete class exposes
        // ValueTask<string?>. The explicit interface impl bridges; this
        // pins that nothing broke the adaptation.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "ona.k").SetResult("v");
        IKeyValueStore store = new LocalStorageKeyValueStore(ctx.JSInterop.JSRuntime);

        await Assert.That(await store.GetAsync("k")).IsEqualTo("v");
    }
}
