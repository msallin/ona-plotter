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
        string? colregsLabel = null, string? colregsRole = null,
        double? loa = null, double? beam = null,
        double? distNm = null) =>
        new(Context: ctx, DisplayName: name, Mmsi: mmsi,
            CpaNm: cpa, TcpaMin: tcpa, DistanceNm: distNm, BearingDeg: null,
            SogKn: null, ShipType: null, IsBuddy: buddy,
            ColregsLabel: colregsLabel, ColregsRole: colregsRole,
            LengthOverallMeters: loa, BeamMeters: beam);

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

    // --- LOA / Beam dimension chip (PR #221) -------------------------
    //
    // The row appends a single dot-separated dimensions segment to the
    // existing distance / SOG line. Four branches matter:
    //   1. LOA + Beam present    -> "32.5 × 6.2 m"
    //   2. LOA only              -> "L 32.5 m"
    //   3. Beam only             -> "B 6.2 m"
    //   4. Neither               -> no dimensions span at all
    // All four are exercised below. Reaching the fallback branch is
    // important because most class-B targets never broadcast Type 24 -
    // a regression that defaulted those to "0.0 × 0.0 m" would render
    // a misleading chip on the most common harbour traffic.

    [Test]
    public async Task Dimensions_Both_Render_As_Joined_Chip()
    {
        // Helm is looking at a class-A target with full static; the
        // "32.5 × 6.2 m" chip lets them eyeball the size without
        // opening the popup.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] {
                V("c1", "Big Ship", distNm: 1.0, loa: 32.5, beam: 6.2)
            }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("32.5 × 6.2 m");  // U+00D7 = ×
        // None of the single-axis fallback prefixes should appear when
        // both dimensions are present - otherwise the row renders both
        // the joined chip AND a stray "L"/"B" segment.
        await Assert.That(cut.Markup).DoesNotContain("L 32.5 m");
        await Assert.That(cut.Markup).DoesNotContain("B 6.2 m");
    }

    [Test]
    public async Task Dimensions_LoaOnly_Renders_LengthPrefixed_Chip()
    {
        // LOA without beam: AIS Type 5 dim A + dim B were broadcast,
        // dim C + dim D missing or zero. Render "L 32.5 m" so the
        // helm understands which axis is shown rather than guessing.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] {
                V("c1", "Long Boat", distNm: 1.0, loa: 32.5, beam: null)
            }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("L 32.5 m");
        await Assert.That(cut.Markup).DoesNotContain("×");
    }

    [Test]
    public async Task Dimensions_BeamOnly_Renders_BeamPrefixed_Chip()
    {
        // Beam without LOA is rarer in practice but the schema permits
        // it. Mirror the LOA-only branch so the row is symmetric.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] {
                V("c1", "Wide Boat", distNm: 1.0, loa: null, beam: 6.2)
            }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("B 6.2 m");
        await Assert.That(cut.Markup).DoesNotContain("×");
    }

    [Test]
    public async Task Dimensions_NeitherSet_RendersNoChip()
    {
        // The common case for class-B traffic: never broadcast Type 24,
        // so both LOA and beam stay null. The dimensions segment must
        // be entirely absent - no "0.0 m", no "L  m", no separator dot
        // adding visual noise to the row.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<VesselsSection>(p => p
            .Add(x => x.Vessels, new[] {
                V("c1", "Small Boat", distNm: 1.0, loa: null, beam: null)
            }));
        Expand(cut);

        // None of the dimension markers should leak through.
        await Assert.That(cut.Markup).DoesNotContain("×");
        await Assert.That(cut.Markup).DoesNotContain("L 0");
        await Assert.That(cut.Markup).DoesNotContain("B 0");
        // " m" alone is too generic (the SOG segment uses " kn"; no
        // " m" appears anywhere else on the row when dimensions are
        // absent), so its absence is a sharper sentinel for the chip.
        await Assert.That(cut.Markup).DoesNotContain(" m</span>");
    }
}
