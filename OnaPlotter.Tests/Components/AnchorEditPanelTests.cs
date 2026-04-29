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
    public async Task Initial_BetweenPresets_SnapsDown()
    {
        // Heuristic produced 65 m (e.g. 13 m depth * 5). The chip row
        // doesn't have 65 m, so we snap to the next-lower preset
        // (50 m). The bias is intentionally toward tighter (more
        // sensitive) alarm rather than looser.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 65);

        var actives = cut.FindAll(".anchor-edit-chips .map-btn.active");
        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].TextContent).Contains("50");
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
        // Empty label hides the subline so first-runs without a
        // depth source don't show a stale hint.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, initial: 30, suggestion: "5x 6m depth");
        await Assert.That(cut.Markup).Contains("5x 6m depth");
    }
}
