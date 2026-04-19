using Bunit;
using OnaPlotter.Components.Map;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for MapHud click-to-expand. The four corner panels share
/// one expandedHud selector; clicking a panel toggles it and flips the
/// hud-panel-expanded class. Also pins the leeway / Beaufort / DMS
/// helpers since they now show up in the expanded markup.
/// </summary>
public class MapHudTests
{
    private sealed class FakeAutopilot : IAutopilotApi
    {
        public Task<bool> SetStateAsync(string state, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AdjustHeadingAsync(double deltaDeg, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static IRenderedComponent<MapHud> Render(Bunit.TestContext ctx, NavigationData data)
    {
        return ctx.RenderComponent<MapHud>(p => p
            .Add(x => x.Data, data)
            .Add(x => x.Autopilot, new FakeAutopilot()));
    }

    [Test]
    public async Task TopLeft_Panel_Starts_Collapsed()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        var cut = Render(ctx, data);

        // No corner should be expanded on initial render.
        await Assert.That(cut.FindAll(".hud-panel-expanded").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Clicking_TopLeft_Flips_Expanded_Class()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        var cut = Render(ctx, data);

        var panel = cut.Find(".hud-top-left .hud-panel");
        panel.Click();

        await Assert.That(panel.ClassList).Contains("hud-panel-expanded");

        // Clicking again collapses.
        panel.Click();
        await Assert.That(panel.ClassList.Contains("hud-panel-expanded")).IsFalse();
    }

    [Test]
    public async Task Clicking_A_Different_Corner_Switches_Expansion()
    {
        // Only one panel can be expanded at a time; switching corners
        // should close the previous and open the new one.
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        var cut = Render(ctx, data);

        cut.Find(".hud-top-left .hud-panel").Click();
        cut.Find(".hud-top-right .hud-panel").Click();

        await Assert.That(cut.Find(".hud-top-left .hud-panel").ClassList.Contains("hud-panel-expanded")).IsFalse();
        await Assert.That(cut.Find(".hud-top-right .hud-panel").ClassList).Contains("hud-panel-expanded");
    }

    [Test]
    public async Task Expanded_TopLeft_Shows_DMS_Position_When_Fix_Available()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        data.ApplyPosition(47.3769, 8.5417); // Zurich-ish
        var cut = Render(ctx, data);

        cut.Find(".hud-top-left .hud-panel").Click();

        // DMS should show degree, minute, second glyphs and N/E hemisphere.
        var extras = cut.Find(".hud-top-left .hud-extra").TextContent;
        await Assert.That(extras).Contains("\u00B0");  // degree
        await Assert.That(extras).Contains("\u2032");  // minute
        await Assert.That(extras).Contains("\u2033");  // second
        await Assert.That(extras).Contains("N");
        await Assert.That(extras).Contains("E");
    }

    [Test]
    public async Task Expanded_TopRight_Shows_SignedAwa_And_Beaufort()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        // 35° starboard (positive radians) at ~10 m/s = F5 Fresh.
        data.Apply("environment.wind.angleApparent", 35.0 * System.Math.PI / 180);
        data.Apply("environment.wind.speedApparent", 10.2);
        data.Apply("environment.wind.angleTrueWater", 60.0 * System.Math.PI / 180);
        data.Apply("environment.wind.speedTrue", 9.0);
        var cut = Render(ctx, data);

        cut.Find(".hud-top-right .hud-panel").Click();

        var extras = cut.Find(".hud-top-right .hud-extra").TextContent;
        await Assert.That(extras).Contains("35\u00B0 STBD");   // AWA
        await Assert.That(extras).Contains("60\u00B0 STBD");   // TWA
        await Assert.That(extras).Contains("F5");              // Beaufort
    }

    [Test]
    public async Task Expanded_TopRight_Port_Side_Negative_Angle()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        data.Apply("environment.wind.angleApparent", -45.0 * System.Math.PI / 180);
        data.Apply("environment.wind.speedApparent", 5.0);
        var cut = Render(ctx, data);

        cut.Find(".hud-top-right .hud-panel").Click();

        var extras = cut.Find(".hud-top-right .hud-extra").TextContent;
        await Assert.That(extras).Contains("45\u00B0 PORT");
    }

    [Test]
    public async Task Expanded_BottomLeft_Hides_Data_Rows_Without_Tide_Plugin()
    {
        // Vessel sitting in a marina with no tide data should expand to
        // a nearly-empty expanded panel -- the compact view shows Depth,
        // the expanded view has no extra rows to render.
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        data.Apply("environment.depth.belowTransducer", 8.5);
        var cut = Render(ctx, data);

        cut.Find(".hud-bottom-left .hud-panel").Click();

        // Expanded block renders, but should have no rows inside.
        var extras = cut.FindAll(".hud-bottom-left .hud-extra-row");
        await Assert.That(extras.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Expanded_BottomRight_Shows_Leeway_When_Cog_And_Hdg_Differ()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        // HDG = 090° (east), COG = 100° (10° starboard of HDG).
        data.Apply("navigation.headingTrue", 90.0 * System.Math.PI / 180);
        data.Apply("navigation.courseOverGroundTrue", 100.0 * System.Math.PI / 180);
        var cut = Render(ctx, data);

        cut.Find(".hud-bottom-right .hud-panel").Click();

        var extras = cut.Find(".hud-bottom-right .hud-extra").TextContent;
        // COG and drift of +10° should be visible.
        await Assert.That(extras).Contains("100");
        await Assert.That(extras).Contains("+10");
    }
}
