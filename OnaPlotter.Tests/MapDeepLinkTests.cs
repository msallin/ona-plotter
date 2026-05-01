using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the deep-link query parser used by Map.razor to land on a
/// requested resource (route / waypoint / note / region) when the
/// helm taps "Open on map" from the Resources page or follows a
/// shared link. Pure parser; the dispatch lives in the page.
/// </summary>
public class MapDeepLinkTests
{
    [Test]
    public async Task Parse_FocusRoute_ProducesFocusTarget()
    {
        var t = MapDeepLink.Parse("?focus=route:abc123");
        await Assert.That(t).IsNotNull();
        await Assert.That(t!.Value.Kind).IsEqualTo("route");
        await Assert.That(t!.Value.Id).IsEqualTo("abc123");
        await Assert.That(t!.Value.IsEdit).IsFalse();
    }

    [Test]
    public async Task Parse_EditRegion_FlipsIsEdit()
    {
        var t = MapDeepLink.Parse("?edit=region:xyz");
        await Assert.That(t).IsNotNull();
        await Assert.That(t!.Value.Kind).IsEqualTo("region");
        await Assert.That(t!.Value.Id).IsEqualTo("xyz");
        await Assert.That(t!.Value.IsEdit).IsTrue();
    }

    [Test]
    public async Task Parse_EditWinsOverFocus_WhenBothPresent()
    {
        // Real-world case: a deep link the helm constructed by hand
        // mixing both keys. Prefer the editor invocation so the
        // editor link "wins" when ambiguous.
        var t = MapDeepLink.Parse("?focus=route:r1&edit=region:r2");
        await Assert.That(t!.Value.Kind).IsEqualTo("region");
        await Assert.That(t!.Value.Id).IsEqualTo("r2");
        await Assert.That(t!.Value.IsEdit).IsTrue();
    }

    [Test]
    public async Task Parse_QueryWithoutLeadingQuestionMark_Works()
    {
        // Some callers (Uri.Query) include the '?', some don't. The
        // parser should accept both shapes.
        var t = MapDeepLink.Parse("focus=note:n1");
        await Assert.That(t!.Value.Kind).IsEqualTo("note");
        await Assert.That(t!.Value.Id).IsEqualTo("n1");
    }

    [Test]
    public async Task Parse_UnknownKeysIgnored()
    {
        // Foreign query params (analytics, third-party redirects) must
        // not derail the parse -- only focus / edit are meaningful.
        var t = MapDeepLink.Parse("?utm_source=email&focus=waypoint:w1&ref=share");
        await Assert.That(t!.Value.Kind).IsEqualTo("waypoint");
        await Assert.That(t!.Value.Id).IsEqualTo("w1");
    }

    [Test]
    public async Task Parse_UrlEscapedId_IsDecoded()
    {
        // Resource ids can include URL-unsafe characters (slashes in
        // SignalK URNs). The parser unescapes the value half so the
        // page's lookup matches the stored id.
        var t = MapDeepLink.Parse("?focus=route:abc%2Fdef");
        await Assert.That(t!.Value.Id).IsEqualTo("abc/def");
    }

    [Test]
    public async Task Parse_NullOrEmpty_ReturnsNull()
    {
        await Assert.That(MapDeepLink.Parse(null)).IsNull();
        await Assert.That(MapDeepLink.Parse("")).IsNull();
        await Assert.That(MapDeepLink.Parse("?")).IsNull();
    }

    [Test]
    public async Task Parse_MissingColon_ReturnsNull()
    {
        // The value half must split cleanly into kind:id; a malformed
        // value drops to null rather than guessing.
        await Assert.That(MapDeepLink.Parse("?focus=route")).IsNull();
        await Assert.That(MapDeepLink.Parse("?focus=:")).IsNull();
        await Assert.That(MapDeepLink.Parse("?focus=route:")).IsNull();
        await Assert.That(MapDeepLink.Parse("?focus=:id")).IsNull();
    }

    [Test]
    public async Task Parse_OnlyForeignParams_ReturnsNull()
    {
        var t = MapDeepLink.Parse("?utm_source=email&ref=share");
        await Assert.That(t).IsNull();
    }

    [Test]
    public async Task Parse_MultipleColonsInId_PreservesTrailingSegments()
    {
        // SignalK ids occasionally contain colons (urn:mrn:imo:mmsi:N).
        // Split on the FIRST colon only so the id keeps every later
        // segment intact.
        var t = MapDeepLink.Parse("?focus=waypoint:urn:mrn:plotter:w1");
        await Assert.That(t!.Value.Kind).IsEqualTo("waypoint");
        await Assert.That(t!.Value.Id).IsEqualTo("urn:mrn:plotter:w1");
    }
}
