using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for ChartsSection. Every chart row renders with
/// the same shape (checkbox + name + up/down arrows); the arrows
/// are disabled for charts that aren't in the draw stack or are
/// at its ends. These tests pin:
/// - rendering-dot glyph only on enabled rows
/// - both arrows disabled when the chart isn't rendering
/// - boundary disabled state when rendering (top / bottom of stack)
/// - up / down click direction values
/// Section defaults to collapsed; tests expand it by clicking the
/// SectionHeader first.
/// </summary>
public class ChartsSectionTests
{
    private static SignalkChart Chart(string id, string name) =>
        new() { Identifier = id, Name = name };

    private static IRenderedComponent<ChartsSection> RenderExpanded(
        Bunit.TestContext ctx,
        SignalkChart[] charts,
        HashSet<string> enabled,
        EventCallback<(SignalkChart, int)> onReorder = default)
    {
        var cut = ctx.RenderComponent<ChartsSection>(p => p
            .Add(x => x.Charts, charts)
            .Add(x => x.Enabled, enabled)
            .Add(x => x.OnReorder, onReorder));
        cut.Find(".section-toggle").Click();
        return cut;
    }

    [Test]
    public async Task Rendering_Dot_Only_On_Enabled_Rows()
    {
        // Every row renders, but only enabled charts carry the
        // .chart-rendering-dot glyph next to their name. The amber
        // row highlight was dropped, so the dot is the sole signal
        // that a chart is on the map.
        using var ctx = new Bunit.TestContext();
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "c" };  // b disabled
        var cut = RenderExpanded(ctx, charts, enabled);

        var rows = cut.FindAll(".chart-item");
        await Assert.That(rows.Count).IsEqualTo(3);
        // Row order matches Charts[] (A, B, C).
        await Assert.That(rows[0].QuerySelector(".chart-rendering-dot")).IsNotNull();
        await Assert.That(rows[1].QuerySelector(".chart-rendering-dot")).IsNull();
        await Assert.That(rows[2].QuerySelector(".chart-rendering-dot")).IsNotNull();
    }

    [Test]
    public async Task Reorder_Buttons_Disabled_When_Not_Rendering()
    {
        // A chart that's in the quick bar but not currently drawn
        // on the map has no stack position; both arrows disabled.
        using var ctx = new Bunit.TestContext();
        var charts = new[] { Chart("a", "A"), Chart("b", "B") };
        var enabled = new HashSet<string> { "a" };  // only a renders
        var cut = RenderExpanded(ctx, charts, enabled);

        var rowB = cut.FindAll(".chart-item")[1];
        var btns = rowB.QuerySelectorAll(".chart-reorder-btn");
        await Assert.That(btns[0].HasAttribute("disabled")).IsTrue();
        await Assert.That(btns[1].HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task Reorder_Buttons_Boundary_Disabled_State()
    {
        // All three rendering. ActiveStack is reversed: [C, B, A]
        // with C on top. So on the stack:
        //   - C is at the top    -> up disabled, down enabled
        //   - B is in the middle -> both enabled
        //   - A is at the bottom -> up enabled, down disabled
        using var ctx = new Bunit.TestContext();
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "b", "c" };
        var cut = RenderExpanded(ctx, charts, enabled);

        var rows = cut.FindAll(".chart-item");
        var aBtns = rows[0].QuerySelectorAll(".chart-reorder-btn");
        var bBtns = rows[1].QuerySelectorAll(".chart-reorder-btn");
        var cBtns = rows[2].QuerySelectorAll(".chart-reorder-btn");

        // A = bottom of stack (stackIdx 2): up OK, down disabled.
        await Assert.That(aBtns[0].HasAttribute("disabled")).IsFalse();
        await Assert.That(aBtns[1].HasAttribute("disabled")).IsTrue();
        // B = middle (stackIdx 1): both enabled.
        await Assert.That(bBtns[0].HasAttribute("disabled")).IsFalse();
        await Assert.That(bBtns[1].HasAttribute("disabled")).IsFalse();
        // C = top (stackIdx 0): up disabled, down OK.
        await Assert.That(cBtns[0].HasAttribute("disabled")).IsTrue();
        await Assert.That(cBtns[1].HasAttribute("disabled")).IsFalse();
    }

    [Test]
    public async Task Up_Click_Fires_Reorder_With_Direction_Minus_One()
    {
        using var ctx = new Bunit.TestContext();
        (SignalkChart chart, int direction)? captured = null;
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "b", "c" };
        var cb = EventCallback.Factory.Create<(SignalkChart, int)>(this, t => captured = t);
        var cut = RenderExpanded(ctx, charts, enabled, cb);

        var rowB = cut.FindAll(".chart-item")[1];
        var btns = rowB.QuerySelectorAll(".chart-reorder-btn");
        btns[0].Click();  // up arrow

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
        var cb = EventCallback.Factory.Create<(SignalkChart, int)>(this, t => captured = t);
        var cut = RenderExpanded(ctx, charts, enabled, cb);

        var rowB = cut.FindAll(".chart-item")[1];
        var btns = rowB.QuerySelectorAll(".chart-reorder-btn");
        btns[1].Click();  // down arrow

        await Assert.That(captured!.Value.direction).IsEqualTo(1);
    }
}
