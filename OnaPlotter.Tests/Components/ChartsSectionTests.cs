using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for ChartsSection. The active-stack chip list exposes
/// up/down buttons that emit (chart, direction) tuples; the parent
/// resolves them into reorder + persist. Pin:
/// - boundary disabled state for endpoints of the list
/// - correct direction value on click
/// - the displayed order is the REVERSE of Charts[] (top of stack first)
/// </summary>
public class ChartsSectionTests
{
    private static SignalkChart Chart(string id, string name) =>
        new() { Identifier = id, Name = name };

    [Test]
    public async Task ActiveStack_Shows_Only_Enabled_Reversed()
    {
        using var ctx = new Bunit.TestContext();
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "c" };   // b disabled
        var cut = ctx.RenderComponent<ChartsSection>(p => p
            .Add(x => x.Charts, charts)
            .Add(x => x.Enabled, enabled));

        var chips = cut.FindAll(".chart-active-chip");
        await Assert.That(chips.Count).IsEqualTo(2);
        // Reversed: c (was enabled last) sits on top.
        await Assert.That(chips[0].TextContent).Contains("C");
        await Assert.That(chips[1].TextContent).Contains("A");
    }

    [Test]
    public async Task Up_Disabled_On_Top_Chip_Down_Disabled_On_Bottom()
    {
        // 3-chart stack: the top chip can't go further up, the bottom
        // can't go further down. Disabled state is an affordance and
        // an accessibility cue (screen readers announce disabled).
        using var ctx = new Bunit.TestContext();
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "b", "c" };
        var cut = ctx.RenderComponent<ChartsSection>(p => p
            .Add(x => x.Charts, charts)
            .Add(x => x.Enabled, enabled));

        var chips = cut.FindAll(".chart-active-chip");
        // Top chip (index 0) -- up disabled, down enabled.
        var topButtons = chips[0].QuerySelectorAll(".chart-reorder-btn");
        await Assert.That(topButtons[0].HasAttribute("disabled")).IsTrue();
        await Assert.That(topButtons[1].HasAttribute("disabled")).IsFalse();

        // Bottom chip (index 2) -- up enabled, down disabled.
        var botButtons = chips[2].QuerySelectorAll(".chart-reorder-btn");
        await Assert.That(botButtons[0].HasAttribute("disabled")).IsFalse();
        await Assert.That(botButtons[1].HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task Up_Click_Fires_Reorder_With_Direction_Minus_One()
    {
        // Up arrow on a middle chip fires OnReorder(chart, -1). The
        // parent resolves the direction; this test only pins the
        // tuple shape so a silent flip doesn't regress reordering.
        using var ctx = new Bunit.TestContext();
        (SignalkChart chart, int direction)? captured = null;
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "b", "c" };

        var cut = ctx.RenderComponent<ChartsSection>(p => p
            .Add(x => x.Charts, charts)
            .Add(x => x.Enabled, enabled)
            .Add(x => x.OnReorder, EventCallback.Factory.Create<(SignalkChart, int)>(
                this, t => captured = t)));

        // Chip 1 is the middle of the reversed stack (B); click its up button.
        var middleBtns = cut.FindAll(".chart-active-chip")[1].QuerySelectorAll(".chart-reorder-btn");
        middleBtns[0].Click();  // up arrow

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.chart.Identifier).IsEqualTo("b");
        await Assert.That(captured.Value.direction).IsEqualTo(-1);
    }

    [Test]
    public async Task Down_Click_Fires_Reorder_With_Direction_Plus_One()
    {
        using var ctx = new Bunit.TestContext();
        (SignalkChart chart, int direction)? captured = null;
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "b", "c" };

        var cut = ctx.RenderComponent<ChartsSection>(p => p
            .Add(x => x.Charts, charts)
            .Add(x => x.Enabled, enabled)
            .Add(x => x.OnReorder, EventCallback.Factory.Create<(SignalkChart, int)>(
                this, t => captured = t)));

        var middleBtns = cut.FindAll(".chart-active-chip")[1].QuerySelectorAll(".chart-reorder-btn");
        middleBtns[1].Click();  // down arrow

        await Assert.That(captured!.Value.direction).IsEqualTo(1);
    }
}
