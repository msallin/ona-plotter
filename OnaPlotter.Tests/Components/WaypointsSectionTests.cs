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

        cut.FindAll(".route-action-btn")[0].Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("w1");
    }

    [Test]
    public async Task EditButton_PromptAccepted_Fires_OnRename_WithTrimmedName()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        // prompt() stays on native JS for now; rename flow hasn't
        // been promoted to a service yet. Stub it via bUnit's
        // JSInterop just as before.
        ctx.JSInterop.Setup<string?>("prompt", _ => true).SetResult("  New Harbor  ");
        (SignalkWaypoint wp, string newName)? captured = null;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnRename, EventCallback.Factory.Create<(SignalkWaypoint, string)>(
                this, t => captured = t)));
        cut.Find(".section-toggle").Click();

        cut.FindAll(".route-action-btn")[1].Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.wp.Id).IsEqualTo("w1");
        await Assert.That(captured.Value.newName).IsEqualTo("New Harbor");
    }

    [Test]
    public async Task EditButton_PromptCancelled_DoesNotFire()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        ctx.JSInterop.Setup<string?>("prompt", _ => true).SetResult(null);
        bool fired = false;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnRename, EventCallback.Factory.Create<(SignalkWaypoint, string)>(
                this, _ => fired = true)));
        cut.Find(".section-toggle").Click();

        cut.FindAll(".route-action-btn")[1].Click();
        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task EditButton_EmptyName_DoesNotFire()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        ctx.JSInterop.Setup<string?>("prompt", _ => true).SetResult("   ");
        bool fired = false;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnRename, EventCallback.Factory.Create<(SignalkWaypoint, string)>(
                this, _ => fired = true)));
        cut.Find(".section-toggle").Click();

        cut.FindAll(".route-action-btn")[1].Click();
        await Assert.That(fired).IsFalse();
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

        cut.FindAll(".route-action-btn")[2].Click();
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

        cut.FindAll(".route-action-btn")[2].Click();
        await Assert.That(fired).IsFalse();
    }
}
