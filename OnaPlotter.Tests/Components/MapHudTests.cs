using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OnaPlotter.Components.Map;
using OnaPlotter.Models;
using OnaPlotter.Services;
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
        public Task<ApiResult> SetStateAsync(string state, CancellationToken ct = default) => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> AdjustHeadingAsync(double deltaDeg, CancellationToken ct = default) => Task.FromResult(ApiResult.Ok);
    }

    private static IRenderedComponent<MapHud> Render(Bunit.TestContext ctx, NavigationData data)
    {
        // MapHud now @injects IToastService for the autopilot
        // heading-nudge audit toast; supply a real ToastService rather
        // than a fake since the assertions never read its state.
        ctx.Services.AddSingleton<IToastService, ToastService>();
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

        var panel = cut.Find(".hud-stack-tl .hud-panel");
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

        cut.Find(".hud-stack-tl .hud-panel").Click();
        cut.Find(".hud-stack-tr .hud-panel").Click();

        await Assert.That(cut.Find(".hud-stack-tl .hud-panel").ClassList.Contains("hud-panel-expanded")).IsFalse();
        await Assert.That(cut.Find(".hud-stack-tr .hud-panel").ClassList).Contains("hud-panel-expanded");
    }

    [Test]
    public async Task Expanded_TopLeft_Shows_DMS_Position_When_Fix_Available()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        data.ApplyPosition(47.3769, 8.5417); // Zurich-ish
        var cut = Render(ctx, data);

        cut.Find(".hud-stack-tl .hud-panel").Click();

        // DMS should show degree, minute, second glyphs and N/E hemisphere.
        var extras = cut.Find(".hud-stack-tl .hud-extra").TextContent;
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

        cut.Find(".hud-stack-tr .hud-panel").Click();

        var extras = cut.Find(".hud-stack-tr .hud-extra").TextContent;
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

        cut.Find(".hud-stack-tr .hud-panel").Click();

        var extras = cut.Find(".hud-stack-tr .hud-extra").TextContent;
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

        cut.Find(".hud-stack-bl .hud-panel").Click();

        // Expanded block renders, but should have no rows inside.
        var extras = cut.FindAll(".hud-stack-bl .hud-extra-row");
        await Assert.That(extras.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Panel_Has_Button_Role_And_Aria_Expanded_For_Keyboard_Users()
    {
        // The four corner panels are plain divs with @onclick; explicit
        // role="button" + tabindex + aria-expanded is what tells screen
        // readers and Tab navigation this is an actionable element.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, new NavigationData());
        var panel = cut.Find(".hud-stack-tl .hud-panel");

        await Assert.That(panel.GetAttribute("role")).IsEqualTo("button");
        await Assert.That(panel.GetAttribute("tabindex")).IsEqualTo("0");
        await Assert.That(panel.GetAttribute("aria-expanded")).IsEqualTo("false");

        panel.Click();
        await Assert.That(panel.GetAttribute("aria-expanded")).IsEqualTo("true");
    }

    [Test]
    public async Task Enter_Key_Toggles_Panel()
    {
        // Keyboard parity with mouse click. Enter / Space match implicit
        // button behaviour.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, new NavigationData());
        var panel = cut.Find(".hud-stack-tl .hud-panel");

        panel.KeyDown("Enter");
        await Assert.That(panel.ClassList).Contains("hud-panel-expanded");

        panel.KeyDown("Escape");
        await Assert.That(panel.ClassList.Contains("hud-panel-expanded")).IsFalse();
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

        cut.Find(".hud-stack-br .hud-panel").Click();

        var extras = cut.Find(".hud-stack-br .hud-extra").TextContent;
        // Drift of +10° should be visible. COG itself is intentionally
        // not duplicated in BR's extras -- it lives on the TL card.
        await Assert.That(extras).Contains("+10");
    }

    // --- Heading T / M suffix ---

    [Test]
    public async Task Heading_ShowsT_WhenTrueHeadingPreferred()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        data.Apply("navigation.headingTrue", 45.0 * System.Math.PI / 180);
        data.PreferMagneticHeading = false;
        var cut = Render(ctx, data);

        var heading = cut.Find(".hud-stack-br .hud-value").TextContent;
        await Assert.That(heading).Contains("T");
    }

    [Test]
    public async Task Heading_ShowsM_WhenMagneticPreferred()
    {
        using var ctx = new Bunit.TestContext();
        var data = new NavigationData();
        data.Apply("navigation.headingMagnetic", 45.0 * System.Math.PI / 180);
        data.PreferMagneticHeading = true;
        var cut = Render(ctx, data);

        var heading = cut.Find(".hud-stack-br .hud-value").TextContent;
        await Assert.That(heading).Contains("M");
    }

    // --- Depth staleness ---

    [Test]
    public async Task Depth_Stale_Badge_AppearsWhenNoRecentUpdate()
    {
        // Apply a depth reading with an old timestamp by constructing a
        // NavigationData with a fake clock that advances between apply
        // and render. Depth aged ~20 s -> Stale tier -> badge text
        // "stale" visible in the depth hud label.
        using var ctx = new Bunit.TestContext();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var clock = new ClockHolder(now);
        var data = new NavigationData(clock.Get);
        data.Apply("environment.depth.belowTransducer", 3.2);
        clock.Now = now.AddSeconds(20);
        var cut = Render(ctx, data);

        var depthLabel = cut.Find(".hud-stack-bl .hud-label").TextContent;
        await Assert.That(depthLabel).Contains("stale");
    }

    [Test]
    public async Task Depth_Dead_Badge_AppearsWhenSensorSilent()
    {
        using var ctx = new Bunit.TestContext();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var clock = new ClockHolder(now);
        var data = new NavigationData(clock.Get);
        data.Apply("environment.depth.belowTransducer", 3.2);
        clock.Now = now.AddSeconds(60);
        var cut = Render(ctx, data);

        var depthLabel = cut.Find(".hud-stack-bl .hud-label").TextContent;
        await Assert.That(depthLabel).Contains("dead");
    }

    private sealed class ClockHolder
    {
        public DateTime Now { get; set; }
        public ClockHolder(DateTime now) { Now = now; }
        public DateTime Get() => Now;
    }
}
