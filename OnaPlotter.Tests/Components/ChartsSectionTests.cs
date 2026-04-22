using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for ChartsSection. The reorder affordance is now
/// inline on each rendering chart row (previously a separate
/// .chart-active-chip stack above the list). These tests pin:
/// - rendering rows get the .chart-item-rendering highlight
/// - boundary disabled state (top / bottom of draw stack)
/// - up / down click direction values
/// The section defaults to collapsed, so tests expand it by
/// clicking the header first.
/// </summary>
public class ChartsSectionTests
{
    private static SignalkChart Chart(string id, string name) =>
        new() { Identifier = id, Name = name };

    // Helper: ChartsSection starts collapsed. Rows aren't rendered
    // until the user expands the section, which the SectionHeader
    // button handles. Every test needs to be in the expanded state,
    // so centralise the click.
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

        // Expand the section so .chart-item rows render. The
        // SectionHeader is a div with .section-toggle + @onclick;
        // bUnit's .Click() fires the Blazor handler directly.
        var header = cut.Find(".section-toggle");
        header.Click();
        return cut;
    }

    [Test]
    public async Task Rendering_Rows_Get_Highlight_Class()
    {
        // Only the enabled charts pick up .chart-item-rendering; the
        // others are plain .chart-item rows. This pins the visual
        // signal that replaces the old yellow box.
        using var ctx = new Bunit.TestContext();
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "c" };  // b disabled
        var cut = RenderExpanded(ctx, charts, enabled);

        var renderingRows = cut.FindAll(".chart-item-rendering");
        await Assert.That(renderingRows.Count).IsEqualTo(2);
        // List order is Charts[] order (a, b, c alphabetical); a and
        // c are the rendering ones so they carry the class.
        await Assert.That(renderingRows[0].TextContent).Contains("A");
        await Assert.That(renderingRows[1].TextContent).Contains("C");
    }

    [Test]
    public async Task Reorder_Buttons_Boundary_Disabled_State()
    {
        // All three rendering. ActiveStack is reversed: [C, B, A]
        // with C on top. So on the stack:
        //   - C is at the top  -> up disabled, down enabled
        //   - B is in the middle -> both enabled
        //   - A is at the bottom -> up enabled, down disabled
        using var ctx = new Bunit.TestContext();
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "b", "c" };
        var cut = RenderExpanded(ctx, charts, enabled);

        var rows = cut.FindAll(".chart-item-rendering");
        var rowA = rows[0]; // Charts[] order = A, B, C
        var rowB = rows[1];
        var rowC = rows[2];

        var aBtns = rowA.QuerySelectorAll(".chart-reorder-btn");
        var bBtns = rowB.QuerySelectorAll(".chart-reorder-btn");
        var cBtns = rowC.QuerySelectorAll(".chart-reorder-btn");

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
        // Up arrow on the middle chart fires OnReorder(chart, -1).
        // Parent resolves the direction; this test only pins the
        // tuple shape so a silent flip doesn't regress reordering.
        using var ctx = new Bunit.TestContext();
        (SignalkChart chart, int direction)? captured = null;
        var charts = new[] { Chart("a", "A"), Chart("b", "B"), Chart("c", "C") };
        var enabled = new HashSet<string> { "a", "b", "c" };
        var cb = EventCallback.Factory.Create<(SignalkChart, int)>(this, t => captured = t);
        var cut = RenderExpanded(ctx, charts, enabled, cb);

        var rowB = cut.FindAll(".chart-item-rendering")[1];
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

        var rowB = cut.FindAll(".chart-item-rendering")[1];
        var btns = rowB.QuerySelectorAll(".chart-reorder-btn");
        btns[1].Click();  // down arrow

        await Assert.That(captured!.Value.direction).IsEqualTo(1);
    }
}
