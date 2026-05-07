using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// Buddy-MMSI exact-match behaviour was a review punch-list item - if
/// the lookup falls back to Contains() we regress to the false-match
/// bug. These tests pin the correct behaviour.
/// </summary>
public class BuddiesSectionTests
{
    private static SignalkBuddy Buddy(string urn, string name) => new(urn, name);

    private static VesselListEntry Vessel(string ctx, string name, string? mmsi, double? distNm = null) =>
        new(Context: ctx, DisplayName: name, Mmsi: mmsi,
            CpaNm: null, TcpaMin: null, DistanceNm: distNm, BearingDeg: null,
            SogKn: null, ShipType: null, IsBuddy: true,
            ColregsLabel: null, ColregsRole: null);

    // Sections start collapsed; expand before asserting on inner rows.
    private static void Expand(IRenderedComponent<BuddiesSection> cut) =>
        cut.Find(".section-toggle").Click();

    [Test]
    public async Task BuddyWithNoMatchingVessel_Shows_NotInAisRange()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<BuddiesSection>(p => p
            .Add(x => x.Buddies, new[] { Buddy("urn:mrn:imo:mmsi:1234", "Friend") })
            .Add(x => x.Vessels, Array.Empty<VesselListEntry>()));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("Friend");
        await Assert.That(cut.Markup).Contains("Not in AIS range");
    }

    [Test]
    public async Task BuddyWithMatchingMmsi_Shows_Distance()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<BuddiesSection>(p => p
            .Add(x => x.Buddies, new[] { Buddy("urn:mrn:imo:mmsi:1234", "Friend") })
            .Add(x => x.Vessels, new[] {
                Vessel("vessels.urn:mrn:imo:mmsi:1234", "Friend Live", "1234", distNm: 2.4)
            }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("2.4 nm");
        await Assert.That(cut.Markup).DoesNotContain("Not in AIS range");
    }

    [Test]
    public async Task BuddyNameContainingOtherMmsi_Does_Not_FalseMatch()
    {
        // A buddy's name contains "1234" as a substring. Under the old
        // Contains-based lookup this matched a different vessel whose
        // MMSI happened to be 1234. Ensure the exact-Mmsi match fixes it.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<BuddiesSection>(p => p
            .Add(x => x.Buddies, new[] { Buddy("urn:mrn:imo:mmsi:9999", "Boat 1234 Doe") })
            .Add(x => x.Vessels, new[] {
                Vessel("vessels.urn:mrn:imo:mmsi:1234", "Different Boat", "1234", distNm: 5.0)
            }));
        Expand(cut);

        // Different vessel's distance must NOT leak into the buddy row.
        await Assert.That(cut.Markup).Contains("Boat 1234 Doe");
        await Assert.That(cut.Markup).Contains("Not in AIS range");
        await Assert.That(cut.Markup).DoesNotContain("5.0 nm");
    }

    [Test]
    public async Task No_Refresh_Button_In_Section()
    {
        // The Refresh button was pulled (user feedback - the buddy list
        // refreshes itself on reconnect / on the BuddyList seed, the
        // in-panel button was visual noise). If it comes back, ship an
        // explicit Parameter + test; don't leak the old title back in.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<BuddiesSection>(p => p
            .Add(x => x.Buddies, new[] { Buddy("urn:mrn:imo:mmsi:1", "A") })
            .Add(x => x.Vessels, Array.Empty<VesselListEntry>()));

        await Assert.That(cut.FindAll("button[title='Re-fetch the buddy list from the plugin']").Count)
            .IsEqualTo(0);
    }
}
