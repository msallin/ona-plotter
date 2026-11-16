using Bunit;
using OnaPlotter.Components.Map.Hud;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// Component tests for HudAnchorCard. Pin the snapshot-equality
/// contract (drives Blazor's render-skip optimisation), the render
/// branches for distance / radius / peak / bearing, and the
/// tap-to-adjust gesture wired through OnAdjust. The anchor plugin
/// (v2.0.0+) is the only supported source, so the card has no
/// manual-mode chip row.
/// </summary>
public class HudAnchorCardTests
{
    private static HudAnchorCard.AnchorHudSnapshot Snap(
        bool visible = true,
        bool dragging = false,
        double? currentRadius = 12,
        double? maxRadius = 30,
        string? dormantReason = null,
        double? peakRadius = null,
        double? bearingTrue = null) =>
        new(visible, dragging, currentRadius, maxRadius,
            dormantReason, peakRadius, bearingTrue);

    [Test]
    public async Task NotVisible_RendersNothing()
    {
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(visible: false)));
        await Assert.That(cut.FindAll(".anchor-panel").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Visible_ShowsDistAndRadius()
    {
        // Plugin feeds maxRadius + currentRadius; card renders both.
        // Card visibility is parent-gated on AnchorActive AND
        // MaxRadius set, so we don't need to pin "no chips when
        // server owns the radius" - there are no chips at all.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap()));
        await Assert.That(cut.Markup).Contains("Dist");
        await Assert.That(cut.Markup).Contains("Radius");
    }

    [Test]
    public async Task Dragging_AddsAnchorAlarmClass()
    {
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(dragging: true)));
        await Assert.That(cut.Find(".anchor-panel").ClassList.Contains("anchor-alarm")).IsTrue();
    }

    [Test]
    public async Task DormantReason_RendersHintWithWarningGlyph()
    {
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
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
        // watch alongside the live "Dist" value - "we drifted to N m
        // at the worst" without watching the live value tick.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(peakRadius: 24.5)));
        await Assert.That(cut.Markup).Contains("Peak");
    }

    [Test]
    public async Task PeakRadius_HiddenWhenNull()
    {
        // Plugin hasn't published a peak yet (just-armed, or older
        // plugin version). The card should omit the row entirely
        // rather than render an empty placeholder.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
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

    // ---- Tap-to-adjust ----

    [Test]
    public async Task OnAdjust_NotWired_CardIsNotInteractive()
    {
        // No OnAdjust -> the card has no role=button, no clickable
        // class. Card stays read-only, which is fine: the helm can
        // still raise via the Anchor button on the bottom bar.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap()));
        var card = cut.Find(".anchor-panel");
        await Assert.That(card.GetAttribute("role")).IsNull();
        await Assert.That(card.ClassList.Contains("anchor-panel-tappable")).IsFalse();
    }

    [Test]
    public async Task OnAdjust_Wired_CardIsTappableAndFires()
    {
        // OnAdjust wired -> the whole card becomes a button: role,
        // tappable class, click fires the callback. This is the
        // dedicated adjust gesture (the bottom-bar Anchor button is
        // the dedicated raise gesture, kept separate so the helm
        // doesn't risk a misclick raising the anchor when they
        // meant to nudge the radius).
        using var ctx = new Bunit.BunitContext();
        bool fired = false;
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap())
            .Add(x => x.OnAdjust, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(this, () => fired = true)));
        var card = cut.Find(".anchor-panel");
        await Assert.That(card.GetAttribute("role")).IsEqualTo("button");
        await Assert.That(card.ClassList.Contains("anchor-panel-tappable")).IsTrue();

        card.Click();
        await Assert.That(fired).IsTrue();
    }

    // ---- Bearing ----

    [Test]
    public async Task Bearing_HiddenWhenNull()
    {
        // No bearing data (older plugin, or first delta after drop
        // hasn't arrived). The needle row should NOT render so the
        // card height stays unchanged.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
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
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
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
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: -Math.PI / 2)));
        var text = cut.Find(".anchor-bearing-value").TextContent;
        await Assert.That(text).Contains("270");
        await Assert.That(text).DoesNotContain("-");
    }

    [Test]
    public async Task Bearing_NaN_HidesNeedle()
    {
        // A NaN delta from a misbehaving plugin must hide the needle
        // entirely rather than render an invalid angle.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: double.NaN)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bearing_PositiveInfinity_HidesNeedle()
    {
        // Guards against a HUD freeze: with a `while (deg < 0) deg +=
        // 360` style normaliser, +Infinity casts to int.MaxValue and
        // the loop runs ~6M iterations PER RENDER. The helper must
        // return null and hide the needle.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: double.PositiveInfinity)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bearing_NegativeInfinity_HidesNeedle()
    {
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
            .Add(x => x.Snapshot, Snap(bearingTrue: double.NegativeInfinity)));
        await Assert.That(cut.FindAll(".anchor-bearing").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bearing_VeryLargeFiniteRadians_NormalisesQuickly()
    {
        // 100 * pi = 50 full rotations + 0. The modulo idiom must
        // handle this in O(1), not O(n) like a while-loop would.
        // Same expected output as bearingTrue=0: 0 deg displayed.
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<HudAnchorCard>(p => p
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
}
