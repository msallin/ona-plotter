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
    public async Task SaveButton_Fires_OnSave()
    {
        using var ctx = new Bunit.TestContext();
        int fires = 0;
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.OnSave, EventCallback.Factory.Create(this, () => fires++)));

        cut.Find("button.active").Click();       // the Save button has .active

        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task UndoAndCancel_Fire_ResultingCallbacks()
    {
        using var ctx = new Bunit.TestContext();
        int undo = 0, cancel = 0;
        var cut = ctx.RenderComponent<RouteEditPanel>(p => p
            .Add(x => x.OnUndo, EventCallback.Factory.Create(this, () => undo++))
            .Add(x => x.OnCancel, EventCallback.Factory.Create(this, () => cancel++)));

        // Three .map-btn children: Undo, Save (.active), Cancel. Pick by text.
        var buttons = cut.FindAll("button.map-btn");
        foreach (var b in buttons)
        {
            if (b.TextContent.Contains("Undo")) b.Click();
            if (b.TextContent.Contains("Cancel")) b.Click();
        }

        await Assert.That(undo).IsEqualTo(1);
        await Assert.That(cancel).IsEqualTo(1);
    }
}
