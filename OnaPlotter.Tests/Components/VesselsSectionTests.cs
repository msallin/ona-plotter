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

    // Sections start collapsed unless a vessel is already in the
    // danger/warn band -- OnParametersSet auto-expands in that case so
    // the helm doesn't have to dig for the threat row. Toggle only
    // when still collapsed so the tests don't fight the auto-expand.
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
                V("c1", "Ship", colregsLabel: "Crossing (stbd)", colregsRole: "Give way")
            }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("Crossing (stbd)");
        await Assert.That(cut.Markup).Contains("Give way");
        await Assert.That(cut.Markup).Contains("colregs-giveway");
    }

    [Test]
    public async Task AutoExpands_When_Vessel_InDangerBand()
    {
        // OnParametersSet auto-expands so the helm sees the threat
        // the moment they open the Layers panel. Pin the behaviour
        // so a future edit doesn't silently regress it back to
        // "always collapsed".
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Near", cpa: 0.2, tcpa: 4) }));
        // No Expand() call -- the render should already have the row.
        await Assert.That(cut.FindAll(".vessel-row").Count).IsEqualTo(1);
        await Assert.That(cut.Markup).Contains("vessel-danger");
    }

    [Test]
    public async Task StaysCollapsed_When_NoDangerOrWarn()
    {
        // Auto-expand only triggers on danger/warn -- a routine fleet
        // view stays compact.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Safe", cpa: 3.0, tcpa: 30) }));
        await Assert.That(cut.FindAll(".vessel-row").Count).IsEqualTo(0);
    }

    [Test]
    public async Task UserCollapse_IsSticky_EvenAfterAutoExpandCase()
    {
        // If the helm manually collapses, a new render with a danger
        // vessel must NOT re-expand -- fighting the user's intent.
        // _userToggled is the flag; pin it.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] { V("c1", "Near", cpa: 0.2, tcpa: 4) }));
        await Assert.That(cut.FindAll(".vessel-row").Count).IsEqualTo(1); // auto-expanded
        cut.Find(".section-toggle").Click();                              // user collapses
        await Assert.That(cut.FindAll(".vessel-row").Count).IsEqualTo(0);
        // Re-render with a fresh parameter set (still danger). Must
        // respect the collapse -- _userToggled gate.
        cut.SetParametersAndRender(p => p
            .Add(x => x.Vessels, new[] { V("c2", "Near2", cpa: 0.2, tcpa: 4) }));
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
