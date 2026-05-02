using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for WaypointsSection. Like RoutesSection, the row
/// has Go / Edit / Delete actions; tests pin the wiring plus the
/// JS prompt()-driven rename flow. Delete routes through
/// IConfirmationService (FakeConfirmationService for the test).
/// </summary>
public class WaypointsSectionTests
{
    private static SignalkWaypoint Wp(string id, string? name = null) =>
        new() { Id = id, Name = name };

    private static (Bunit.TestContext ctx, FakeConfirmationService confirm) Context()
    {
        var ctx = new Bunit.TestContext();
        var confirm = new FakeConfirmationService();
        ctx.Services.AddSingleton<IConfirmationService>(confirm);
        return (ctx, confirm);
    }

    [Test]
    public async Task Hidden_When_No_Waypoints()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<WaypointsSection>();
        await Assert.That(cut.Markup.Trim()).IsEqualTo("");
    }

    [Test]
    public async Task Filter_Hides_NonMatching_Rows()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Alpha"), Wp("w2", "Bravo"), Wp("w3", "Alpenhorn") })
            .Add(x => x.LayerFilter, "Alp"));
        cut.Find(".section-toggle").Click();

        await Assert.That(cut.Markup).Contains("Alpha");
        await Assert.That(cut.Markup).Contains("Alpenhorn");
        await Assert.That(cut.Markup).DoesNotContain("Bravo");
    }

    [Test]
    public async Task Filter_NoMatch_HidesWholeSection()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Alpha") })
            .Add(x => x.LayerFilter, "zzz"));
        await Assert.That(cut.Markup.Trim()).IsEqualTo("");
    }

    [Test]
    public async Task GoButton_Fires_OnNavigate_WithSameWaypoint()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        SignalkWaypoint? captured = null;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnNavigate, EventCallback.Factory.Create<SignalkWaypoint>(
                this, w => captured = w)));
        cut.Find(".section-toggle").Click();

        // Pin the Go button by its title attribute rather than slot
        // index so a future button-order tweak (e.g. Focus moves
        // ahead of Go) doesn't false-fail this test.
        cut.Find("button[title='Navigate to this waypoint']").Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("w1");
    }

    [Test]
    public async Task EditButton_Click_Fires_OnEdit_WithWaypoint()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        // Replaces the older PromptEdit-based EditButton tests:
        // the layers-panel Edit button no longer surfaces a JS
        // prompt; it raises OnEdit which the parent wires to open
        // the create-dialog pre-filled. This test pins the new
        // contract so a future drift back to a prompt-based flow
        // surfaces here first.
        SignalkWaypoint? captured = null;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnEdit, EventCallback.Factory.Create<SignalkWaypoint>(
                this, w => captured = w)));
        cut.Find(".section-toggle").Click();

        cut.Find("button[title='Edit name + description']").Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("w1");
        await Assert.That(captured.Name).IsEqualTo("Harbor");
    }

    [Test]
    public async Task DeleteButton_ConfirmAccepted_Fires_OnDelete()
    {
        var (ctx, confirm) = Context();
        using var _ctx = ctx;
        confirm.AutoConfirm = true;
        SignalkWaypoint? captured = null;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnDelete, EventCallback.Factory.Create<SignalkWaypoint>(
                this, w => captured = w)));
        cut.Find(".section-toggle").Click();

        cut.Find("button[title='Delete waypoint']").Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("w1");
    }

    [Test]
    public async Task DeleteButton_ConfirmDeclined_DoesNotFire()
    {
        var (ctx, confirm) = Context();
        using var _ctx = ctx;
        confirm.AutoConfirm = false;
        bool fired = false;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnDelete, EventCallback.Factory.Create<SignalkWaypoint>(
                this, _ => fired = true)));
        cut.Find(".section-toggle").Click();

        cut.Find("button[title='Delete waypoint']").Click();
        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task FocusButton_FiresOnFocus_WithSameWaypoint()
    {
        // The new Focus button (slot 0) feeds the Map page's
        // FocusWaypoint, which centres the chart on the wp's
        // lat/lon. Pin the wiring; the pan-to-position itself is
        // a Map-page concern.
        var (ctx, _) = Context();
        using var _ctx = ctx;
        SignalkWaypoint? captured = null;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnFocus, EventCallback.Factory.Create<SignalkWaypoint>(
                this, w => captured = w)));
        cut.Find(".section-toggle").Click();

        cut.Find("button[title='Center the map on this waypoint']").Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("w1");
    }
}
