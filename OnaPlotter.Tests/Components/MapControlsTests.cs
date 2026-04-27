using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit coverage for the bottom control bar. Focuses on the More menu
/// because it's the most stateful part and easy to regress -- the dot
/// badge, active-item highlighting, backdrop dismiss, Race visibility,
/// Route button's active-during-edit state.
/// </summary>
public class MapControlsTests
{
    // MapControls' own parameter defaults cover the happy cruise case; the
    // test helper just forwards the optional configure callback.
    private static IRenderedComponent<MapControls> Render(Bunit.TestContext ctx,
        Action<ComponentParameterCollectionBuilder<MapControls>>? configure = null)
    {
        return ctx.RenderComponent<MapControls>(p => configure?.Invoke(p));
    }

    /// <summary>Simulates the Map.razor parent that owns MoreMenuOpen
    /// state. Tests that exercise opening / closing the menu wire
    /// OnToggleMore through this helper so the parameter flip is
    /// applied to the rendered tree, matching how the real parent
    /// drives the controlled component.</summary>
    private sealed class MoreMenuController
    {
        public bool Open { get; private set; }
        public IRenderedComponent<MapControls> Cut { get; set; } = default!;
        public EventCallback Toggle => EventCallback.Factory.Create(this, () =>
        {
            Open = !Open;
            Cut.SetParametersAndRender(p => p.Add(x => x.MoreMenuOpen, Open));
        });
    }

    private static IRenderedComponent<MapControls> RenderWithMoreController(
        Bunit.TestContext ctx,
        out MoreMenuController controller,
        Action<ComponentParameterCollectionBuilder<MapControls>>? extra = null)
    {
        var c = new MoreMenuController();
        var cut = ctx.RenderComponent<MapControls>(p =>
        {
            p.Add(x => x.MoreMenuOpen, false);
            p.Add(x => x.OnToggleMore, c.Toggle);
            extra?.Invoke(p);
        });
        c.Cut = cut;
        controller = c;
        return cut;
    }

    [Test]
    public async Task More_Button_Shows_Dot_When_Contained_Toggle_Is_On()
    {
        // Dot badge class = .ctrl-btn-dot. Tests each of the contained
        // toggles individually because a regression could easily drop
        // one from AnyMoreItemActive. Measure is intentionally NOT in
        // this list any more -- it's a permanent bar button on every
        // viewport, and its bar slot already shows the active state, so
        // a More dot for Measure would be redundant + misleading.
        using var ctx = new Bunit.TestContext();

        var night = Render(ctx, p => p.Add(x => x.NightMode, true));
        await Assert.That(night.Find(".ctrl-more-wrap > button").ClassList).Contains("ctrl-btn-dot");

        var lay = Render(ctx, p => p.Add(x => x.LaylinesVisible, true));
        await Assert.That(lay.Find(".ctrl-more-wrap > button").ClassList).Contains("ctrl-btn-dot");

        // Measure on by itself must NOT light the dot (its bar button
        // shows the state directly now).
        var meas = Render(ctx, p => p.Add(x => x.MeasureActive, true));
        await Assert.That(meas.Find(".ctrl-more-wrap > button").ClassList.Contains("ctrl-btn-dot")).IsFalse();

        var none = Render(ctx);
        await Assert.That(none.Find(".ctrl-more-wrap > button").ClassList.Contains("ctrl-btn-dot")).IsFalse();
    }

    [Test]
    public async Task More_Button_Title_Lists_Active_Items()
    {
        // The dynamic title makes the dot discoverable on hover without
        // opening the menu. The default catalogue lists what's actually
        // inside the menu (Legend / Night / Laylines / Orient).
        // Active toggles get summarised in parens; Measure is excluded
        // because it lives on the bar permanently.
        using var ctx = new Bunit.TestContext();

        var off = Render(ctx);
        await Assert.That(off.Find(".ctrl-more-wrap > button").GetAttribute("title"))
            .IsEqualTo("More: Legend, Night, Laylines, Harbor, Orient");

        var some = Render(ctx, p => p
            .Add(x => x.NightMode, true)
            .Add(x => x.LaylinesVisible, true));
        await Assert.That(some.Find(".ctrl-more-wrap > button").GetAttribute("title"))
            .IsEqualTo("More (on: Night, Laylines)");

        // Measure being on by itself should leave the title at the
        // default catalogue, not surface "(on: Measure)".
        var measOn = Render(ctx, p => p.Add(x => x.MeasureActive, true));
        await Assert.That(measOn.Find(".ctrl-more-wrap > button").GetAttribute("title"))
            .IsEqualTo("More: Legend, Night, Laylines, Harbor, Orient");
    }

    [Test]
    public async Task More_Button_Dot_Lights_For_HarborMode()
    {
        // Harbor mode is in AnyMoreItemActive specifically because a
        // forgotten Harbor mode is the worst-case (alarms suppressed
        // riding into open water). The More dot must signal it from
        // the bar without opening the menu.
        using var ctx = new Bunit.TestContext();
        var harbor = Render(ctx, p => p.Add(x => x.HarborMode, true));
        await Assert.That(harbor.Find(".ctrl-more-wrap > button").ClassList).Contains("ctrl-btn-dot");
        await Assert.That(harbor.Find(".ctrl-more-wrap > button").GetAttribute("title"))
            .IsEqualTo("More (on: Harbor)");
    }

    [Test]
    public async Task More_Menu_Opens_On_Click_And_Backdrop_Dismisses()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderWithMoreController(ctx, out _);

        // Closed initially: no menu, no backdrop.
        await Assert.That(cut.FindAll(".ctrl-more-menu").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".ctrl-more-backdrop").Count).IsEqualTo(0);

        cut.Find(".ctrl-more-wrap > button").Click();
        await Assert.That(cut.FindAll(".ctrl-more-menu").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".ctrl-more-backdrop").Count).IsEqualTo(1);

        // Backdrop click dismisses -- no re-toggle of More required.
        cut.Find(".ctrl-more-backdrop").Click();
        await Assert.That(cut.FindAll(".ctrl-more-menu").Count).IsEqualTo(0);
    }

    [Test]
    public async Task More_Menu_Item_Click_Fires_Callback_And_Closes_Menu()
    {
        // Picking a menu item should invoke the callback AND close the
        // menu. Otherwise the user sees two navigation clicks worth of
        // state change for one tap.
        using var ctx = new Bunit.TestContext();
        int fired = 0;
        var cut = RenderWithMoreController(ctx, out _, p => p
            .Add(x => x.OnToggleLaylines, EventCallback.Factory.Create(this, () => fired++)));

        cut.Find(".ctrl-more-wrap > button").Click();
        // Menu open, pick Laylines.
        var items = cut.FindAll(".ctrl-more-item");
        AngleSharp.Dom.IElement? laylinesItem = null;
        foreach (var el in items)
        {
            if (el.TextContent.Contains("Laylines")) { laylinesItem = el; break; }
        }
        await Assert.That(laylinesItem).IsNotNull();
        laylinesItem!.Click();

        await Assert.That(fired).IsEqualTo(1);
        // Menu closed after the selection.
        await Assert.That(cut.FindAll(".ctrl-more-menu").Count).IsEqualTo(0);
    }

    [Test]
    public async Task More_Button_Has_Aria_Expanded_And_Haspopup()
    {
        // Accessibility contract: the toggle must tell screen readers
        // this is a menu and whether it's open. Keyboard-only users
        // on a 21" helm need this to navigate.
        using var ctx = new Bunit.TestContext();
        var cut = RenderWithMoreController(ctx, out _);

        var btn = cut.Find(".ctrl-more-wrap > button");
        await Assert.That(btn.GetAttribute("aria-haspopup")).IsEqualTo("menu");
        await Assert.That(btn.GetAttribute("aria-expanded")).IsEqualTo("false");

        btn.Click();
        await Assert.That(cut.Find(".ctrl-more-wrap > button").GetAttribute("aria-expanded"))
            .IsEqualTo("true");
    }

    [Test]
    public async Task Race_Button_Hidden_In_Cruise_Mode()
    {
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, p => p.Add(x => x.SailingMode, "cruise"));
        // Exactly no button with the "Race" label.
        await Assert.That(cut.FindAll("button").Any(b => b.TextContent.Contains("Race"))).IsFalse();
    }

    [Test]
    public async Task Race_Button_Visible_When_Timer_Active_Even_In_Cruise()
    {
        // Edge: user started a timer in Race mode then flipped to Cruise
        // mid-countdown. Hiding the button would orphan a running timer
        // with no way to stop it; keep it visible while RaceTimerActive.
        // Look at the title attribute rather than visible text because
        // the visible label switches to the countdown (RaceTimerLabel)
        // when the timer is live.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, p => p
            .Add(x => x.SailingMode, "cruise")
            .Add(x => x.RaceTimerActive, true));
        await Assert.That(
            cut.FindAll("button").Any(b => b.GetAttribute("title")?.Contains("Race timer") == true))
            .IsTrue();
    }

    [Test]
    public async Task Fab_Menu_Opens_Inside_Wrap_So_It_Anchors_To_Add_Button()
    {
        // The Add flyout used to render at the Map.razor level with
        // position: fixed / left: 12px, which landed it below More instead
        // of above Add. Moving the menu inside a .ctrl-more-wrap around
        // the Add button makes position: absolute anchor to Add itself.
        // Regression guard: when FabMenuOpen, the menu markup exists as a
        // descendant of a .ctrl-more-wrap (not a standalone sibling).
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, p => p.Add(x => x.FabMenuOpen, true));

        // Two .ctrl-more-wrap divs exist (More, Add); at least one of
        // them must contain a .ctrl-more-menu so the flyout is anchored.
        var wraps = cut.FindAll(".ctrl-more-wrap");
        var wrapsWithMenu = wraps.Count(w =>
            w.QuerySelector(".ctrl-more-menu") is not null);
        await Assert.That(wrapsWithMenu).IsGreaterThan(0);
    }

    [Test]
    public async Task Add_Button_Marks_Active_While_RouteEditMode_Is_True()
    {
        // Route moved into the Add flyout, so the Add button itself is
        // the bar-level indicator that a route edit is in progress.
        // The user needs that visual cue even when the flyout is closed.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, p => p.Add(x => x.RouteEditMode, true));

        // Find the bar-level Add button (not a menu item -- the menu is
        // closed by default so only the toolbar button renders).
        var addBtn = cut.FindAll("button.ctrl-btn")
            .FirstOrDefault(b => b.TextContent.Contains("Add"));
        await Assert.That(addBtn).IsNotNull();
        await Assert.That(addBtn!.ClassList).Contains("active");
    }

    [Test]
    public async Task Route_Menu_Item_Disabled_During_Edit()
    {
        // Tapping "Route" while already editing would double-start the
        // flow. The item must render but be disabled, consistent with
        // the old button behaviour.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, p => p
            .Add(x => x.FabMenuOpen, true)
            .Add(x => x.RouteEditMode, true));

        var routeItem = cut.FindAll(".ctrl-more-item")
            .FirstOrDefault(b => b.TextContent.Contains("Route"));
        await Assert.That(routeItem).IsNotNull();
        await Assert.That(routeItem!.HasAttribute("disabled")).IsTrue();
    }

    // --- Stop Navigation two-tap confirm ---

    [Test]
    public async Task Stop_Button_FirstClick_ArmsConfirmState_DoesNotInvoke()
    {
        using var ctx = new Bunit.TestContext();
        int invokes = 0;
        var cut = ctx.RenderComponent<MapControls>(p => p
            .Add(x => x.RouteNavigating, true)
            .Add(x => x.OnStopNavigation,
                EventCallback.Factory.Create(this, () => invokes++)));

        var stop = cut.FindAll(".ctrl-btn-danger")
            .First(b => b.TextContent.Contains("Stop"));
        stop.Click();

        await Assert.That(invokes).IsEqualTo(0);
        // Label flipped to "Confirm?"; CSS class added.
        var refreshed = cut.FindAll(".ctrl-btn-danger")
            .First(b => b.ClassList.Contains("ctrl-btn-confirming"));
        await Assert.That(refreshed.TextContent).Contains("Confirm");
    }

    [Test]
    public async Task Stop_Button_SecondClick_InvokesOnStopNavigation()
    {
        using var ctx = new Bunit.TestContext();
        int invokes = 0;
        var cut = ctx.RenderComponent<MapControls>(p => p
            .Add(x => x.RouteNavigating, true)
            .Add(x => x.OnStopNavigation,
                EventCallback.Factory.Create(this, () => invokes++)));

        var stop = cut.FindAll(".ctrl-btn-danger")
            .First(b => b.TextContent.Contains("Stop"));
        stop.Click();  // arm
        var armed = cut.FindAll(".ctrl-btn-danger")
            .First(b => b.ClassList.Contains("ctrl-btn-confirming"));
        armed.Click();  // confirm

        await Assert.That(invokes).IsEqualTo(1);
    }
}
