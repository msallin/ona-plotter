using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for RoutesSection. The section has three action
/// buttons (Go, Edit, Delete) so the wiring between the row-local
/// callbacks and the parent's OnToggle / OnEdit / OnNavigate /
/// OnDelete is worth pinning -- misrouting any of them silently
/// activates the wrong flow.
///
/// Delete goes through <see cref="IConfirmationService"/>. We
/// register <see cref="FakeConfirmationService"/> and flip its
/// <c>AutoConfirm</c> per test to exercise the accept / decline
/// paths.
/// </summary>
public class RoutesSectionTests
{
    private static SignalkRoute Route(string id, string? name = null, double? distanceMeters = null) =>
        new() { Id = id, Name = name, Distance = distanceMeters };

    /// <summary>Builds a bUnit TestContext with a FakeConfirmationService
    /// registered. Returns the context + fake so tests can flip
    /// AutoConfirm.</summary>
    private static (Bunit.TestContext ctx, FakeConfirmationService confirm) Context()
    {
        var ctx = new Bunit.TestContext();
        var confirm = new FakeConfirmationService();
        ctx.Services.AddSingleton<IConfirmationService>(confirm);
        return (ctx, confirm);
    }

    [Test]
    public async Task Hidden_When_No_Routes()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<RoutesSection>();
        await Assert.That(cut.Markup.Trim()).IsEqualTo("");
    }

    [Test]
    public async Task ShowsRouteName_OrFallsBackToShortId()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("abcd1234-rest", "My Passage"), Route("aabbccdd-x") }));
        cut.Find(".section-toggle").Click();

        await Assert.That(cut.Markup).Contains("My Passage");
        await Assert.That(cut.Markup).Contains("aabbccdd");
    }

    [Test]
    public async Task DistanceRendered_WhenProvided_ConvertedToNm()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r", "R", distanceMeters: 1852 * 12.5) }));
        cut.Find(".section-toggle").Click();

        await Assert.That(cut.Markup).Contains("12.5 nm");
    }

    [Test]
    public async Task Toggle_Click_Fires_OnToggle_WithEnabledTrue()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        (SignalkRoute route, bool enabled)? captured = null;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnToggle, EventCallback.Factory.Create<(SignalkRoute, bool)>(
                this, t => captured = t)));
        cut.Find(".section-toggle").Click();

        cut.Find("input[type='checkbox']").Change(true);
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.route.Id).IsEqualTo("r1");
        await Assert.That(captured.Value.enabled).IsTrue();
    }

    [Test]
    public async Task GoButton_Fires_OnNavigate_WithTheSameRoute()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        SignalkRoute? captured = null;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnNavigate, EventCallback.Factory.Create<SignalkRoute>(
                this, r => captured = r)));
        cut.Find(".section-toggle").Click();

        cut.FindAll(".route-action-btn")[0].Click();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("r1");
    }

    [Test]
    public async Task EditButton_Fires_OnEdit_WithTheSameRoute()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
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
        var (ctx, confirm) = Context();
        using var _ctx = ctx;
        confirm.AutoConfirm = true;
        SignalkRoute? captured = null;
        var cut = ctx.RenderComponent<RoutesSection>(p => p
            .Add(x => x.Routes, new[] { Route("r1", "R1") })
            .Add(x => x.OnDelete, EventCallback.Factory.Create<SignalkRoute>(
                this, r => captured = r)));
        cut.Find(".section-toggle").Click();

        cut.FindAll(".route-action-btn")[2].Click();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("r1");
        await Assert.That(confirm.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteButton_ConfirmDeclined_DoesNotFire_OnDelete()
    {
        var (ctx, confirm) = Context();
        using var _ctx = ctx;
        confirm.AutoConfirm = false;
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
