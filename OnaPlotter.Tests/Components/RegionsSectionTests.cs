using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit coverage for the Regions section in the Layers panel. Mirrors
/// NotesSectionTests since the two components share a shape. Pins
/// specifically on the behaviour users will notice if it breaks:
/// Focus button reaches C#, Show toggle reaches C#, Delete goes
/// through the confirmation service.
///
/// RegionsSection injects <see cref="IConfirmationService"/> to gate
/// the Delete action; every test builds a context with a
/// <see cref="FakeConfirmationService"/> so the component can resolve
/// that dependency even in the tests that never click Delete.
/// </summary>
public class RegionsSectionTests
{
    private static SignalkRegion Region(string id, string name, string? desc = null)
    {
        var r = new SignalkRegion { Id = id, Name = name, Description = desc };
        // One trivial ring so the model looks real; the section doesn't
        // care about geometry, just about list rendering.
        r.OuterRings = new[] { new[] { new[] { 47.4, 8.5 }, new[] { 47.5, 8.5 }, new[] { 47.5, 8.6 } } };
        return r;
    }

    /// <summary>Builds a bUnit TestContext with a FakeConfirmationService
    /// registered so RegionsSection can inject it; returns both so tests
    /// that exercise Delete can flip AutoConfirm.</summary>
    private static (Bunit.TestContext ctx, FakeConfirmationService confirm) Context()
    {
        var ctx = new Bunit.TestContext();
        var confirm = new FakeConfirmationService();
        ctx.Services.AddSingleton<IConfirmationService>(confirm);
        return (ctx, confirm);
    }

    // Sections start collapsed; expand before asserting on inner content.
    private static void Expand(IRenderedComponent<RegionsSection> cut) =>
        cut.Find(".section-toggle").Click();

    [Test]
    public async Task EmptyList_SectionHidden()
    {
        var (ctx, _) = Context();
        using (ctx)
        {
            var cut = ctx.RenderComponent<RegionsSection>(p => p
                .Add(x => x.Regions, Array.Empty<SignalkRegion>()));
            await Assert.That(cut.Markup).DoesNotContain("Regions");
        }
    }

    [Test]
    public async Task MultipleRegions_RenderedWithCount()
    {
        var (ctx, _) = Context();
        using (ctx)
        {
            var cut = ctx.RenderComponent<RegionsSection>(p => p
                .Add(x => x.Regions, new[]
                {
                    Region("r1", "Anchorage"),
                    Region("r2", "No-go zone"),
                }));
            Expand(cut);

            await Assert.That(cut.Markup).Contains("Anchorage");
            await Assert.That(cut.Markup).Contains("No-go zone");
            await Assert.That(cut.Markup).Contains(">2<");
        }
    }

    [Test]
    public async Task FocusButton_InvokesCallback()
    {
        SignalkRegion? focused = null;
        var (ctx, _) = Context();
        using (ctx)
        {
            var cut = ctx.RenderComponent<RegionsSection>(p => p
                .Add(x => x.Regions, new[] { Region("r1", "Anchorage") })
                .Add(x => x.OnFocus, EventCallback.Factory.Create<SignalkRegion>(
                    this, r => focused = r)));
            Expand(cut);

            // First action button in the row is Focus (rendered order:
            // Focus, Edit, Delete).
            cut.FindAll("button.route-action-btn")[0].Click();

            await Assert.That(focused).IsNotNull();
            await Assert.That(focused!.Id).IsEqualTo("r1");
        }
    }

    [Test]
    public async Task DeleteButton_PromptsWithName_WhenSet()
    {
        // The Delete button should route through the confirmation
        // service with a label that helps the user identify what's
        // about to vanish. Name is preferred over Id.
        var (ctx, confirm) = Context();
        confirm.AutoConfirm = false; // don't fire OnDelete for this test
        using (ctx)
        {
            var cut = ctx.RenderComponent<RegionsSection>(p => p
                .Add(x => x.Regions, new[] { Region("r1", "Anchorage") }));
            Expand(cut);

            // Third action button per row (Focus / Edit / Delete).
            cut.FindAll("button.route-action-btn")[2].Click();

            await Assert.That(confirm.LastMessage).IsEqualTo("Delete region 'Anchorage'?");
        }
    }

    [Test]
    public async Task DeleteButton_PromptsWithId_WhenNameEmpty()
    {
        // No name -> fall through to the id, since ids are at least
        // user-recognisable on the SignalK Admin UI.
        var (ctx, confirm) = Context();
        confirm.AutoConfirm = false;
        using (ctx)
        {
            var cut = ctx.RenderComponent<RegionsSection>(p => p
                .Add(x => x.Regions, new[] { Region("r-without-name", name: "") }));
            Expand(cut);

            cut.FindAll("button.route-action-btn")[2].Click();

            await Assert.That(confirm.LastMessage).IsEqualTo("Delete region 'r-without-name'?");
        }
    }

    [Test]
    public async Task DeleteButton_PromptsWithUntitled_WhenBothEmpty()
    {
        // Defence-in-depth: malformed payload with neither Name nor
        // Id set should still produce a grammatical confirmation
        // dialog instead of "Delete region ''?".
        var (ctx, confirm) = Context();
        confirm.AutoConfirm = false;
        using (ctx)
        {
            var region = new SignalkRegion { Id = "", Name = "" };
            region.OuterRings = new[] { new[] { new[] { 47.4, 8.5 }, new[] { 47.5, 8.5 }, new[] { 47.5, 8.6 } } };

            var cut = ctx.RenderComponent<RegionsSection>(p => p
                .Add(x => x.Regions, new[] { region }));
            Expand(cut);

            cut.FindAll("button.route-action-btn")[2].Click();

            await Assert.That(confirm.LastMessage).IsEqualTo("Delete region '(untitled)'?");
        }
    }

    [Test]
    public async Task VisibleToggle_InvokesCallback()
    {
        bool? received = null;
        var (ctx, _) = Context();
        using (ctx)
        {
            var cut = ctx.RenderComponent<RegionsSection>(p => p
                .Add(x => x.Regions, new[] { Region("r1", "Anchorage") })
                .Add(x => x.Visible, true)
                .Add(x => x.OnToggleVisible, EventCallback.Factory.Create<bool>(
                    this, v => received = v)));

            var checkbox = cut.Find("input[type=checkbox]");
            await checkbox.ChangeAsync(new ChangeEventArgs { Value = false });

            await Assert.That(received).IsFalse();
        }
    }
}
