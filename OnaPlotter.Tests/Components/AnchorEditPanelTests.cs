using Bunit;
using OnaPlotter.Components.Map;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for the v2.0.0+ two-state anchor panel.
///
/// State A (Drop): no anchor active; only a Drop button + Cancel.
/// Tapping Drop fires <c>OnDrop</c> (no radius arg -- the plugin's
/// step 2 carries that).
///
/// State B (SetRadius): position pinned server-side; the helm needs
/// to set the radius. "Auto" chip + numeric chips visible. "Auto"
/// commits via <c>OnAutoSetRadius</c>; numeric chip + Set commits via
/// <c>OnSetRadius(int)</c>. Cancel just closes the panel; the
/// dropped pin stays armed server-side.
///
/// Pinning the contracts here so a future refactor that re-couples
/// the two states or merges the buttons breaks the test, not the
/// helm trying to anchor at sundown.
/// </summary>
public class AnchorEditPanelTests
{
    private static IRenderedComponent<AnchorEditPanel> RenderDrop(
        Bunit.TestContext ctx,
        bool busy = false,
        Action? onDrop = null,
        Action? onCancel = null,
        string suggestion = "")
    {
        return ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.Drop)
            .Add(x => x.InitialRadiusMeters, 0)
            .Add(x => x.Busy, busy)
            .Add(x => x.SuggestionLabel, suggestion)
            .Add(x => x.OnDrop, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(p, () => onDrop?.Invoke()))
            .Add(x => x.OnCancel, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(p, () => onCancel?.Invoke())));
    }

    private static IRenderedComponent<AnchorEditPanel> RenderSetRadius(
        Bunit.TestContext ctx,
        int initial = 30,
        bool busy = false,
        Action<int>? onSetRadius = null,
        Action? onAutoSetRadius = null,
        Action? onCancel = null,
        string suggestion = "")
    {
        return ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, initial)
            .Add(x => x.Busy, busy)
            .Add(x => x.SuggestionLabel, suggestion)
            .Add(x => x.OnSetRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(p, r => onSetRadius?.Invoke(r)))
            .Add(x => x.OnAutoSetRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(p, () => onAutoSetRadius?.Invoke()))
            .Add(x => x.OnCancel, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(p, () => onCancel?.Invoke())));
    }

    // ---- Drop mode ----

    [Test]
    public async Task DropMode_Renders_DropButton_NoChips()
    {
        // Drop mode is the radius-deferred state -- the helm hasn't
        // backed down yet; no point picking a radius. Pin: Drop button
        // present, no chip-row.
        using var ctx = new Bunit.TestContext();
        var cut = RenderDrop(ctx);

        await Assert.That(cut.FindAll(".anchor-edit-drop").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-chips").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".anchor-edit-set").Count).IsEqualTo(0);
    }

    [Test]
    public async Task DropButton_FiresOnDrop_NoArguments()
    {
        // Two-step contract: Drop fires the no-arg callback. The
        // radius is set in step 2 via SetRadius mode.
        using var ctx = new Bunit.TestContext();
        bool fired = false;
        var cut = RenderDrop(ctx, onDrop: () => fired = true);

        cut.Find(".anchor-edit-drop").Click();
        await Assert.That(fired).IsTrue();
    }

    [Test]
    public async Task DropMode_BusyDisablesButtons()
    {
        // While the PUT round-trip is in flight, both buttons disable
        // so a double-tap doesn't fire two PUTs.
        using var ctx = new Bunit.TestContext();
        var cut = RenderDrop(ctx, busy: true);

        var drop = cut.Find(".anchor-edit-drop");
        await Assert.That(drop.HasAttribute("disabled")).IsTrue();
        await Assert.That(drop.TextContent.Trim()).IsEqualTo("Dropping...");

        var cancel = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Cancel");
        await Assert.That(cancel.HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task DropMode_BusyGate_BlocksOnDropFiring()
    {
        // bUnit's .Click() ignores disabled, so the in-handler
        // `if (Busy) return;` gate is the real defence.
        using var ctx = new Bunit.TestContext();
        bool fired = false;
        var cut = RenderDrop(ctx, busy: true, onDrop: () => fired = true);

        cut.Find(".anchor-edit-drop").Click();
        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task DropMode_Cancel_FiresOnCancel()
    {
        using var ctx = new Bunit.TestContext();
        bool fired = false;
        var cut = RenderDrop(ctx, onCancel: () => fired = true);

        cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Cancel").Click();
        await Assert.That(fired).IsTrue();
    }

    [Test]
    public async Task DropMode_TitleReadsDropAnchor()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderDrop(ctx);
        await Assert.That(cut.Find(".anchor-edit-title").TextContent).IsEqualTo("Drop anchor");
    }

    // ---- SetRadius mode ----

    [Test]
    public async Task SetRadiusMode_Renders_AutoChip_NumericChips_SetButton()
    {
        // Pin the chip set: Auto + every preset, plus a Set button.
        // No Drop button (we're past step 1).
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx);

        await Assert.That(cut.FindAll(".anchor-edit-auto").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-set").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-drop").Count).IsEqualTo(0);

        // 6 numeric chips (20/30/50/75/100/150) + 1 Auto = 7 inside the chip row
        var chips = cut.FindAll(".anchor-edit-chips .map-btn");
        await Assert.That(chips.Count).IsEqualTo(7);
    }

    [Test]
    public async Task SetRadiusMode_InitialRadius_LandsOnPresetChip()
    {
        // Heuristic suggested 30 -> 30 chip is active. Auto is NOT
        // active (a numeric is picked).
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("30");
    }

    [Test]
    public async Task SetRadiusMode_InitialBetweenPresets_SnapsUp()
    {
        // 65 m -> 75 m chip (round UP keeps scope >= 5:1).
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 65);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("75");
    }

    [Test]
    public async Task SetRadiusMode_AutoChip_TapsSwitchToAuto()
    {
        // Helm taps Auto -> active class moves to Auto, no numeric is
        // active. Tapping Set then fires OnAutoSetRadius (not
        // OnSetRadius).
        using var ctx = new Bunit.TestContext();
        bool autoFired = false;
        bool numericFired = false;
        var cut = RenderSetRadius(ctx, initial: 30,
            onSetRadius: _ => numericFired = true,
            onAutoSetRadius: () => autoFired = true);

        cut.Find(".anchor-edit-auto").Click();
        cut.Find(".anchor-edit-set").Click();

        await Assert.That(autoFired).IsTrue();
        await Assert.That(numericFired).IsFalse();
    }

    [Test]
    public async Task SetRadiusMode_NumericChip_FiresOnSetRadius_WithCurrentValue()
    {
        // Initial 30 -> helm taps 75 -> Set fires OnSetRadius(75).
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = RenderSetRadius(ctx, initial: 30, onSetRadius: r => captured = r);

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75")).Click();
        cut.Find(".anchor-edit-set").Click();

        await Assert.That(captured).IsEqualTo(75);
    }

    [Test]
    public async Task SetRadiusMode_NumericChip_DoesNotFireOnAutoSet()
    {
        // Numeric Set route MUST NOT fire OnAutoSetRadius even though
        // both come through OnSetClicked() -- the picked-vs-Auto
        // discriminator decides.
        using var ctx = new Bunit.TestContext();
        bool autoFired = false;
        var cut = RenderSetRadius(ctx, initial: 30, onAutoSetRadius: () => autoFired = true);

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("100")).Click();
        cut.Find(".anchor-edit-set").Click();

        await Assert.That(autoFired).IsFalse();
    }

    [Test]
    public async Task SetRadiusMode_AutoToNumeric_LastWins()
    {
        // Helm taps Auto, then 50 -> Set fires OnSetRadius(50). The
        // active chip should be 50, not Auto.
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = RenderSetRadius(ctx, initial: 30, onSetRadius: r => captured = r);

        cut.Find(".anchor-edit-auto").Click();
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("50")).Click();
        cut.Find(".anchor-edit-set").Click();

        await Assert.That(captured).IsEqualTo(50);
    }

    [Test]
    public async Task SetRadiusMode_Busy_DisablesEverything()
    {
        // While the PUT is in flight: Set disabled, every chip
        // disabled, Cancel disabled.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, busy: true);

        await Assert.That(cut.Find(".anchor-edit-set").HasAttribute("disabled")).IsTrue();
        var chips = cut.FindAll(".anchor-edit-chips .map-btn");
        foreach (var chip in chips)
        {
            await Assert.That(chip.HasAttribute("disabled")).IsTrue();
        }
        var cancel = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Cancel");
        await Assert.That(cancel.HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task SetRadiusMode_TitleReadsSetAlarmRadius()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx);
        await Assert.That(cut.Find(".anchor-edit-title").TextContent).IsEqualTo("Set alarm radius");
    }

    [Test]
    public async Task SuggestionLabel_RendersWhenProvided()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, suggestion: "5x 6.0 m depth");
        await Assert.That(cut.FindAll(".anchor-edit-sub").Count).IsEqualTo(1);
        await Assert.That(cut.Find(".anchor-edit-sub").TextContent).IsEqualTo("5x 6.0 m depth");
    }

    [Test]
    public async Task SuggestionLabel_Empty_HidesSubline()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, suggestion: "");
        await Assert.That(cut.FindAll(".anchor-edit-sub").Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetRadiusMode_ParentReRender_PreservesUserChipChoice()
    {
        // Helm taps 75-chip; parent re-renders with the SAME
        // InitialRadiusMeters. Set must fire 75 (helm's choice),
        // NOT 30 (initial). Without the snap-only-on-initial-change
        // guard the panel re-snaps every parameter set and silently
        // overwrites the chip choice.
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, 30)
            .Add(x => x.OnSetRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(p, r => captured = r)));

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75")).Click();

        cut.SetParametersAndRender(p => p.Add(x => x.InitialRadiusMeters, 30));

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("75");

        cut.Find(".anchor-edit-set").Click();
        await Assert.That(captured).IsEqualTo(75);
    }

    [Test]
    public async Task SetRadiusMode_ParentReSeedsWithNewInitial_Resnaps()
    {
        // New InitialRadiusMeters from the parent (different anchorage,
        // different depth) MUST re-seed the chip pick. This is how the
        // parent pushes a new heuristic suggestion.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, 30));

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75")).Click();

        cut.SetParametersAndRender(p => p.Add(x => x.InitialRadiusMeters, 100));

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("100");
    }

    // ---- Mode switching ----

    [Test]
    public async Task ModeSwitch_DropToSetRadius_RendersChipRow()
    {
        // Parent flips Mode after a successful Drop -> the panel
        // re-renders with chip row visible.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.Drop)
            .Add(x => x.InitialRadiusMeters, 30));

        await Assert.That(cut.FindAll(".anchor-edit-chips").Count).IsEqualTo(0);

        cut.SetParametersAndRender(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius));

        await Assert.That(cut.FindAll(".anchor-edit-chips").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-set").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-drop").Count).IsEqualTo(0);
    }
}
