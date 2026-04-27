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
        string? dormantReason = null) =>
        new(visible, dragging, manual, manualRadius, currentRadius, maxRadius, dormantReason);

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
}
