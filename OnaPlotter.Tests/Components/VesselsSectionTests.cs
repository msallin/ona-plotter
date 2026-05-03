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

    // Sections start collapsed and stay that way until the helm taps
    // the header. Earlier we auto-expanded on any danger / warn CPA,
    // but that meant the panel opened itself mid-edit every time a
    // distant ferry's TCPA dipped below the threshold; alarms +
    // banner stack already surface the threat. Tests unconditionally
    // click-to-expand now.
    private static void Expand(IRenderedComponent<VesselsSection> cut)
    {
        if (cut.FindAll(".vessel-row").Count == 0)
            cut.Find(".section-toggle").Click();
    }

    [Test]
    public async Task DangerClass_AppliedWhen_Cpa_BelowPointFive_Tcpa_Below10()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Near", cpa: 0.3, tcpa: 5) }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("vessel-danger");
        await Assert.That(cut.Markup).DoesNotContain("vessel-warn");
    }

    [Test]
    public async Task WarnClass_AppliedWhen_Cpa_BelowOne_Tcpa_Below20()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Nearish", cpa: 0.8, tcpa: 15) }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("vessel-warn");
    }

    [Test]
    public async Task Safe_Vessel_HasNeitherDangerNorWarn()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Safe", cpa: 3.0, tcpa: 30) }));
        Expand(cut);

        await Assert.That(cut.Markup).DoesNotContain("vessel-danger");
        await Assert.That(cut.Markup).DoesNotContain("vessel-warn");
    }

    [Test]
    public async Task Buddy_Shows_StarPrefix()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Morning Star", buddy: true) }));
        Expand(cut);

        // U+2605 is the black star used as the buddy glyph.
        await Assert.That(cut.Markup).Contains("\u2605 Morning Star");
    }

    [Test]
    public async Task ColregsRow_Shows_GiveWay_WithDangerColor()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] {
                V("c1", "Ship", colregsLabel: "Crossing (S)", colregsRole: "Give way")
            }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("Crossing (S)");
        await Assert.That(cut.Markup).Contains("Give way");
        await Assert.That(cut.Markup).Contains("colregs-giveway");
    }

    [Test]
    public async Task StaysCollapsed_UntilHelmToggles_EvenWithDangerVessel()
    {
        // We removed the auto-expand-on-threat behaviour: the Vessels
        // section now stays collapsed regardless of CPA/TCPA until
        // the helm taps the header. Threats surface via alarms + the
        // banner stack, not by opening random panel sections during
        // unrelated edit flows.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Near", cpa: 0.2, tcpa: 4) }));
        await Assert.That(cut.FindAll(".vessel-row").Count).IsEqualTo(0);

        cut.Find(".section-toggle").Click();
        await Assert.That(cut.FindAll(".vessel-row").Count).IsEqualTo(1);
        await Assert.That(cut.Markup).Contains("vessel-danger");
    }

    [Test]
    public async Task StaysCollapsed_When_NoDangerOrWarn()
    {
        // Default-collapsed applies regardless of CPA; routine fleet
        // view stays compact just like threat view does.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Safe", cpa: 3.0, tcpa: 30) }));
        await Assert.That(cut.FindAll(".vessel-row").Count).IsEqualTo(0);
    }

    [Test]
    public async Task OnFocus_Fires_WithVesselContext_OnRowClick()
    {
        using var ctx = new Bunit.TestContext();
        string? focused = null;
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("vessels.ctx1", "Foo") })
            .Add(x => x.OnFocus, EventCallback.Factory.Create<string>(this, s => focused = s)));
        Expand(cut);

        cut.Find(".vessel-row").Click();

        await Assert.That(focused).IsEqualTo("vessels.ctx1");
    }
}
