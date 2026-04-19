using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit coverage for the Regions section in the Layers panel. Mirrors
/// NotesSectionTests since the two components share a shape. Pins
/// specifically on the behaviour users will notice if it breaks:
/// Focus button reaches C#, Show toggle reaches C#.
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

    // Sections start collapsed; expand before asserting on inner content.
    private static void Expand(IRenderedComponent<RegionsSection> cut) =>
        cut.Find(".section-toggle").Click();

    [Test]
    public async Task EmptyList_SectionHidden()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<RegionsSection>(p => p
            .Add(x => x.Regions, Array.Empty<SignalkRegion>()));
        await Assert.That(cut.Markup).DoesNotContain("Regions");
    }

    [Test]
    public async Task MultipleRegions_RenderedWithCount()
    {
        using var ctx = new Bunit.TestContext();
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

    [Test]
    public async Task FocusButton_InvokesCallback()
    {
        SignalkRegion? focused = null;
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<RegionsSection>(p => p
            .Add(x => x.Regions, new[] { Region("r1", "Anchorage") })
            .Add(x => x.OnFocus, EventCallback.Factory.Create<SignalkRegion>(
                this, r => focused = r)));
        Expand(cut);

        cut.Find("button.map-btn").Click();

        await Assert.That(focused).IsNotNull();
        await Assert.That(focused!.Id).IsEqualTo("r1");
    }

    [Test]
    public async Task VisibleToggle_InvokesCallback()
    {
        bool? received = null;
        using var ctx = new Bunit.TestContext();
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
