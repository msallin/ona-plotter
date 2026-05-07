using Bunit;
using OnaPlotter.Components.Map;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for the v2.0.0+ two-state anchor panel.
///
/// State A (Drop): no anchor active; only a Drop button + Close.
/// Tapping Drop fires <c>OnDrop</c> (no radius arg - step 2 carries
/// that).
///
/// State B (SetRadius): position pinned server-side; the helm picks
/// the radius. Pick-then-Set UX:
///   - Chip taps (numeric OR Auto) update the panel's internal pick
///     and fire <c>OnPreviewRadius(N)</c>. The parent uses that to
///     resize the on-map alarm ring as a visual preview - NO PUT.
///   - The Set button fires <c>OnSetRadius(effective)</c>; that's
///     where the parent does the PUT and closes the panel.
///   - SetRadius mode has NO Close button (helm exits without
///     committing by raising via the bottom-bar Anchor button).
///   - Auto chip + boat drift: when AutoPreviewRadius changes while
///     Auto is the pick, the panel re-emits OnPreviewRadius with the
///     new value - the on-map ring follows the boat in 5 m steps.
///
/// Pinning the contracts here so a future refactor that re-couples
/// the preview/commit pair (e.g. live-commit chips that also fire
/// PUT on tap) breaks the test, not the helm trying to anchor at
/// sundown.
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
        Action<int>? onPreviewRadius = null,
        Action<int>? onSetRadius = null,
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
            .Add(x => x.OnPreviewRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(p, r => onPreviewRadius?.Invoke(r)))
            .Add(x => x.OnSetRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(p, r => onSetRadius?.Invoke(r)))
            .Add(x => x.OnCancel, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create(p, () => onCancel?.Invoke())));
    }

    // ---- Drop mode ----

    [Test]
    public async Task DropMode_Renders_DropButton_NoChips()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderDrop(ctx);

        await Assert.That(cut.FindAll(".anchor-edit-drop").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-chips").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".anchor-edit-set").Count).IsEqualTo(0);
    }

    [Test]
    public async Task DropButton_FiresOnDrop_NoArguments()
    {
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

    // ---- SetRadius mode: layout ----

    [Test]
    public async Task SetRadiusMode_Renders_AutoChip_NumericChips_AndSetButton()
    {
        // Pick-then-Set: the row has 5 numeric chips + Auto, plus
        // a dedicated Set button. Pin so a regression that goes
        // back to live-commit (Set button gone) fails here.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx);

        await Assert.That(cut.FindAll(".anchor-edit-auto").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-set").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".anchor-edit-drop").Count).IsEqualTo(0);

        var chips = cut.FindAll(".anchor-edit-chips .map-btn");
        await Assert.That(chips.Count).IsEqualTo(6);
    }

    [Test]
    public async Task SetRadiusMode_NoCloseButton()
    {
        // Field-study: "Cancel" / "Close" misled both sailors into
        // thinking the dropped pin would be aborted. New SetRadius
        // mode has no escape button - helm raises via the Anchor
        // button if they don't want to set a radius.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx);

        var closeButtons = cut.FindAll("button.map-btn")
            .Where(b => b.TextContent.Trim() is "Close" or "Cancel")
            .ToList();
        await Assert.That(closeButtons.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetRadiusMode_TitleReadsSetAlarmRadius()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx);
        await Assert.That(cut.Find(".anchor-edit-title").TextContent).IsEqualTo("Set alarm radius");
    }

    [Test]
    public async Task SetRadiusMode_InitialRadius_LandsOnPresetChip()
    {
        // Initial=30 -> 30 chip is active (panel seeds _pickedNumeric
        // from InitialRadiusMeters on first parameter set).
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
    public async Task SetRadiusMode_Unseeded_AutoIsActiveByDefault()
    {
        // UnseededInitialRadius -> _pickedNumeric stays null, Auto
        // chip is the implicit active. No numeric chip lights up.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: AnchorEditPanel.UnseededInitialRadius);

        var numericActives = cut.FindAll(".anchor-edit-chips .map-btn.active")
            .Where(b => !b.ClassList.Contains("anchor-edit-auto"))
            .ToList();
        await Assert.That(numericActives.Count).IsEqualTo(0);
        await Assert.That(cut.Find(".anchor-edit-auto").ClassList.Contains("active")).IsTrue();
    }

    // ---- SetRadius mode: chip taps fire preview, NOT commit ----

    [Test]
    public async Task SetRadiusMode_NumericChipTap_FiresOnPreviewRadius_NotOnSetRadius()
    {
        // Pick-then-Set core contract: a chip tap is a preview, not
        // a commit. OnPreviewRadius fires with the picked value;
        // OnSetRadius stays silent until the explicit Set tap.
        // (The initial-mount preview emit is verified separately in
        // SetRadiusMode_AutoPreviewLanding_FiresInitialPreviewWhenAutoActive
        // and InitialRadiusMeters seeding tests; here we render with
        // Unseeded + null AutoPreviewRadius so no initial emit fires
        // and the chip click is the only preview event.)
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var commits = new List<int>();
        var cut = RenderSetRadius(ctx,
            initial: AnchorEditPanel.UnseededInitialRadius,
            autoPreview: null,
            onPreviewRadius: r => preview.Add(r),
            onSetRadius: r => commits.Add(r));

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();

        await Assert.That(preview).IsEquivalentTo([75]);
        await Assert.That(commits).IsEmpty();
    }

    [Test]
    public async Task SetRadiusMode_AutoChipTap_FiresOnPreviewRadiusWithAutoValue()
    {
        // Auto chip tap previews the parent-supplied AutoPreviewRadius
        // (not a hardcoded value) so the on-map ring matches what
        // Set would commit.
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = RenderSetRadius(ctx, initial: 30,
            autoPreview: 47,
            onPreviewRadius: r => preview.Add(r));

        cut.Find(".anchor-edit-auto").Click();

        await Assert.That(preview).Contains(47);
    }

    [Test]
    public async Task SetRadiusMode_AutoChipTap_NoPreview_DoesNotFire()
    {
        // Auto picked + AutoPreviewRadius=null (no GPS, no anchor
        // delta). Tap is panel-internal only; OnPreviewRadius stays
        // silent because there's nothing to preview. The chip face
        // remains plain "Auto" and Set stays disabled.
        // (initial=Unseeded keeps Auto active from the start so
        // there's no initial-mount emit either.)
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = RenderSetRadius(ctx,
            initial: AnchorEditPanel.UnseededInitialRadius,
            autoPreview: null,
            onPreviewRadius: r => preview.Add(r));

        cut.Find(".anchor-edit-auto").Click();

        await Assert.That(preview).IsEmpty();
    }

    [Test]
    public async Task SetRadiusMode_NumericThenAuto_PreviewSwitchesToAutoValue()
    {
        // Helm picks 75, then switches to Auto. Preview should follow
        // the active pick: 75 -> AutoPreviewRadius. Pin so a refactor
        // that caches the last numeric value across an Auto pick
        // (and thereby ships the wrong preview) breaks the test.
        // (initial=Unseeded with autoPreview=47 fires preview(47) on
        // mount; the click sequence then adds 75 and 47.)
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = RenderSetRadius(ctx,
            initial: AnchorEditPanel.UnseededInitialRadius,
            autoPreview: 47,
            onPreviewRadius: r => preview.Add(r));

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();
        cut.Find(".anchor-edit-auto").Click();

        await Assert.That(preview).IsEquivalentTo([47, 75, 47]);
    }

    // ---- SetRadius mode: Set commits ----

    [Test]
    public async Task SetRadiusMode_Set_AfterNumericPick_FiresOnSetRadius_WithChipValue()
    {
        using var ctx = new Bunit.TestContext();
        var commits = new List<int>();
        var cut = RenderSetRadius(ctx, initial: 30,
            onSetRadius: r => commits.Add(r));

        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("50") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();
        cut.Find(".anchor-edit-set").Click();

        await Assert.That(commits).IsEquivalentTo([50]);
    }

    [Test]
    public async Task SetRadiusMode_Set_AfterAutoPick_FiresOnSetRadius_WithAutoPreview()
    {
        // Auto picked + AutoPreviewRadius=47 -> Set commits 47.
        // The single-callback path means the parent's PUT logic is
        // identical for Auto and numeric chips.
        using var ctx = new Bunit.TestContext();
        var commits = new List<int>();
        var cut = RenderSetRadius(ctx,
            initial: AnchorEditPanel.UnseededInitialRadius,
            autoPreview: 47,
            onSetRadius: r => commits.Add(r));

        cut.Find(".anchor-edit-set").Click();

        await Assert.That(commits).IsEquivalentTo([47]);
    }

    [Test]
    public async Task SetRadiusMode_Set_NoEffective_DoesNotFire()
    {
        // Auto picked (default) AND no AutoPreviewRadius landed yet
        // -> Set is disabled and the in-handler guard suppresses
        // any attempted commit. Pin: Set tap with nothing to commit
        // is a no-op rather than a PUT of zero / null.
        using var ctx = new Bunit.TestContext();
        var commits = new List<int>();
        var cut = RenderSetRadius(ctx,
            initial: AnchorEditPanel.UnseededInitialRadius,
            autoPreview: null,
            onSetRadius: r => commits.Add(r));

        var setBtn = cut.Find(".anchor-edit-set");
        await Assert.That(setBtn.HasAttribute("disabled")).IsTrue();
        setBtn.Click();
        await Assert.That(commits).IsEmpty();
    }

    [Test]
    public async Task SetRadiusMode_Set_Busy_DoesNotFire()
    {
        // bUnit's .Click() ignores disabled; the in-handler Busy
        // guard is what actually stops the second PUT mid-flight.
        using var ctx = new Bunit.TestContext();
        var commits = new List<int>();
        var cut = RenderSetRadius(ctx, initial: 50, busy: true,
            onSetRadius: r => commits.Add(r));

        cut.Find(".anchor-edit-set").Click();

        await Assert.That(commits).IsEmpty();
    }

    [Test]
    public async Task SetRadiusMode_Busy_DisablesEverything()
    {
        // While a PUT is in flight: every chip + Set disabled.
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, busy: true);

        foreach (var chip in cut.FindAll(".anchor-edit-chips .map-btn"))
        {
            await Assert.That(chip.HasAttribute("disabled")).IsTrue();
        }
        await Assert.That(cut.Find(".anchor-edit-set").HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task SetRadiusMode_Busy_BlocksChipPreviewToo()
    {
        // The Busy gate also covers the preview path - a preview
        // PUT would race the in-flight Set PUT and confuse the helm
        // about which value committed.
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = RenderSetRadius(ctx, initial: 30, busy: true,
            autoPreview: 47,
            onPreviewRadius: r => preview.Add(r));

        cut.Find(".anchor-edit-auto").Click();
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();

        await Assert.That(preview).IsEmpty();
    }

    // ---- SetRadius mode: live Auto-preview tracking (boat drift) ----

    [Test]
    public async Task SetRadiusMode_AutoPreviewLanding_FiresInitialPreviewWhenAutoActive()
    {
        // Panel just opened with Auto active (Unseeded) and
        // AutoPreviewRadius already landed at 47. The first
        // OnParametersSet should fire OnPreviewRadius(47) so the
        // parent draws the initial alarm ring without the helm
        // having to tap Auto first.
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = RenderSetRadius(ctx,
            initial: AnchorEditPanel.UnseededInitialRadius,
            autoPreview: 47,
            onPreviewRadius: r => preview.Add(r));

        await Assert.That(preview).IsEquivalentTo([47]);
    }

    [Test]
    public async Task SetRadiusMode_AutoPreviewChange_RefiresPreview_WhenAutoIsPicked()
    {
        // Boat drifts; parent recomputes AutoRadiusPreview() each
        // tick. As long as Auto is the active pick, the panel must
        // re-emit OnPreviewRadius with the new value so the on-map
        // ring follows. This is the "Auto means whatever Auto says
        // right now" contract.
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, AnchorEditPanel.UnseededInitialRadius)
            .Add(x => x.AutoPreviewRadius, 30)
            .Add(x => x.OnPreviewRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(this, r => preview.Add(r))));

        // Initial render fires preview(30).
        await Assert.That(preview).IsEquivalentTo([30]);

        // Boat drifts: parent passes new AutoPreviewRadius.
        cut.SetParametersAndRender(p => p.Add(x => x.AutoPreviewRadius, 35));
        await Assert.That(preview).IsEquivalentTo([30, 35]);

        // Drifts again.
        cut.SetParametersAndRender(p => p.Add(x => x.AutoPreviewRadius, 40));
        await Assert.That(preview).IsEquivalentTo([30, 35, 40]);
    }

    [Test]
    public async Task SetRadiusMode_AutoPreviewChange_DoesNotFire_WhenNumericIsPicked()
    {
        // Helm picked 75; AutoPreviewRadius then changes. The on-
        // map ring should stay at 75 (the helm's pick), not jump
        // to the new Auto value. Numeric pick is a sticky decision.
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, AnchorEditPanel.UnseededInitialRadius)
            .Add(x => x.AutoPreviewRadius, 30)
            .Add(x => x.OnPreviewRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(this, r => preview.Add(r))));

        // Helm picks 75.
        cut.FindAll(".anchor-edit-chips .map-btn")
            .First(b => b.TextContent.Contains("75") && !b.ClassList.Contains("anchor-edit-auto"))
            .Click();
        await Assert.That(preview[^1]).IsEqualTo(75);
        int firesAfterPick = preview.Count;

        // Boat drifts - AutoPreviewRadius bumps but should NOT
        // re-fire because numeric is the pick.
        cut.SetParametersAndRender(p => p.Add(x => x.AutoPreviewRadius, 35));
        cut.SetParametersAndRender(p => p.Add(x => x.AutoPreviewRadius, 40));

        await Assert.That(preview.Count).IsEqualTo(firesAfterPick);
    }

    [Test]
    public async Task SetRadiusMode_SamePreviewValue_DoesNotDoubleFire()
    {
        // OnParametersSet runs on every render; we must not fire
        // the same preview value twice in a row (parent's render
        // could trigger our re-render → loop). Pin: AutoPreviewRadius
        // unchanged across re-render = no extra fire.
        using var ctx = new Bunit.TestContext();
        var preview = new List<int>();
        var cut = ctx.RenderComponent<AnchorEditPanel>(p => p
            .Add(x => x.Mode, AnchorEditPanel.AnchorPanelMode.SetRadius)
            .Add(x => x.InitialRadiusMeters, AnchorEditPanel.UnseededInitialRadius)
            .Add(x => x.AutoPreviewRadius, 30)
            .Add(x => x.OnPreviewRadius, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<int>(this, r => preview.Add(r))));

        await Assert.That(preview).IsEquivalentTo([30]);

        // Same value pushed again - e.g., parent re-rendered for
        // an unrelated reason.
        cut.SetParametersAndRender(p => p.Add(x => x.SuggestionLabel, "5x 6m depth"));

        await Assert.That(preview).IsEquivalentTo([30]);
    }

    // ---- SetRadius mode: chip face rendering ----

    [Test]
    public async Task SetRadiusMode_AutoChip_RendersPlainAuto_WhenNoPreview()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, autoPreview: null);

        await Assert.That(cut.Find(".anchor-edit-auto").TextContent.Trim())
            .IsEqualTo("Auto");
    }

    [Test]
    public async Task SetRadiusMode_AutoChip_RendersPreviewedRadius_WhenSet()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, autoPreview: 47);

        var auto = cut.Find(".anchor-edit-auto");
        await Assert.That(auto.TextContent).Contains("Auto:");
        await Assert.That(auto.TextContent).Contains("47");
    }

    [Test]
    public async Task SetRadiusMode_AutoChip_TooltipReflectsPreview()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, autoPreview: 47);

        var auto = cut.Find(".anchor-edit-auto");
        await Assert.That(auto.GetAttribute("title")!).Contains("47");
    }

    [Test]
    public async Task SetRadiusMode_AutoChip_LivePreviewUpdatesOnReRender()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderSetRadius(ctx, initial: 30, autoPreview: 40);

        await Assert.That(cut.Find(".anchor-edit-auto").TextContent).Contains("40");

        cut.SetParametersAndRender(p => p.Add(x => x.AutoPreviewRadius, 55));

        await Assert.That(cut.Find(".anchor-edit-auto").TextContent).Contains("55");
        await Assert.That(cut.Find(".anchor-edit-auto").TextContent).DoesNotContain("40");
    }

    // ---- Mode switching ----

    [Test]
    public async Task ModeSwitch_DropToSetRadius_RendersChipRowAndSet()
    {
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

    // ---- Suggestion label ----

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

    // ---- External InitialRadiusMeters changes (server delta echo) ----

    [Test]
    public async Task SetRadiusMode_InitialChange_ReSeedsActiveChip()
    {
        // Helm picks via the Anchor button -> panel opens with the
        // current MaxRadius as InitialRadiusMeters. After Set, the
        // parent re-renders with a fresh InitialRadiusMeters from
        // the server delta. The active chip should track that.
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
}
