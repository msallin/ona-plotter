using Bunit;
using OnaPlotter.Components.Map.Hud;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// Component tests for HudRouteCard. The perf-B refactor extracted
/// this card out of MapHud so only route-related changes re-render
/// it; these tests pin the snapshot-equality contract and make sure
/// the rendered markup still covers the field matrix (visible vs
/// hidden, WP progress on vs off, route total row appearing only
/// when the server supplies the fields).
/// </summary>
public class HudRouteCardTests
{
    private static HudRouteCard.RouteHudSnapshot Snap(
        bool visible = true,
        string? name = "Night Leg",
        int? pointIndex = null,
        int? pointTotal = null,
        double radius = 50,
        double? distance = 1500,
        double? bearing = 1.57,
        double? vmg = 3.5,
        double? ttg = 1200,
        double? xte = null,
        double? routeDist = null,
        double? routeTtg = null) =>
        new(visible, name, pointIndex, pointTotal, radius, distance, bearing, vmg, ttg, xte, routeDist, routeTtg);

    [Test]
    public async Task NotVisible_RendersNothing()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap(visible: false)));
        await Assert.That(cut.FindAll(".hud-route").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Visible_RendersDtwBrgAlways()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap()));
        await Assert.That(cut.Markup).Contains("DTW");
        await Assert.That(cut.Markup).Contains("BRG");
    }

    [Test]
    public async Task WpProgress_Shown_WhenBothIndexAndTotalPresent()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap(pointIndex: 2, pointTotal: 7)));
        // pointIndex is 0-based from SK so the label shows idx+1.
        await Assert.That(cut.Markup).Contains("WP 3 of 7");
    }

    [Test]
    public async Task WpProgress_HiddenWhenOnlyOneFieldPresent()
    {
        // Degrades cleanly on older SK servers that publish pointTotal
        // but not pointIndex, or vice versa. Don't render half-info.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap(pointIndex: 1, pointTotal: null)));
        await Assert.That(cut.Markup).DoesNotContain("WP");
    }

    [Test]
    public async Task RouteTotalRow_ShownWhenEitherFieldPresent()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap(routeDist: 15_000, routeTtg: null)));
        await Assert.That(cut.Markup).Contains("Route total");
    }

    [Test]
    public async Task ApproachOffHint_ShownWhenRadiusIsZero()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap(radius: 0)));
        await Assert.That(cut.Markup).Contains("APPROACH alarm off");
    }

    [Test]
    public async Task ApproachOffHint_HiddenWhenRadiusPositive()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap(radius: 50)));
        await Assert.That(cut.Markup).DoesNotContain("APPROACH alarm off");
    }

    [Test]
    public async Task Snapshot_EqualityIsMemberwise()
    {
        // The whole point of the record-struct snapshot is that two
        // equal snapshots compare equal and Blazor skips re-render.
        // Pin that equality is structural, not reference.
        var a = Snap();
        var b = Snap();
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Snapshot_FieldChange_NotEqual()
    {
        var a = Snap(distance: 1500);
        var b = Snap(distance: 1400);
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task OnStop_Fires_WhenStopButtonClicked()
    {
        using var ctx = new Bunit.TestContext();
        int fires = 0;
        var cut = ctx.RenderComponent<HudRouteCard>(p => p
            .Add(x => x.Snapshot, Snap())
            .Add(x => x.OnStop, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => fires++)));

        cut.Find(".route-stop-btn").Click();
        await Assert.That(fires).IsEqualTo(1);
    }
}
