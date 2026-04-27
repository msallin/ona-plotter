using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit smoke tests for RouteEditPanel. It's a pure parameter-in,
/// event-out component with no DI - ideal starter target for the new
/// Blazor component test harness.
/// </summary>
public class RouteEditPanelTests
{
    [Test]
    public async Task Renders_Name_And_Stats()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.Name, "Morning Sail")
            .Add(x => x.Stats, "4 WP / 2.3 nm"));

        await Assert.That(cut.Markup).Contains("4 WP / 2.3 nm");
        var input = cut.Find("input.route-name-input");
        await Assert.That(input.GetAttribute("value")).IsEqualTo("Morning Sail");
    }

    [Test]
    public async Task NameInput_Raises_NameChanged()
    {
        using var ctx = new Bunit.TestContext();
        string? captured = null;
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.Name, "")
            .Add(x => x.NameChanged, EventCallback.Factory.Create<string>(this, v => captured = v)));

        cut.Find("input.route-name-input").Input("Night Leg");

        await Assert.That(captured).IsEqualTo("Night Leg");
    }

    [Test]
    public async Task SaveButton_Fires_OnSave_AndIsPrimary()
    {
        using var ctx = new Bunit.TestContext();
        int fires = 0;
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.OnSave, EventCallback.Factory.Create(this, () => fires++)));

        // Save is the primary action -- carries the .active class
        // (helm-flagged: Save & Go is hidden when editing the active
        // route, and Save is the obvious commit verb in every flow).
        var save = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Save");
        await Assert.That(save.GetAttribute("class") ?? "").Contains("active");
        save.Click();

        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task SaveAndGoButton_Fires_OnSaveAndGo()
    {
        // Save & Go activates the new route immediately after save. Pins
        // that the callback is wired and distinct from plain OnSave.
        using var ctx = new Bunit.TestContext();
        int saveFires = 0, goFires = 0;
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.OnSave,      EventCallback.Factory.Create(this, () => saveFires++))
            .Add(x => x.OnSaveAndGo, EventCallback.Factory.Create(this, () => goFires++)));

        var btn = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Contains("Save & Go") || b.TextContent.Contains("Save & Go"));
        btn.Click();

        await Assert.That(goFires).IsEqualTo(1);
        await Assert.That(saveFires).IsEqualTo(0);
    }

    [Test]
    public async Task CancelButton_Fires_OnCancel()
    {
        // The "Undo" button (and its OnUndo callback) was dropped on
        // helm request -- the per-waypoint × on the list rows plus
        // marker-drag-to-reposition cover the same UX, and Undo was
        // redundant chrome. Cancel remains; assert it still wires.
        using var ctx = new Bunit.TestContext();
        int cancel = 0;
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.OnCancel, EventCallback.Factory.Create(this, () => cancel++)));

        var btn = cut.FindAll("button.map-btn")
            .First(b => b.TextContent.Trim() == "Cancel");
        btn.Click();

        await Assert.That(cancel).IsEqualTo(1);
    }

    [Test]
    public async Task SaveAndGo_Hidden_When_EditingActiveRoute()
    {
        // Editing an existing route that is ALSO the currently-active
        // course: Save and Save & Go would perform identical operations
        // (the route is already active), so the "& Go" button is
        // hidden to remove the duplicated verb. Save remains primary.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.IsEditingExisting, true)
            .Add(x => x.IsEditingActiveRoute, true));

        var labels = cut.FindAll("button.map-btn").Select(b => b.TextContent.Trim()).ToList();
        await Assert.That(labels).DoesNotContain("Save & Go");
        await Assert.That(labels).Contains("Save");
    }

    [Test]
    public async Task List_Hidden_When_Coords_Missing_Or_Empty()
    {
        using var ctx = new Bunit.TestContext();

        // Null coords: no list element.
        var noCoords = ctx.RenderComponent<RouteEditPanel>(p => p.Add(x => x.Coords, null));
        await Assert.That(noCoords.FindAll(".route-edit-list").Count).IsEqualTo(0);

        // Empty coords array: still no list -- "Waypoints (0)" would be noise.
        var empty = ctx.RenderComponent<RouteEditPanel>(p => p.Add(x => x.Coords, System.Array.Empty<double[]>()));
        await Assert.That(empty.FindAll(".route-edit-list").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Renders_Numbered_Rows_For_Each_Coord()
    {
        using var ctx = new Bunit.TestContext();
        var coords = new double[][]
        {
            new[] { 48.2144, 11.5753 },
            new[] { 48.22, 11.58 },
            new[] { 48.23, 11.59 }
        };
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p.Add(x => x.Coords, coords));

        var rows = cut.FindAll(".route-edit-wp-row");
        await Assert.That(rows.Count).IsEqualTo(3);

        // Badge text is 1-indexed so it matches the on-map marker numbers.
        var badges = cut.FindAll(".route-edit-wp-badge");
        await Assert.That(badges[0].TextContent.Trim()).IsEqualTo("1");
        await Assert.That(badges[2].TextContent.Trim()).IsEqualTo("3");

        // Coord formatting uses the hemisphere suffix, not signed degrees.
        await Assert.That(cut.Markup).Contains("48.2144");
        await Assert.That(cut.Markup).Contains("N");
        await Assert.That(cut.Markup).Contains("E");

        // The panel count badge echoes the length.
        await Assert.That(cut.Find(".route-edit-list-count").TextContent.Trim()).IsEqualTo("3");
    }

    [Test]
    public async Task Remove_Button_Fires_OnRemoveWaypoint_With_Index()
    {
        using var ctx = new Bunit.TestContext();
        int? removed = null;
        var coords = new double[][]
        {
            new[] { 48.2, 11.5 },
            new[] { 48.3, 11.6 },
            new[] { 48.4, 11.7 }
        };
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.Coords, coords)
            .Add(x => x.OnRemoveWaypoint, EventCallback.Factory.Create<int>(this, i => removed = i)));

        // Click the middle row's remove button -- verifies the captured
        // `idx` lambda closure captures per-iteration, not the final value.
        cut.FindAll(".route-edit-wp-remove")[1].Click();

        await Assert.That(removed).IsEqualTo(1);
    }

    [Test]
    public async Task Renders_Southern_Western_Hemisphere_Coords()
    {
        // Covers the SW quadrant -- negative lat/lon should render with S/W
        // suffixes, not minus signs, and use absolute-value degrees.
        using var ctx = new Bunit.TestContext();
        var coords = new double[][] { new[] { -33.8688, -151.2093 } };
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p.Add(x => x.Coords, coords));

        var coord = cut.Find(".route-edit-wp-coord").TextContent;
        await Assert.That(coord).Contains("33.8688");
        await Assert.That(coord).Contains("S");
        await Assert.That(coord).Contains("151.2093");
        await Assert.That(coord).Contains("W");
        await Assert.That(coord.Contains('-')).IsFalse();
    }
}
