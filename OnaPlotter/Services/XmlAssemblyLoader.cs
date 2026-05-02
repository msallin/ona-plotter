using Microsoft.AspNetCore.Components.WebAssembly.Services;

namespace OnaPlotter.Services;

/// <summary>
/// One-shot lazy loader for <c>System.Private.Xml.wasm</c>. The XML
/// stack is only used by GPX import / export on the Resources +
/// History pages; keeping it out of the boot bundle saves ~500 KB
/// raw / ~100 KB brotli on cold start, a meaningful chunk of the
/// marine-4G first-paint budget.
/// <para>
/// Subsequent calls after the first GPX action cost nothing extra:
/// the runtime caches the loaded module, and this wrapper memoises
/// the resulting Task so concurrent or repeat calls share the same
/// completion. WASM today is single-threaded, so the <c>??=</c>
/// guard is sufficient; should the runtime ever go multi-threaded
/// the worst case is a duplicate fetch (the underlying loader is
/// itself idempotent), not a correctness bug.
/// </para>
/// </summary>
public sealed class XmlAssemblyLoader
{
    // Two-piece load: System.Private.Xml.wasm holds the bulk
    // (XmlException, XmlReader/Writer, the older XmlDocument tree),
    // System.Private.Xml.Linq.wasm holds the XDocument / XElement
    // implementation that ResourceImporter / ResourceExporter call.
    // Both are listed as <BlazorWebAssemblyLazyLoad> in csproj so they
    // ship as separate downloadable assets; LazyAssemblyLoader fetches
    // them in a single round trip.
    private static readonly string[] XmlAssemblies =
    [
        "System.Private.Xml.wasm",
        "System.Private.Xml.Linq.wasm",
    ];
    private readonly LazyAssemblyLoader _loader;
    private Task? _loadTask;

    public XmlAssemblyLoader(LazyAssemblyLoader loader) => _loader = loader;

    /// <summary>Idempotent. Awaits the single shared load on first call;
    /// subsequent calls return the cached completed task.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        // LoadAssembliesAsync returns the loaded assemblies; we don't
        // need the result -- the side effect (the runtime registers
        // the assemblies) is what unlocks XDocument.Parse etc.
        await _loader.LoadAssembliesAsync(XmlAssemblies);
    }
}
