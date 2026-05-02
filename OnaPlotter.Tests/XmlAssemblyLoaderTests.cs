using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the memoisation contract of <see cref="XmlAssemblyLoader"/>:
/// the underlying lazy-load is invoked at most once per instance,
/// regardless of how many GPX actions the helm fires.
///
/// <para>The production ctor takes Blazor's <c>LazyAssemblyLoader</c>
/// which requires the WASM runtime; the internal seam ctor takes a
/// <c>Func&lt;string[], Task&gt;</c> stand-in so we can drive the
/// behaviour from a plain test fixture.</para>
/// </summary>
public class XmlAssemblyLoaderTests
{
    [Test]
    public async Task EnsureLoaded_Invokes_Underlying_Once_Across_Repeat_Calls()
    {
        int calls = 0;
        var loader = new XmlAssemblyLoader(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        await loader.EnsureLoadedAsync();
        await loader.EnsureLoadedAsync();
        await loader.EnsureLoadedAsync();

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureLoaded_Concurrent_Awaits_Share_One_Underlying_Call()
    {
        // Concurrent callers must share the cached Task rather than
        // each kicking off their own load. The underlying delegate
        // hangs until released so we can observe both callers
        // queueing on the same in-flight task.
        var release = new TaskCompletionSource();
        int calls = 0;
        var loader = new XmlAssemblyLoader(_ =>
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        });

        var a = loader.EnsureLoadedAsync();
        var b = loader.EnsureLoadedAsync();

        // Both started; only one underlying load fired.
        await Assert.That(calls).IsEqualTo(1);
        // Releasing the underlying completes both awaiters.
        release.SetResult();
        await Task.WhenAll(a, b);
    }

    [Test]
    public async Task EnsureLoaded_Passes_Both_Xml_Assembly_Names()
    {
        // The actual lazy-load contract relies on these assembly
        // names matching the <BlazorWebAssemblyLazyLoad> entries in
        // csproj. Pinning the list here means a future "let's also
        // lazy-load X" change has to update this test, which is the
        // signal that the csproj also needs an entry.
        string[]? capturedNames = null;
        var loader = new XmlAssemblyLoader(asms =>
        {
            capturedNames = asms;
            return Task.CompletedTask;
        });

        await loader.EnsureLoadedAsync();

        await Assert.That(capturedNames).IsNotNull();
        await Assert.That(capturedNames!).Contains("System.Private.Xml.wasm");
        await Assert.That(capturedNames!).Contains("System.Private.Xml.Linq.wasm");
    }
}
