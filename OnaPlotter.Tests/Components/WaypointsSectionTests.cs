using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for WaypointsSection. Like RoutesSection, the row
/// grew Edit + Delete actions recently; tests pin the Go /
/// Edit / Delete wiring plus the JS prompt()-driven rename flow.
/// </summary>
public class WaypointsSectionTests
{
    private static SignalkWaypoint Wp(string id, string? name = null) =>
        new() { Id = id, Name = name };

    [Test]
    public async Task Hidden_When_No_Waypoints()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<WaypointsSection>();
        await Assert.That(cut.Markup.Trim()).IsEqualTo("");
    }

    [Test]
    public async Task Filter_Hides_NonMatching_Rows()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Alpha"), Wp("w2", "Bravo"), Wp("w3", "Alpenhorn") })
            .Add(x => x.LayerFilter, "Alp"));
        cut.Find(".section-toggle").Click();

        // "Alp" matches "Alpha" + "Alpenhorn" but not "Bravo".
        await Assert.That(cut.Markup).Contains("Alpha");
        await Assert.That(cut.Markup).Contains("Alpenhorn");
        await Assert.That(cut.Markup).DoesNotContain("Bravo");
    }

    [Test]
    public async Task Filter_NoMatch_HidesWholeSection()
    {
        // Section is a zero-render when the filter elides every item;
        // otherwise the header would tease with "0" and waste space.
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Alpha") })
            .Add(x => x.LayerFilter, "zzz"));
        await Assert.That(cut.Markup.Trim()).IsEqualTo("");
    }

    [Test]
    public async Task GoButton_Fires_OnNavigate_WithSameWaypoint()
    {
        using var ctx = new Bunit.TestContext();
        SignalkWaypoint? captured = null;
        var cut = ctx.RenderComponent<WaypointsSection>(p => p
            .Add(x => x.Waypoints, new[] { Wp("w1", "Harbor") })
            .Add(x => x.OnNavigate, EventCallback.Factory.Create<SignalkWaypoint>(
                this, w => captured = w)));
        cut.Find(".section-toggle").Click();

        // Button order: Go, Edit, Delete.
        cut.FindAll(".route-action-btn")[0].Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("w1");
    }

    [Test]
    public async Task EditButton_PromptAccepted_Fires_OnRename_WithTrimmedName()
    {
        using var ctx = new Bunit.TestContext();
        // prompt() returns the user's new name with leading/trailing
        // whitespace that the section must trim before propagating.
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
        using var ctx = new Bunit.TestContext();
        // prompt() returning null means the user hit Cancel.
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
        using var ctx = new Bunit.TestContext();
        // Empty-string rename is nonsense; must be rejected before
        // hitting the callback (otherwise a server PUT would overwrite
        // a real name with a blank).
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
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
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
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<bool>("confirm", _ => true).SetResult(false);
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
