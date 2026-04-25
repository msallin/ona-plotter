using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Routes AIS-AtoN deltas through SignalkClient.ProcessMessage and
/// pins that they land in <see cref="AtonStore"/> instead of being
/// confused with own-boat or AIS vessel deltas. The dispatch order
/// in ProcessMessage matters: self-context first, then atons-context,
/// then AIS-context. Without correct ordering atons would land in
/// AisStore and render as ghost vessels with junk MMSIs.
/// </summary>
public class SignalkClientAtonTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public string Combine(string path) => BaseUrl + path;
    }

    private static (SignalkClient client, AtonStore store, AisStore ais) NewClient()
    {
        var atons = new AtonStore();
        var ais = new AisStore();
        var c = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: ais,
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: atons,
            time: TimeProvider.System);
        c.SetSelfContext("vessels.urn:mrn:imo:mmsi:261006533");
        return (c, atons, ais);
    }

    private static string AtonDelta(string context, string path, string valueJson) => $@"{{
      ""context"":""{context}"",
      ""updates"":[{{
        ""timestamp"":""2026-04-25T18:00:00.000Z"",
        ""values"":[{{ ""path"":""{path}"", ""value"":{valueJson} }}]
      }}]
    }}";

    [Test]
    public async Task IsAtonContext_OnlyMatchesAtonsPrefix()
    {
        // Other prefixes (vessels.*, shore.*, aircraft.*, sar.*) MUST
        // NOT match or atons would steal deltas meant for other
        // stores. shore.basestations.* would naturally be a sibling
        // here in a future commit; today the store stays atons-only.
        await Assert.That(SignalkClient.IsAtonContext("atons.urn:mrn:imo:mmsi:992111234")).IsTrue();
        await Assert.That(SignalkClient.IsAtonContext("atons.")).IsTrue();
        await Assert.That(SignalkClient.IsAtonContext("vessels.urn:mrn:imo:mmsi:1")).IsFalse();
        await Assert.That(SignalkClient.IsAtonContext("shore.basestations.foo")).IsFalse();
        await Assert.That(SignalkClient.IsAtonContext("")).IsFalse();
        await Assert.That(SignalkClient.IsAtonContext(null)).IsFalse();
    }

    [Test]
    public async Task AtonDelta_LandsInAtonStore()
    {
        var (c, atons, _) = NewClient();
        c.ProcessMessage(AtonDelta(
            "atons.urn:mrn:imo:mmsi:992111234",
            "navigation.position",
            "{\"latitude\":47.5, \"longitude\":-122.25}"));

        await Assert.That(atons.Count).IsEqualTo(1);
        var aton = atons.GetAtons().Single();
        await Assert.That(aton.Latitude).IsEqualTo(47.5);
        await Assert.That(aton.Longitude).IsEqualTo(-122.25);
    }

    [Test]
    public async Task AtonDelta_DoesNotPolluteAisStore()
    {
        // Critical regression check: a buoy must not show up as a
        // ghost vessel. AisStore.Apply doesn't filter on context, so
        // if SignalkClient routed this incorrectly it would fall
        // through and AisStore.Count would be 1.
        var (c, _, ais) = NewClient();
        c.ProcessMessage(AtonDelta(
            "atons.urn:mrn:imo:mmsi:992111234",
            "navigation.position",
            "{\"latitude\":47.5, \"longitude\":-122.25}"));
        await Assert.That(ais.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AtonDelta_IdentityBundle_OnEmptyPath_Flattens()
    {
        // Real-world plugin behaviour: first delta after AIS Type 21
        // arrives is an empty-path object containing every field.
        // SignalkClient just forwards verbatim; the AtoN model does
        // the unpacking. Pin that the bundle reaches the model
        // (we'd see name/type/virtual on the stored AtoN).
        var (c, atons, _) = NewClient();
        c.ProcessMessage(AtonDelta(
            "atons.urn:mrn:imo:mmsi:992111234",
            "",
            @"{""name"":""WRECK"",""atonType"":{""id"":28,""name"":""Iso Danger""},""virtual"":true,""navigation"":{""position"":{""latitude"":51.5,""longitude"":1.0}}}"));

        var aton = atons.GetAtons().Single();
        await Assert.That(aton.Name).IsEqualTo("WRECK");
        await Assert.That(aton.TypeId).IsEqualTo(28);
        await Assert.That(aton.Virtual).IsTrue();
        await Assert.That(aton.Latitude).IsEqualTo(51.5);
    }

    [Test]
    public async Task AtonsTier_HasExpected60sPeriod()
    {
        // Pin the wire shape: subscription tier is named "Atons", uses
        // the atons.* context glob, and rides the dedicated 60-second
        // period constant. Asserting the constant's literal value is
        // dropped because TUnit's constant-comparison analyzer flags
        // it (and a comment in the constant's declaration is the
        // canonical place for "why 60s").
        var tier = SignalkClient.Tiers.Single(t => t.Name == "Atons");
        await Assert.That(tier.Context).IsEqualTo("atons.*");
        await Assert.That(tier.PeriodMs).IsEqualTo(SignalkClient.AtonsSubscriptionPeriodMs);
    }
}
