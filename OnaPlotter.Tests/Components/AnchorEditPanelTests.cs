using Bunit;
using OnaPlotter.Components.Map;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for the anchor-drop panel. Pin three contracts the
/// helm experience depends on:
///   - the initial radius lands on a preset chip (snap-down) so the
///     chip row never renders with no `.active`
///   - tapping a chip changes the active chip but does NOT fire
///     OnDrop until the explicit Drop button is tapped (avoids the
///     "I just wanted to look" misfire)
///   - Drop fires OnDrop with the *currently selected* radius, not
///     InitialRadiusMeters -- regression safety against a stale
///     binding refactor that re-reads the parameter at click time
///   - Dropping=true disables both buttons (no double-tap drop)
/// </summary>
public class AnchorEditPanelTests
{
    private static IRenderedComponent<AnchorEditPanel> Render(
        Bunit.TestContext ctx,
        int initial = 30,
        bool dropping = false,
        Action<int>? onDrop = null,
        Action? onCancel = null,
        string suggestion = "")
    {
        return ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.InitialRadiusMeters, initial)
            .Add(x => x.Dropping, dropping)
            .Add(x => x.SuggestionLabel, suggestion)
            .Add(x => x.OnDrop, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(p, r => onDrop?.Invoke(r)))
            .Add(x => x.OnCancel, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(p, () => onCancel?.Invoke())));
    }

    [Test]
    public async Task Initial_30_LandsOn30Chip()
    {
        // Realistic case: heuristic suggests 30 m. The chip row's 30 m
        // preset becomes active; no other chip is.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 30);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("30");
    }

    [Test]
    public async Task Initial_BetweenPresets_SnapsUp()
    {
        // Heuristic produced 65 m (e.g. 13 m depth * 5). The chip row
        // doesn't have 65 m, so we snap UP to the next preset (75 m).
        // Rounding UP preserves the 5:1 scope the heuristic asked for;
        // rounding DOWN would land at 50 m = 3.85:1, below cruising-
        // rule-of-thumb minimum, with no signal to the helm.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 65);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("75");
    }

    [Test]
    public async Task Initial_BelowSmallestPreset_LandsOnSmallest()
    {
        // Defensive: parent passes 10 m (below the 20 m floor). Don't
        // render with no active chip; pick the smallest.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 10);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("20");
    }

    [Test]
    public async Task Initial_ExactlyOnPreset_LandsOnIt()
    {
        // Round-up is "smallest preset >= radius"; for radius equal to
        // a preset, it MUST stay on that preset (not jump up to the
        // next). Boundary case worth pinning.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 30);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("30");
    }

    [Test]
    public async Task Initial_AboveLargestPreset_LandsOnLargest()
    {
        // Beyond the heuristic's MaxSuggestedMeters ceiling (200 m) is
        // only possible if a caller bypasses Suggest(). We cap at the
        // largest preset (150 m) rather than render with no active
        // chip; helm can still pick the same in Settings.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 250);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("150");
    }

    [Test]
    public async Task TappingChip_ChangesActive_DoesNotFireOnDrop()
    {
        // The chip row is for picking, not committing. A tap should
        // move the active marker but NOT fire OnDrop -- otherwise
        // the helm "just looking" at radius options would silently
        // anchor.
        using var ctx = new Bunit.TestContext();
        bool fired = false;
        var cut = Render(ctx, initial: 30, onDrop: _ => fired = true);

        // Find the 100 m chip and click.
        var hundredChip = cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("100"));
        hundredChip.Click();

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("100");
        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task DropButton_FiresOnDrop_WithCurrentRadius()
    {
        // Initial = 30, helm taps 75 chip, then Drop. Callback should
        // get 75 (the helm's choice), not 30 (the initial).
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = Render(ctx, initial: 30, onDrop: r => captured = r);

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75")).Click();
        cut.Find(".anchor-edit-drop").Click();

        await Assert.That(captured).IsEqualTo(75);
    }

    [Test]
    public async Task DropButton_WithoutChipChange_FiresInitialRadius()
    {
        // Common path: heuristic was right, helm doesn't touch chips,
        // taps Drop. Callback fires with the initial value.
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = Render(ctx, initial: 50, onDrop: r => captured = r);

        cut.Find(".anchor-edit-drop").Click();
        await Assert.That(captured).IsEqualTo(50);
    }

    [Test]
    public async Task Dropping_DisablesBothButtons()
    {
        // While the REST drop is in flight, the buttons must be
        // disabled. A Cancel mid-flight doesn't roll the server
        // state back; better to lock the panel until OnDrop's caller
        // returns.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 30, dropping: true);

        var drop = cut.Find(".anchor-edit-drop");
        await Assert.That(drop.HasAttribute("disabled")).IsTrue();
        await Assert.That(drop.TextContent.Trim()).IsEqualTo("Dropping...");

        var cancel = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Cancel");
        await Assert.That(cancel.HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task CancelButton_FiresOnCancel_DoesNotFireOnDrop()
    {
        using var ctx = new Bunit.TestContext();
        bool dropFired = false;
        bool cancelFired = false;
        var cut = Render(ctx, initial: 30,
            onDrop: _ => dropFired = true,
            onCancel: () => cancelFired = true);

        var cancel = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Cancel");
        cancel.Click();

        await Assert.That(cancelFired).IsTrue();
        await Assert.That(dropFired).IsFalse();
    }

    [Test]
    public async Task SuggestionLabel_Renders_When_Provided()
    {
        // The eyebrow tells the helm where the pre-fill came from.
        // Use the production format string verbatim so a refactor
        // that changes "5x" -> "5×" (Unicode) breaks here.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 30, suggestion: "5x 6.0 m depth");
        await Assert.That(cut.FindAll(".anchor-edit-sub").Count).IsEqualTo(1);
        await Assert.That(cut.Find(".anchor-edit-sub").TextContent).IsEqualTo("5x 6.0 m depth");
    }

    [Test]
    public async Task SuggestionLabel_Empty_HidesSublineEntirely()
    {
        // Per the documented contract: empty SuggestionLabel must NOT
        // leave an empty <span class="anchor-edit-sub"> in the DOM
        // (which would still consume vertical space + line-height).
        // Pin the absence so a future "always render the span" refactor
        // surfaces.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 30, suggestion: "");
        await Assert.That(cut.FindAll(".anchor-edit-sub").Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParentReRender_PreservesUserChipChoice()
    {
        // Helm taps 75-chip; parent re-renders with the SAME
        // InitialRadiusMeters (e.g. a toast tick triggered StateHasChanged).
        // Drop must fire 75 (helm's choice), NOT 30 (initial).
        // Without the snap-only-on-initial-change guard the panel
        // re-snaps every parameter set and silently overwrites the
        // chip choice. Live regression risk.
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.InitialRadiusMeters, 30)
            .Add(x => x.OnDrop, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(p, r => captured = r)));

        // Helm taps 75-chip.
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75")).Click();

        // Parent re-renders the panel with the SAME initial value.
        cut.SetParametersAndRender(p => p.Add(x => x.InitialRadiusMeters, 30));

        // The 75 chip must still be active.
        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("75");

        // And tapping Drop must report 75, not 30.
        cut.Find(".anchor-edit-drop").Click();
        await Assert.That(captured).IsEqualTo(75);
    }

    [Test]
    public async Task ParentReSeedsWithNewInitial_Resnaps()
    {
        // Inverse contract: when the parent passes a NEW
        // InitialRadiusMeters (e.g. a fresh OpenAnchorEditPanel call
        // re-derived the heuristic from updated depth), the panel
        // SHOULD re-seed. This is the only mechanism the parent has
        // to push a new suggestion; pin it.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.InitialRadiusMeters, 30));

        // Helm taps 75-chip.
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75")).Click();

        // Parent re-renders with a DIFFERENT initial (panel re-opened
        // for a new anchorage with a different depth-derived suggestion).
        cut.SetParametersAndRender(p => p.Add(x => x.InitialRadiusMeters, 100));

        // Active chip should now be 100 (the new initial), not 75
        // (the helm's previous tap is for a closed panel).
        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("100");
    }

    [Test]
    public async Task CancelDuringDrop_DoesNotFireOnCancel()
    {
        // Symmetry with Drop's in-handler `if (Dropping) return;` gate.
        // bUnit's .Click() ignores `disabled`, so the gate is the only
        // real defence. Pin: with Dropping=true, a Cancel click fires
        // the handler but the handler refuses to invoke the parent's
        // OnCancel callback.
        using var ctx = new Bunit.TestContext();
        bool fired = false;
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.InitialRadiusMeters, 30)
            .Add(x => x.Dropping, true)
            .Add(x => x.OnCancel, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(p, () => fired = true)));

        var cancel = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Cancel");
        cancel.Click();

        await Assert.That(fired).IsFalse();
    }

    [Test]
    [Arguments(20)]
    [Arguments(30)]
    [Arguments(50)]
    [Arguments(75)]
    [Arguments(100)]
    [Arguments(150)]
    public async Task EveryPreset_ReachableAsActiveChip(int preset)
    {
        // Boundary chips (20 = floor, 150 = ceiling) get specific
        // coverage so a regression that disabled the largest or
        // smallest preset only shows as a chip-tap no-op.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: preset);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains(preset.ToString());
    }

    [Test]
    public async Task LastChipTapWins_OnDropFiresLatest()
    {
        // Multi-tap ordering contract: tap 75 then tap 30 -> Drop
        // fires 30, not 75. The chip row is single-select with
        // last-tap-wins semantics.
        using var ctx = new Bunit.TestContext();
        int? captured = null;
        var cut = Render(ctx, initial: 100, onDrop: r => captured = r);

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75")).Click();
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("30")).Click();
        cut.Find(".anchor-edit-drop").Click();

        await Assert.That(captured).IsEqualTo(30);
    }
}
