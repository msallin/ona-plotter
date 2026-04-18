using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// Targeted bUnit tests for the row-class helper and the star prefix
/// on buddy vessels. These are the exact things that cost us time in
/// earlier reviews (false buddy matches, wrong danger class).
/// </summary>
public class VesselsSectionTests
{
    private static VesselListEntry V(
        string ctx, string name, string? mmsi = null,
        double? cpa = null, double? tcpa = null, bool buddy = false,
        string? colregsLabel = null, string? colregsRole = null) =>
        new(Context: ctx, DisplayName: name, Mmsi: mmsi,
            CpaNm: cpa, TcpaMin: tcpa, DistanceNm: null, BearingDeg: null,
            SogKn: null, ShipType: null, IsBuddy: buddy,
            ColregsLabel: colregsLabel, ColregsRole: colregsRole);

    [Test]
    public async Task DangerClass_AppliedWhen_Cpa_BelowPointFive_Tcpa_Below10()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Near", cpa: 0.3, tcpa: 5) }));

        await Assert.That(cut.Markup).Contains("vessel-danger");
        await Assert.That(cut.Markup).DoesNotContain("vessel-warn");
    }

    [Test]
    public async Task WarnClass_AppliedWhen_Cpa_BelowOne_Tcpa_Below20()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Nearish", cpa: 0.8, tcpa: 15) }));

        await Assert.That(cut.Markup).Contains("vessel-warn");
    }

    [Test]
    public async Task Safe_Vessel_HasNeitherDangerNorWarn()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Safe", cpa: 3.0, tcpa: 30) }));

        await Assert.That(cut.Markup).DoesNotContain("vessel-danger");
        await Assert.That(cut.Markup).DoesNotContain("vessel-warn");
    }

    [Test]
    public async Task Buddy_Shows_StarPrefix()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Morning Star", buddy: true) }));

        // U+2605 is the black star used as the buddy glyph.
        await Assert.That(cut.Markup).Contains("\u2605 Morning Star");
    }

    [Test]
    public async Task ColregsRow_Shows_GiveWay_WithDangerColor()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] {
                V("c1", "Ship", colregsLabel: "Crossing (stbd)", colregsRole: "Give way")
            }));

        await Assert.That(cut.Markup).Contains("Crossing (stbd)");
        await Assert.That(cut.Markup).Contains("Give way");
        await Assert.That(cut.Markup).Contains("colregs-giveway");
    }

    [Test]
    public async Task OnFocus_Fires_WithVesselContext_OnRowClick()
    {
        using var ctx = new Bunit.TestContext();
        string? focused = null;
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("vessels.ctx1", "Foo") })
            .Add(x => x.OnFocus, EventCallback.Factory.Create<string>(this, s => focused = s)));

        cut.Find(".vessel-row").Click();

        await Assert.That(focused).IsEqualTo("vessels.ctx1");
    }
}
