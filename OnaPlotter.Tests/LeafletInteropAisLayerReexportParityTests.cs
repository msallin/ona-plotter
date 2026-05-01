using System.Text.RegularExpressions;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the contract that every <c>export function ...</c> in
/// <c>aisLayer.js</c> that the C# side calls (via
/// <see cref="OnaPlotter.Services.Js.IMapAisJs"/>) is also
/// re-exported from <c>leafletInterop.js</c>. The C# JS module
/// reference is the leafletInterop.js module, NOT aisLayer.js
/// directly -- so an export that lives in aisLayer.js without a
/// matching re-export crashes at runtime in the helm's browser
/// with "X is not a function".
///
/// <para>This regressed once already (PR #140 added
/// <c>setGuardZoneWarningRingVisible</c> in aisLayer.js without the
/// leafletInterop.js re-export, then surfaced as a Blazor unhandled
/// exception when the Map page first pushed the flag). This parity
/// test fails before that ships.</para>
/// </summary>
public class LeafletInteropAisLayerReexportParityTests
{
    private static string ReadJs(string filename)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "OnaPlotter", "wwwroot", "js", filename);
        return File.ReadAllText(path);
    }

    [Test]
    public async Task EveryAisLayerExport_HasLeafletInteropReExport()
    {
        // Scrape both files; assert every aisLayer export name is
        // present somewhere in leafletInterop.js (either as another
        // `export function name` definition wrapping the call, or as
        // a `export const name = (...)` re-export). A few aisLayer
        // exports are internal (used only by other JS modules); the
        // skipList carries those so the parity test only complains
        // about names the C# side actually invokes.
        var aisLayer = ReadJs("aisLayer.js");
        var interop  = ReadJs("leafletInterop.js");

        // Match `export function NAME(` -- covers the regular export-
        // function declarations the file uses for its public API.
        var rx = new Regex(@"export\s+function\s+(?<name>[a-zA-Z_$][a-zA-Z0-9_$]*)\s*\(");
        var aisLayerExports = rx.Matches(aisLayer)
            .Select(m => m.Groups["name"].Value)
            .Distinct()
            .ToList();

        // Lifecycle / module-internal exports that don't need a
        // leafletInterop.js re-export. `init` + `dispose` are called
        // by leafletInterop.js itself during init / teardown
        // (aisLayerMod.init(...), aisLayerMod.dispose()). `setBoatPosition`
        // is invoked from the mux via the imported module reference
        // when own-vessel position updates. `resolveAisPopupTitle` is a
        // pure helper exported only so the Node-driven aisLayer.test.js
        // can pin its precedence chain; the C# side never calls it.
        // None of these are reachable from C#, so they don't need a
        // top-level re-export.
        var skipList = new HashSet<string>(StringComparer.Ordinal)
        {
            "init", "dispose", "setBoatPosition", "resolveAisPopupTitle",
        };

        await Assert.That(aisLayerExports.Count)
            .IsGreaterThan(5)
            .Because("sanity: regex should have caught most named exports");

        foreach (var name in aisLayerExports)
        {
            if (skipList.Contains(name)) continue;

            // The interop module re-exports either as
            //   export function name(...) { ... }
            // or
            //   export const name = (...) => aisLayerMod.name(...)
            // So we can scan for "export function name(" OR
            // "export const name = " OR "export {name}". Use a
            // generic word-boundary scan on the public-export form.
            bool reexported =
                Regex.IsMatch(interop, $@"export\s+function\s+{Regex.Escape(name)}\s*\(") ||
                Regex.IsMatch(interop, $@"export\s+const\s+{Regex.Escape(name)}\s*=") ||
                Regex.IsMatch(interop, $@"export\s*{{[^}}]*\b{Regex.Escape(name)}\b");
            await Assert.That(reexported)
                .IsTrue()
                .Because($"aisLayer.js exports '{name}' but leafletInterop.js does not " +
                         "re-export it. Either add an `export const name = (...) => " +
                         "aisLayerMod.name(...)` re-export in leafletInterop.js, OR " +
                         "add the name to LeafletInteropAisLayerReexportParityTests.skipList " +
                         "if it's genuinely internal-only.");
        }
    }
}
