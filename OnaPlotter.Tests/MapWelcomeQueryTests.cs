using OnaPlotter.Components.Pages;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="Map.ShouldForceShowFromUri"/>: the pure URI parser
/// that decides whether the welcome card should be force-shown despite
/// the dismissed KV flag. Earlier code used Contains("welcome=1")
/// substring matching, which would over-match on URIs like
/// /map?other=welcome=1abc - this test class is the safety net for
/// any future regression to the substring approach.
/// </summary>
public class MapWelcomeQueryTests
{
    [Test]
    [Arguments("https://boat.local/map?welcome=1", true)]
    [Arguments("http://boat.local/signalk-onaplotter/map?welcome=1", true)]
    [Arguments("https://boat.local/map?welcome=1&other=foo", true)]
    [Arguments("https://boat.local/map?other=foo&welcome=1", true)]
    [Arguments("https://boat.local/map", false)]
    [Arguments("https://boat.local/map?", false)]
    public async Task ShouldForceShow_RecognisesExplicitWelcomeOne(string uri, bool expected)
    {
        await Assert.That(Map.ShouldForceShowFromUri(uri)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://boat.local/map?welcome=0")]
    [Arguments("https://boat.local/map?welcome=true")]
    [Arguments("https://boat.local/map?welcome=yes")]
    [Arguments("https://boat.local/map?welcome=")]
    public async Task ShouldForceShow_OtherWelcomeValues_ReturnFalse(string uri)
    {
        // Only "1" triggers; the contract is strict so a future
        // "welcome=v2" or "welcome=true" doesn't surprise the helm.
        await Assert.That(Map.ShouldForceShowFromUri(uri)).IsFalse();
    }

    [Test]
    [Arguments("https://boat.local/map?other=welcome=1")]
    [Arguments("https://boat.local/map?id=welcome=1abc")]
    [Arguments("https://boat.local/map?notwelcome=1")]
    [Arguments("https://boat.local/map?welcomes=1")]
    public async Task ShouldForceShow_SubstringCollisions_DoNotTrigger(string uri)
    {
        // Regression for PARA-001: the Contains("welcome=1") version
        // matched all of these, surfacing the welcome card on a URL
        // the helm never asked for. Strict parsed-key match.
        await Assert.That(Map.ShouldForceShowFromUri(uri)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-a-uri")]
    [Arguments("://broken")]
    public async Task ShouldForceShow_MalformedOrEmpty_ReturnsFalse(string? uri)
    {
        // Defensive: a malformed Nav.Uri must not crash OnInitialized;
        // the helper returns false and the welcome card stays gated
        // on the normal dismissed-flag path.
        await Assert.That(Map.ShouldForceShowFromUri(uri)).IsFalse();
    }

    [Test]
    public async Task ShouldForceShow_DuplicateWelcomeKeys_ReturnsFalse()
    {
        // ?welcome=1&welcome=1 produces a multi-value collection in
        // QueryHelpers; the helper requires exactly one value to
        // avoid responding to a copy-paste-doubled URL where the
        // helm's actual intent is unclear.
        await Assert.That(Map.ShouldForceShowFromUri(
            "https://boat.local/map?welcome=1&welcome=1")).IsFalse();
    }
}
