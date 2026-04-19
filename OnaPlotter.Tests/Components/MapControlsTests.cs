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

    [Test]
    public async Task More_Button_Shows_Dot_When_Contained_Toggle_Is_On()
    {
        // Dot badge class = .ctrl-btn-dot. Test each of the three
        // contained toggles individually because a regression could
        // easily drop one from AnyMoreItemActive.
        using var ctx = new Bunit.TestContext();

        var night = Render(ctx, p => p.Add(x => x.NightMode, true));
        await Assert.That(night.Find(".ctrl-more-wrap > button").ClassList).Contains("ctrl-btn-dot");

        var lay = Render(ctx, p => p.Add(x => x.LaylinesVisible, true));
        await Assert.That(lay.Find(".ctrl-more-wrap > button").ClassList).Contains("ctrl-btn-dot");

        var meas = Render(ctx, p => p.Add(x => x.MeasureActive, true));
        await Assert.That(meas.Find(".ctrl-more-wrap > button").ClassList).Contains("ctrl-btn-dot");

        var none = Render(ctx);
        await Assert.That(none.Find(".ctrl-more-wrap > button").ClassList.Contains("ctrl-btn-dot")).IsFalse();
    }

    [Test]
    public async Task More_Button_Title_Lists_Active_Items()
    {
        // The dynamic title makes the dot discoverable on hover without
        // opening the menu. Three toggles -> "More (on: Night, Laylines,
        // Measure)". None -> the default catalogue string.
        using var ctx = new Bunit.TestContext();

        var off = Render(ctx);
        await Assert.That(off.Find(".ctrl-more-wrap > button").GetAttribute("title"))
            .IsEqualTo("More: Legend, Night, Laylines, Measure");

        var some = Render(ctx, p => p
            .Add(x => x.NightMode, true)
            .Add(x => x.LaylinesVisible, true));
        await Assert.That(some.Find(".ctrl-more-wrap > button").GetAttribute("title"))
            .IsEqualTo("More (on: Night, Laylines)");
    }

    [Test]
    public async Task More_Menu_Opens_On_Click_And_Backdrop_Dismisses()
    {
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx);

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
        var cut = Render(ctx, p => p
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
        var cut = Render(ctx);

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
    public async Task Fab_Route_With_Wind_Disabled_Without_Polar_Or_Wind()
    {
        // Isochrone routing needs a polar AND live true-wind data.
        // Missing polar: "Upload a polar in Settings first".
        // Missing wind: "Polar OK -- waiting for true-wind data".
        // This pins both gates so the UI doesn't regress to enabling
        // the item when only half the prerequisites are met.
        using var ctx = new Bunit.TestContext();

        // No polar -> disabled regardless of wind.
        var noPolar = Render(ctx, p => p
            .Add(x => x.FabMenuOpen, true)
            .Add(x => x.HasPolar, false)
            .Add(x => x.HasWind, true));
        var item1 = noPolar.FindAll(".ctrl-more-item")
            .First(b => b.TextContent.Contains("Route with wind"));
        await Assert.That(item1.HasAttribute("disabled")).IsTrue();

        // Polar loaded but no wind -> still disabled, different tooltip.
        var noWind = Render(ctx, p => p
            .Add(x => x.FabMenuOpen, true)
            .Add(x => x.HasPolar, true)
            .Add(x => x.HasWind, false));
        var item2 = noWind.FindAll(".ctrl-more-item")
            .First(b => b.TextContent.Contains("Route with wind"));
        await Assert.That(item2.HasAttribute("disabled")).IsTrue();
        await Assert.That(item2.GetAttribute("title")).Contains("waiting for true-wind");

        // Polar + wind -> enabled.
        var ready = Render(ctx, p => p
            .Add(x => x.FabMenuOpen, true)
            .Add(x => x.HasPolar, true)
            .Add(x => x.HasWind, true));
        var item3 = ready.FindAll(".ctrl-more-item")
            .First(b => b.TextContent.Contains("Route with wind"));
        await Assert.That(item3.HasAttribute("disabled")).IsFalse();
    }

    [Test]
    public async Task Route_Button_Stays_Visible_With_Active_Class_During_Edit()
    {
        // A disappearing button mid-edit was disorienting. Regression
        // guard: RouteEditMode=true renders the button .active and
        // disabled, not removed.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, p => p.Add(x => x.RouteEditMode, true));

        var btn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Route"));
        await Assert.That(btn).IsNotNull();
        await Assert.That(btn!.ClassList).Contains("active");
        await Assert.That(btn.HasAttribute("disabled")).IsTrue();
    }
}
