using System.Text.RegularExpressions;
using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// JS-interop-backed implementation of <see cref="IClientCapabilities"/>.
/// Reads <c>navigator.hardwareConcurrency</c> + <c>navigator.userAgent</c>
/// once per bootstrap. The heuristic mirrors the previous in-JS
/// detection in <c>leafletInterop.js::initMap</c> so the Map page's
/// behaviour doesn't change with the move; the difference is just
/// that the decision now sits on the C# side.
/// </summary>
public sealed class ClientCapabilitiesService : IClientCapabilities
{
    private readonly IJSRuntime _js;

    public ClientCapabilitiesService(IJSRuntime js) => _js = js;

    public bool IsSlowClient { get; private set; }

    /// <summary>Matches "arm", "armv6"/"armv7"/"armv8", or "raspberry"
    /// case-insensitively. Compiled because we evaluate it once per
    /// bootstrap; the cost is amortised by the multiple sites that
    /// later read <see cref="IsSlowClient"/>.
    /// <para>
    /// Example UAs that match: <c>Raspbian armv7l</c>,
    /// <c>Mozilla/5.0 (Linux; ARM; Tizen ...)</c>.
    /// </para></summary>
    private static readonly Regex SlowUaPattern = new(
        @"\barm\b|\barmv|raspberry",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Pure heuristic: maps a (hardwareConcurrency, userAgent)
    /// pair to the slow-client flag. Exposed as a static helper so the
    /// rule can be unit-tested without standing up a JS runtime.
    /// <para>Returns true on either signal: 4-or-fewer logical cores
    /// (Pi-class hardware) OR an ARM / Raspberry UA. Either alone is
    /// enough; both signals confirm but neither is necessary alone.
    /// </para>
    /// <para>Example: <c>(2, "Mozilla/5.0 ...")</c> -> true (low cores);
    /// <c>(8, "... armv7l ...")</c> -> true (ARM UA); <c>(8, "Chrome
    /// on x86_64")</c> -> false; <c>(0, "")</c> -> false (no signal).
    /// </para></summary>
    public static bool ResolveIsSlowClient(int hardwareConcurrency, string? userAgent)
    {
        bool fewCores = hardwareConcurrency > 0 && hardwareConcurrency <= 4;
        bool armUa = !string.IsNullOrEmpty(userAgent)
                     && SlowUaPattern.IsMatch(userAgent);
        return fewCores || armUa;
    }

    public async Task InitializeAsync()
    {
        // Pull both signals in one round-trip rather than two interop
        // calls. The eval returns a typed object so we can deserialise
        // into a record below; falls back to the desktop-class default
        // (IsSlowClient = false) on any interop error.
        try
        {
            var probe = await _js.InvokeAsync<NavigatorProbe?>("eval",
                "({ hc: navigator.hardwareConcurrency || 0, "
                + "ua: navigator.userAgent || '' })");
            if (probe is null) return;
            IsSlowClient = ResolveIsSlowClient(probe.hc, probe.ua);
        }
        catch (JSException) { /* leave at default */ }
        catch (JSDisconnectedException) { /* page tore down mid-bootstrap */ }
    }

    /// <summary>Shape of the eval'd probe object. Lower-case names match
    /// the JS object literal so System.Text.Json can deserialise without
    /// a custom naming policy.</summary>
    private sealed record NavigatorProbe(int hc, string ua);
}
