using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for RoutesSection. The section grew three action
/// buttons recently (Go, Edit, Delete) so the wiring between the
/// row-local callbacks and the parent's OnToggle / OnEdit /
/// OnNavigate / OnDelete is worth pinning -- misrouting any of
/// them silently activates the wrong flow.
///
/// Delete goes through a JS <c>confirm()</c> guard. bUnit's
/// JSInterop lets us stub that -- we set it to always-confirm or
/// always-deny per test and then assert OnDelete fired / didn't.
/// </summary>
public class RoutesSectionTests
{
    private static SignalkRoute Route(string id, string? name = null, double? distanceMeters = null) =>
        new() { Id = id, Name = name, Distance = distanceMeters };

    [Test]
    public async Task Hidden_When_No_Routes()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<RoutesSection>();
        // Empty-list early-return: the whole section renders nothing.
        await Assert.That(cut.Markup.Trim()).IsEqualTo("");
    }

    [Test]
    public async Task ShowsRouteName_OrFallsBackToShortId()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("abcd1234-rest", "My Passage"), Route("aabbccdd-x") }));
        cut.Find(".section-toggle").Click();

        // Named route: full name displayed. Unnamed route: first 8
        // chars of id as fallback.
        await Assert.That(cut.Markup).Contains("My Passage");
        await Assert.That(cut.Markup).Contains("aabbccdd");
    }

    [Test]
    public async Task DistanceRendered_WhenProvided_ConvertedToNm()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r", "R", distanceMeters: 1852 * 12.5) }));
        cut.Find(".section-toggle").Click();

        // 12.5 nm, one-decimal format.
        await Assert.That(cut.Markup).Contains("12.5 nm");
    }

    [Test]
    public async Task Toggle_Click_Fires_OnToggle_WithEnabledTrue()
    {
        using var ctx = new Bunit.TestContext();
        (SignalkRoute route, bool enabled)? captured = null;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnToggle, EventCallback.Factory.Create<(SignalkRoute, bool)>(
                this, t => captured = t)));
        cut.Find(".section-toggle").Click();

        // Checkbox starts unchecked (Enabled set is empty); clicking
        // fires with enabled=true.
        cut.Find("input[type='checkbox']").Change(true);
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.route.Id).IsEqualTo("r1");
        await Assert.That(captured.Value.enabled).IsTrue();
    }

    [Test]
    public async Task GoButton_Fires_OnNavigate_WithTheSameRoute()
    {
        using var ctx = new Bunit.TestContext();
        SignalkRoute? captured = null;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnNavigate, EventCallback.Factory.Create<SignalkRoute>(
                this, r => captured = r)));
        cut.Find(".section-toggle").Click();

        // Buttons render in document order: Go, Edit, Delete.
        cut.FindAll(".route-action-btn")[0].Click();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("r1");
    }

    [Test]
    public async Task EditButton_Fires_OnEdit_WithTheSameRoute()
    {
        using var ctx = new Bunit.TestContext();
        SignalkRoute? captured = null;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnEdit, EventCallback.Factory.Create<SignalkRoute>(
                this, r => captured = r)));
        cut.Find(".section-toggle").Click();

        cut.FindAll(".route-action-btn")[1].Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("r1");
    }

    [Test]
    public async Task DeleteButton_ConfirmAccepted_Fires_OnDelete()
    {
        using var ctx = new Bunit.TestContext();
        // confirm() -> true: user accepts the delete prompt.
        ctx.JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        SignalkRoute? captured = null;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnDelete, EventCallback.Factory.Create<SignalkRoute>(
                this, r => captured = r)));
        cut.Find(".section-toggle").Click();

        // Buttons[2] is Delete.
        cut.FindAll(".route-action-btn")[2].Click();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("r1");
    }

    [Test]
    public async Task DeleteButton_ConfirmDeclined_DoesNotFire_OnDelete()
    {
        using var ctx = new Bunit.TestContext();
        // confirm() -> false: user bails.
        ctx.JSInterop.Setup<bool>("confirm", _ => true).SetResult(false);
        bool fired = false;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnDelete, EventCallback.Factory.Create<SignalkRoute>(
                this, _ => fired = true)));
        cut.Find(".section-toggle").Click();

        cut.FindAll(".route-action-btn")[2].Click();
        await Assert.That(fired).IsFalse();
    }
}
