using Bunit;
using OnaPlotter.Components.Map.Hud;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// Component tests for HudAnchorCard. Same pattern as HudRouteCardTests:
/// pin the snapshot-equality contract and the render branches for the
/// server vs manual anchor cases.
/// </summary>
public class HudAnchorCardTests
{
    private static HudAnchorCard.AnchorHudSnapshot Snap(
        bool visible = true,
        bool dragging = false,
        bool manual = false,
        double manualRadius = 30,
        double? currentRadius = 12,
        double? maxRadius = 30,
        string? dormantReason = null,
        double? peakRadius = null,
        double? bearingTrue = null,
        double? distanceFromBow = null,
        double? rodeLength = null) =>
        new(visible, dragging, manual, manualRadius, currentRadius, maxRadius,
            dormantReason, peakRadius, bearingTrue, distanceFromBow, rodeLength);

    [Test]
    public async Task NotVisible_RendersNothing()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(visible: false)));
        await Assert.That(cut.FindAll(".anchor-panel").Count).IsEqualTo(0);
    }

    [Test]
    public async Task ServerDriven_ShowsDistAndRadius()
    {
        // Server-driven = Manual:false, plugin feeds maxRadius + currentRadius.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(manual: false)));
        await Assert.That(cut.Markup).Contains("Dist");
        await Assert.That(cut.Markup).Contains("Radius");
        // Chips must not render when server owns the radius.
        await Assert.That(cut.FindAll(".anchor-radius-chips").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Manual_ShowsRadiusChipsWhenCallbackProvided()
    {
        // Manual drop on this client: chips row + OnSetRadius wired.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(manual: true, currentRadius: null, maxRadius: null))
            .Add(x => x.OnSetRadius, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<double>(this, _ => { })));
        await Assert.That(cut.FindAll(".anchor-radius-chips").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Manual_NoChips_WhenCallbackMissing()
    {
        // If the consumer doesn't wire OnSetRadius we still render the
        // card but suppress the chips -- tapping them would be a no-op.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(manual: true)));
        await Assert.That(cut.FindAll(".anchor-radius-chips").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Dragging_AddsAnchorAlarmClass()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(dragging: true)));
        await Assert.That(cut.Find(".anchor-panel").ClassList.Contains("anchor-alarm")).IsTrue();
    }

    [Test]
    public async Task DormantReason_RendersHintWithWarningGlyph()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(dormantReason: "No tide data (install a plugin)")));
        await Assert.That(cut.Markup).Contains("No tide data");
        await Assert.That(cut.FindAll(".rule-dormant-hint").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Snapshot_EqualityIsMemberwise()
    {
        var a = Snap();
        var b = Snap();
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Snapshot_RadiusChange_NotEqual()
    {
        var a = Snap(maxRadius: 30);
        var b = Snap(maxRadius: 50);
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task PeakRadius_Renders_WhenSet()
    {
        // Helm sees the peak observed distance during this anchor
        // watch alongside the live "Dist" value -- "we drifted to N m
        // at the worst" without watching the live value tick.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(peakRadius: 24.5)));
        await Assert.That(cut.Markup).Contains("Peak");
    }

    [Test]
    public async Task PeakRadius_HiddenWhenNull()
    {
        // Manual anchors don't track peak (no plugin, no
        // currentRadius). The card should omit the row entirely
        // rather than render an empty placeholder.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(peakRadius: null)));
        await Assert.That(cut.Markup).DoesNotContain("Peak");
    }

    [Test]
    public async Task Snapshot_PeakRadiusChange_NotEqual()
    {
        // Snapshot equality drives Blazor's render-skip optimisation;
        // a drift to a new peak must trigger a re-render.
        var a = Snap(peakRadius: 18);
        var b = Snap(peakRadius: 22);
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task ChipClick_Fires_OnSetRadius_WithValue()
    {
        using var ctx = new Bunit.TestContext();
        double? gotRadius = null;
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(manual: true, currentRadius: null, maxRadius: null))
            .Add(x => x.OnSetRadius, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<double>(this, r => gotRadius = r)));

        // First chip in the preset list is 20. Clicking it reports 20 to
        // the consumer.
        cut.FindAll(".anchor-radius-chips .map-btn")[0].Click();
        await Assert.That(gotRadius).IsEqualTo(20);
    }

    [Test]
    public async Task Bearing_HiddenWhenNull()
    {
        // No bearing data (manual flow, or first delta after drop
        // hasn't arrived). The needle row should NOT render so the
        // card height stays unchanged.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: null)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bearing_RendersDegrees_FromRadians()
    {
        // Plugin publishes radians, the helm reads degrees. Half-PI =
        // 90deg = due east. Pin the conversion so a refactor that
        // changes the snapshot to degrees-already-converted (or back
        // to radians) is a deliberate move, not a silent drift.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: Math.PI / 2)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(1);
        await Assert.That(cut.Find(".anchor-bearing-value").TextContent).Contains("90");
    }

    [Test]
    public async Task Bearing_NormalisesNegativeRadiansToPositiveDegrees()
    {
        // Plugin spec says 0..2pi, but a careless impl could publish
        // -PI/2 for due-west; defend against that by normalising into
        // [0, 360). -PI/2 should render as 270deg, not "-90".
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: -Math.PI / 2)));
        var text = cut.Find(".anchor-bearing-value").TextContent;
        await Assert.That(text).Contains("270");
        await Assert.That(text).DoesNotContain("-");
    }

    [Test]
    public async Task Bearing_NaN_HidesNeedle()
    {
        // The previous impl used a `while (deg < 0) deg += 360;` loop;
        // a NaN delta from a misbehaving plugin would fall through with
        // deg = int.MinValue (NaN -> int = 0 in .NET, but the wider
        // risk is the loop). Pin: NaN must hide the needle entirely
        // rather than render an invalid angle.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: double.NaN)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bearing_PositiveInfinity_HidesNeedle()
    {
        // Critical regression target: with the old `while` loop
        // implementation, +Infinity would cast to int.MaxValue and
        // the loop would run ~6M iterations PER RENDER, freezing the
        // HUD. With the modulo-based fix, the helper returns null and
        // the needle is hidden.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: double.PositiveInfinity)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bearing_NegativeInfinity_HidesNeedle()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: double.NegativeInfinity)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bearing_VeryLargeFiniteRadians_NormalisesQuickly()
    {
        // 100 * pi = 50 full rotations + 0. The modulo idiom must
        // handle this in O(1), not O(n) like the previous while-loop.
        // Same expected output as bearingTrue=0: 0 deg displayed.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: 100.0 * Math.PI)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(1);
        await Assert.That(cut.Find(".anchor-bearing-value").TextContent).Contains("0");
    }

    [Test]
    public async Task Snapshot_BearingChange_NotEqual()
    {
        // Bearing ticks as the boat swings; equality must catch the
        // delta so the needle re-renders to the new angle.
        var a = Snap(bearingTrue: 1.0);
        var b = Snap(bearingTrue: 1.5);
        await Assert.That(a).IsNotEqualTo(b);
    }

    // ---- v2.0.0+ extras: Bow + Rode rows ----

    [Test]
    public async Task DistanceFromBow_RendersBowRow_WhenSet()
    {
        // Plugin v2.0.0+ publishes navigation.anchor.distanceFromBow
        // (bow-corrected distance, distinct from CurrentRadius which
        // is GPS-to-anchor). The HUD card surfaces it as a "Bow" row
        // so the helm reads "anchor 18 m back" at a glance.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(distanceFromBow: 18.0)));
        await Assert.That(cut.Markup).Contains("Bow");
    }

    [Test]
    public async Task DistanceFromBow_HiddenWhenNull()
    {
        // Manual flow + plugin v1.x don't publish this path; the
        // row should omit entirely so the card stays compact.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(distanceFromBow: null)));
        await Assert.That(cut.Markup).DoesNotContain(">Bow<");
    }

    [Test]
    public async Task RodeLength_RendersRodeRow_WhenSet()
    {
        // Helm-confirmed rode length from the plugin's Set Rode
        // Length flow (or the rode counter sensor when wired). Useful
        // for the log + scope-ratio cross-check.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(rodeLength: 60.0)));
        await Assert.That(cut.Markup).Contains("Rode");
    }

    [Test]
    public async Task RodeLength_HiddenWhenNull()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(rodeLength: null)));
        await Assert.That(cut.Markup).DoesNotContain(">Rode<");
    }

    [Test]
    public async Task Snapshot_DistanceFromBowChange_NotEqual()
    {
        // Snapshot equality drives Blazor's render-skip optimisation;
        // a delta on Bow must trigger a re-render.
        var a = Snap(distanceFromBow: 18);
        var b = Snap(distanceFromBow: 22);
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task Snapshot_RodeLengthChange_NotEqual()
    {
        var a = Snap(rodeLength: 50);
        var b = Snap(rodeLength: 60);
        await Assert.That(a).IsNotEqualTo(b);
    }
}
