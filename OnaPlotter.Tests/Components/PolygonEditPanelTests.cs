using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for PolygonEditPanel. Mirrors RouteEditPanelTests because
/// the two panels share a shape (name + coord list + undo / save / cancel);
/// the difference is that Save stays disabled until there are 3 vertices.
/// </summary>
public class PolygonEditPanelTests
{
    [Test]
    public async Task Save_Button_Disabled_Below_Three_Vertices()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<PolygonEditPanel>(p => p
            .Add(x => x.Coords, new double[][]
            {
                new[] { 47.4, 8.5 },
                new[] { 47.5, 8.5 },
            }));

        // Save is the .active button in the bar.
        var save = cut.Find("button.map-btn.active");
        await Assert.That(save.HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task Save_Button_Enabled_At_Three_Vertices()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<PolygonEditPanel>(p => p
            .Add(x => x.Coords, new double[][]
            {
                new[] { 47.4, 8.5 },
                new[] { 47.5, 8.5 },
                new[] { 47.5, 8.6 },
            }));

        var save = cut.Find("button.map-btn.active");
        await Assert.That(save.HasAttribute("disabled")).IsFalse();
    }

    [Test]
    public async Task Renders_Numbered_Vertex_List()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<PolygonEditPanel>(p => p
            .Add(x => x.Coords, new double[][]
            {
                new[] { 47.40, 8.50 },
                new[] { 47.41, 8.51 },
                new[] { 47.42, 8.50 },
            }));

        var rows = cut.FindAll(".route-edit-wp-row");
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(cut.Find(".route-edit-list-count").TextContent.Trim()).IsEqualTo("3");
    }

    [Test]
    public async Task Remove_Button_Fires_With_Correct_Index()
    {
        using var ctx = new Bunit.TestContext();
        int? removed = null;
        var cut = ctx.RenderComponent<PolygonEditPanel>(p => p
            .Add(x => x.Coords, new double[][]
            {
                new[] { 47.40, 8.50 },
                new[] { 47.41, 8.51 },
                new[] { 47.42, 8.50 },
            })
            .Add(x => x.OnRemoveVertex, EventCallback.Factory.Create<int>(this, i => removed = i)));

        cut.FindAll(".route-edit-wp-remove")[2].Click();
        await Assert.That(removed).IsEqualTo(2);
    }

    [Test]
    public async Task NameInput_Raises_NameChanged()
    {
        using var ctx = new Bunit.TestContext();
        string? captured = null;
        var cut = ctx.RenderComponent<PolygonEditPanel>(p => p
            .Add(x => x.Name, "")
            .Add(x => x.NameChanged, EventCallback.Factory.Create<string>(this, v => captured = v)));

        cut.Find("input.route-name-input").Input("Kelp patch");
        await Assert.That(captured).IsEqualTo("Kelp patch");
    }

    [Test]
    public async Task Undo_And_Cancel_Fire_Callbacks()
    {
        using var ctx = new Bunit.TestContext();
        int undo = 0, cancel = 0;
        var cut = ctx.RenderComponent<PolygonEditPanel>(p => p
            .Add(x => x.OnUndo, EventCallback.Factory.Create(this, () => undo++))
            .Add(x => x.OnCancel, EventCallback.Factory.Create(this, () => cancel++)));

        foreach (var b in cut.FindAll("button.map-btn"))
        {
            if (b.TextContent.Contains("Undo")) b.Click();
            if (b.TextContent.Contains("Cancel")) b.Click();
        }

        await Assert.That(undo).IsEqualTo(1);
        await Assert.That(cancel).IsEqualTo(1);
    }
}
