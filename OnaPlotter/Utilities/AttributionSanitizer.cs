using System.Net;

namespace OnaPlotter.Utilities;

/// <summary>
/// Sanitises chart attribution strings before they cross into Leaflet's
/// <c>AttributionControl</c>, which renders the value with
/// <c>innerHTML</c>. A SignalK chart provider is an in-scope trust
/// boundary (the helm picks the URL of the chart server; a hostile or
/// compromised provider would otherwise have a script-execution sink
/// in the WASM origin - read every other localStorage key, pivot to
/// the boat's LAN via fetch).
///
/// <para>The shape of the contract: built-in attributions (OSM /
/// OpenSeaMap, synthesised in <see cref="BuiltInCharts"/>) ship with
/// clickable ODbL / CC-BY-SA links and need to pass through unchanged.
/// SignalK-server attributions arrive over the network and are
/// untrusted; we HTML-escape them so any literal text (including a
/// would-be link) renders as plain text in the corner control. Helms
/// who want clickable provider links rely on the built-in basemaps;
/// SK-served charts get a literal-string credit, which is enough to
/// satisfy attribution requirements without opening an XSS sink.</para>
///
/// <para>Removable contract: drop this file + the call site in
/// <see cref="OnaPlotter.Services.Map.ChartLayerController"/> +
/// the <c>IsTrustedAttribution</c> flag on
/// <see cref="OnaPlotter.Models.SignalkChart"/>. Reverting puts the
/// raw chart-provider string back into Leaflet's innerHTML; do NOT
/// drop without replacing.</para>
/// </summary>
public static class AttributionSanitizer
{
    /// <summary>
    /// Returns a value safe to forward to Leaflet's
    /// <c>attribution</c> option. Trusted strings pass through
    /// unchanged (used for the built-in OSM + OpenSeaMap entries
    /// shipped in the WASM bundle). Untrusted strings are HTML-
    /// escaped so any tags / scripts / event handlers render as
    /// literal text rather than executing in the helm origin.
    /// Null becomes the empty string (the JS layer's
    /// <c>attribution || ''</c> idiom would do the same; we mirror
    /// it on the C# side so the value is non-null at the boundary).
    /// </summary>
    public static string Sanitize(string? raw, bool trusted)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (trusted) return raw;
        // WebUtility.HtmlEncode escapes <, >, &, ", ' so a payload
        // like `<img src=x onerror=...>` renders as visible text
        // rather than executing. Escape-only is the simplest robust
        // mitigation; an allowlist parser that preserves `<a>` is
        // defensible but pulls a parser into the bundle for a
        // marginal UX gain. SK chart servers can ship plain-text
        // attribution today (most ship empty), so the cost is low.
        return WebUtility.HtmlEncode(raw);
    }
}
