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
        string suggestion = "",
        int? autoPreview = null)
    {
        return ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, initial)
            .Add(x => x.Busy, busy)
            .Add(x => x.SuggestionLabel, suggestion)
            .Add(x => x.AutoPreviewRadius, autoPreview)
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

        // The escape button is labelled "Close" not "Cancel" -- field
        // study showed both sailors read "Cancel" as "abort the
        // anchor", but the dropped pin actually stays armed
        // server-side. "Close" matches what the action does.
        var close = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Close");
        await Assert.That(close.HasAttribute("disabled")).IsTrue();
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
    public async Task DropMode_Close_FiresOnCancel()
    {
        // Button is labelled "Close" but still wired to OnCancel
        // (the parent callback name is unchanged; only the visible
        // label flipped to match what the button actually does).
        using var ctx = new Bunit.TestContext();
        bool fired = false;
        var cut = RenderDrop(ctx, onCancel: () => fired = true);

        cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Close").Click();
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
    public async Task SetRadiusMode_Renders_AutoChip_NumericChips_NoSetButton()
    {
        // Live-commit UX: chip taps fire the commit immediately;
        // there is no separate Set button. Pin the chip set + button
        // absence so a regression that re-introduces a Set button
        // doesn't slip past.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx);

        await Assert.That(cut.FindAll(".anchor-edit-auto").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-set").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".anchor-edit-drop").Count).IsEqualTo(0);

        // 5 numeric chips (20/30/50/75/100) + 1 Auto = 6 inside the chip row.
        var chips = cut.FindAll(".anchor-edit-chips .map-btn");
        await Assert.That(chips.Count).IsEqualTo(6);
    }

    [Test]
    public async Task SetRadiusMode_InitialRadius_LandsOnPresetChip()
    {
        // Initial=30 -> 30 chip is active (derived from
        // InitialRadiusMeters, not panel-internal pick state).
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
    public async Task SetRadiusMode_AutoChip_TapFiresOnAutoSetImmediately()
    {
        // Live-commit: tapping Auto fires OnAutoSetRadius right away,
        // no separate Set tap needed. OnSetRadius (numeric) must NOT
        // fire on the same tap.
        using var ctx = new Bunit.TestContext();
        bool autoFired = false;
        bool numericFired = false;
        var cut = RenderSetRadius(ctx, initial: 30,
            onSetRadius: _ => numericFired = true,
            onAutoSetRadius: () => autoFired = true);

        cut.Find(".anchor-edit-auto").Click();

        await Assert.That(autoFired).IsTrue();
        await Assert.That(numericFired).IsFalse();
    }

    [Test]
    public async Task SetRadiusMode_NumericChip_TapFiresOnSetRadiusImmediately()
    {
        // Live-commit: tapping 75 chip fires OnSetRadius(75) right
        // away. Helm doesn't have to chase a Set button afterward.
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = RenderSetRadius(ctx, initial: 30, onSetRadius: r => captured = r);

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();

        await Assert.That(captured).IsEqualTo(75);
    }

    [Test]
    public async Task SetRadiusMode_NumericChip_DoesNotFireOnAutoSet()
    {
        // Tapping a numeric chip routes through OnSetRadius only --
        // OnAutoSetRadius stays silent. Pinning so a future refactor
        // that conflates the two callbacks fails here.
        using var ctx = new Bunit.TestContext();
        bool autoFired = false;
        var cut = RenderSetRadius(ctx, initial: 30, onAutoSetRadius: () => autoFired = true);

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("100") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();

        await Assert.That(autoFired).IsFalse();
    }

    [Test]
    public async Task SetRadiusMode_TwoNumericChipsTapped_BothCommit()
    {
        // Helm picks 50, sees alarm circle resize, decides to go
        // bigger, picks 75. Both taps must commit (each fires the
        // callback in order). Replaces the previous "AutoToNumeric
        // LastWins" semantics -- the panel doesn't accumulate pick
        // state any more; each tap is its own commit.
        using var ctx = new Bunit.TestContext();
        var captured = new List<int>();
        var cut = RenderSetRadius(ctx, initial: 30, onSetRadius: r => captured.Add(r));

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("50") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();

        await Assert.That(captured.Count).IsEqualTo(2);
        await Assert.That(captured[0]).IsEqualTo(50);
        await Assert.That(captured[1]).IsEqualTo(75);
    }

    [Test]
    public async Task SetRadiusMode_AutoTwiceInARow_RecomputesOnEachTap()
    {
        // Helm taps Auto, boat drifts further out, helm taps Auto
        // again to recompute. Each tap must fire OnAutoSetRadius.
        // Plugin uses fresh distance on each call, so two POSTs is
        // the deliberate "extend on drift" gesture.
        using var ctx = new Bunit.TestContext();
        int autoFires = 0;
        var cut = RenderSetRadius(ctx, initial: 30, onAutoSetRadius: () => autoFires++);

        cut.Find(".anchor-edit-auto").Click();
        cut.Find(".anchor-edit-auto").Click();

        await Assert.That(autoFires).IsEqualTo(2);
    }

    [Test]
    public async Task SetRadiusMode_Busy_DisablesEverything()
    {
        // While a PUT is in flight: every chip disabled + Close
        // disabled. Set button no longer exists in the live-commit
        // UX so we don't check for it.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, busy: true);

        var chips = cut.FindAll(".anchor-edit-chips .map-btn");
        foreach (var chip in chips)
        {
            await Assert.That(chip.HasAttribute("disabled")).IsTrue();
        }
        var close = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Close");
        await Assert.That(close.HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task SetRadiusMode_Busy_BlocksChipTaps()
    {
        // bUnit's .Click() ignores disabled, so the in-handler
        // `if (Busy) return;` gate is the real defence.
        using var ctx = new Bunit.TestContext();
        bool numericFired = false;
        bool autoFired = false;
        var cut = RenderSetRadius(ctx, initial: 30, busy: true,
            onSetRadius: _ => numericFired = true,
            onAutoSetRadius: () => autoFired = true);

        cut.Find(".anchor-edit-auto").Click();
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();

        await Assert.That(numericFired).IsFalse();
        await Assert.That(autoFired).IsFalse();
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
    public async Task SetRadiusMode_ActiveChip_TracksInitialRadiusMeters()
    {
        // Live-commit UX: the active chip is DERIVED from
        // InitialRadiusMeters (= the parent's view of the current
        // armed radius), not from internal pick state. So when the
        // server delta echoes back 75 m, the parent re-renders with
        // InitialRadiusMeters=75 and the 75 chip lights up. This
        // pin guards against a refactor that re-introduces panel-
        // internal pick state and the active chip drifts out of
        // sync with reality.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, 30));

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("30");

        // Server delta arrives, parent passes the new radius down.
        cut.SetParametersAndRender(p => p.Add(x => x.InitialRadiusMeters, 75));

        actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("75");
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
        // No Set button in the live-commit UX (chip taps commit
        // immediately) and no Drop button (we're past step 1).
        await Assert.That(cut.FindAll(".anchor-edit-set").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".anchor-edit-drop").Count).IsEqualTo(0);
    }

    // ---- Auto preview ----

    [Test]
    public async Task SetRadiusMode_AutoChip_RendersPlainAuto_WhenNoPreview()
    {
        // No AutoPreviewRadius -> chip face reads "Auto" alone.
        // Field-study finding: helm wants to see the number before
        // committing; the unset state is acceptable for "still
        // loading", not the steady state.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30);

        await Assert.That(cut.Find(".anchor-edit-auto").TextContent.Trim())
            .IsEqualTo("Auto");
    }

    [Test]
    public async Task SetRadiusMode_AutoChip_RendersPreviewedRadius_WhenSet()
    {
        // AutoPreviewRadius=47 -> chip face reads "Auto: 47m". Helm
        // sees the number before tapping Set.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, 30)
            .Add(x => x.AutoPreviewRadius, 47));

        var auto = cut.Find(".anchor-edit-auto");
        await Assert.That(auto.TextContent).Contains("Auto:");
        await Assert.That(auto.TextContent).Contains("47");
    }

    [Test]
    public async Task SetRadiusMode_AutoChip_TooltipReflectsPreview()
    {
        // The tooltip (long-press / hover surface) carries the same
        // information as the chip face. Without it, a helm long-
        // pressing Auto on iPad would see the generic "plugin
        // computes" tooltip and miss the actual number.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, 30)
            .Add(x => x.AutoPreviewRadius, 47));

        var auto = cut.Find(".anchor-edit-auto");
        await Assert.That(auto.GetAttribute("title")!).Contains("47");
    }

    [Test]
    public async Task SetRadiusMode_AutoChip_LivePreviewUpdatesOnReRender()
    {
        // Boat drifts; parent recomputes preview on each render.
        // The chip must reflect the latest value without losing the
        // helm's chip pick (Auto is implicit-active when picked is
        // null; the preview number is just label decoration).
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, 30)
            .Add(x => x.AutoPreviewRadius, 40));

        await Assert.That(cut.Find(".anchor-edit-auto").TextContent).Contains("40");

        cut.SetParametersAndRender(p => p.Add(x => x.AutoPreviewRadius, 55));

        await Assert.That(cut.Find(".anchor-edit-auto").TextContent).Contains("55");
        await Assert.That(cut.Find(".anchor-edit-auto").TextContent).DoesNotContain("40");
    }

    // ---- UnseededInitialRadius sentinel ----

    [Test]
    public async Task SetRadiusMode_UnseededInitial_NoNumericChipActive()
    {
        // Parent passes UnseededInitialRadius -> picked stays null,
        // Auto chip becomes the implicit active (no numeric chip
        // gets a synthetic ".active"). This is the Drop-mode panel
        // pre-fill behaviour for when there's no depth source.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: AnchorEditPanel.UnseededInitialRadius);

        // No numeric chip should be active; Auto IS active.
        var numericActives = cut.FindAll(".anchor-edit-chips .map-btn.active")
            .Where(b => !b.ClassList.Contains("anchor-edit-auto")).ToList();
        await Assert.That(numericActives.Count).IsEqualTo(0);
        await Assert.That(cut.Find(".anchor-edit-auto").ClassList.Contains("active")).IsTrue();
    }

    [Test]
    public async Task SetRadiusMode_UnseededThenNumericTap_FiresOnSetRadius()
    {
        // Helm opens panel with no opinion (Auto active by default),
        // taps 50 chip -> fires OnSetRadius(50) immediately. Auto
        // callback stays silent.
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        bool autoFired = false;
        var cut = RenderSetRadius(ctx,
            initial: AnchorEditPanel.UnseededInitialRadius,
            onSetRadius: r => captured = r,
            onAutoSetRadius: () => autoFired = true);

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("50") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();

        await Assert.That(captured).IsEqualTo(50);
        await Assert.That(autoFired).IsFalse();
    }
}
