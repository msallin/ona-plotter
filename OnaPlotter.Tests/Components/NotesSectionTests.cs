using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit coverage for the Notes section in the Layers panel. After
/// the bbox-filter rework the section no longer hosts a global
/// "Show" toggle; relevance is driven by the parent's bbox-filtered
/// list. The row buttons (Focus / Go / Edit / Delete) all flow into
/// EventCallbacks the Map page wires up. Tests pin the wiring + the
/// IConfirmationService-driven Edit and Delete confirmation flows.
/// </summary>
public class NotesSectionTests
{
    private static SignalkNote Note(string id, string title, string? desc = null,
        double lat = 47.4, double lon = 8.5)
    {
        var n = new SignalkNote
        {
            Id = id,
            Title = title,
            Description = desc,
            Position = new NotePosition { Latitude = lat, Longitude = lon },
        };
        return n;
    }

    /// <summary>Test scaffolding: register a fresh
    /// <see cref="FakeConfirmationService"/> in DI so the
    /// section's @inject resolves. Mirrors WaypointsSectionTests'
    /// Context helper so a future change to either section's
    /// confirmation flow doesn't drift between the two.</summary>
    private static (Bunit.TestContext ctx, FakeConfirmationService confirm) Context()
    {
        var ctx = new Bunit.TestContext();
        var confirm = new FakeConfirmationService();
        ctx.Services.AddSingleton<IConfirmationService>(confirm);
        return (ctx, confirm);
    }

    // Sections render collapsed by default (UX principle: panel opens
    // compact). Every test below that asserts on inner content first
    // clicks the header to expand.
    private static void Expand(IRenderedComponent<NotesSection> cut) =>
        cut.Find(".section-toggle").Click();

    [Test]
    public async Task EmptyList_SectionHidden()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, Array.Empty<SignalkNote>()));

        // The SectionHeader markup includes the word "Notes"; with 0
        // notes the entire section returns, so no header should render.
        await Assert.That(cut.Markup).DoesNotContain("Notes");
    }

    [Test]
    public async Task NotesPresent_RendersTitleAndCount()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp patch"), Note("n2", "Reef") }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("Kelp patch");
        await Assert.That(cut.Markup).Contains("Reef");
        // Section count badge shows the list length.
        await Assert.That(cut.Markup).Contains(">2<");
    }

    [Test]
    public async Task UntitledNote_ShownAsPlaceholder()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "") }));
        Expand(cut);

        await Assert.That(cut.Markup).Contains("(untitled)");
    }

    [Test]
    public async Task DescriptionTruncated_ToSixtyChars()
    {
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var longDesc = new string('x', 80);
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Title", desc: longDesc) }));
        Expand(cut);

        // The 80-char description should be truncated to 60 chars + "..."
        await Assert.That(cut.Markup).Contains(new string('x', 60) + "...");
        // Full 80-char run must NOT appear.
        await Assert.That(cut.Markup).DoesNotContain(new string('x', 80));
    }

    [Test]
    public async Task FocusButton_InvokesCallback()
    {
        SignalkNote? focused = null;
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp") })
            .Add(x => x.OnFocus, EventCallback.Factory.Create<SignalkNote>(
                this, n => focused = n)));
        Expand(cut);

        // Focus is the first action button (Focus / Go / Edit /
        // Delete order). The CrudSection scrollable rows render
        // buttons inside .crud-row; we pin the BUTTON title rather
        // than relying on order so a future palette tweak (e.g.
        // re-shuffling Focus to second position) doesn't false-fail.
        cut.FindAll("button[title^='Center the map']")[0].Click();

        await Assert.That(focused).IsNotNull();
        await Assert.That(focused!.Id).IsEqualTo("n1");
    }

    [Test]
    public async Task GoButton_InvokesNavigateCallback()
    {
        // The Go button feeds the Map page's NavigateToNote, which
        // PUTs the note's lat/lon as the SignalK course destination.
        // Tested in isolation here - the section just bubbles the
        // SignalkNote up. End-to-end coverage of the destination
        // PUT lives in the (future) NavigateToNote tests on the
        // page.
        SignalkNote? navigated = null;
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Reef") })
            .Add(x => x.OnNavigate, EventCallback.Factory.Create<SignalkNote>(
                this, n => navigated = n)));
        Expand(cut);

        cut.FindAll("button[title^='Set this note']")[0].Click();

        await Assert.That(navigated).IsNotNull();
        await Assert.That(navigated!.Id).IsEqualTo("n1");
    }

    [Test]
    public async Task EditButton_Click_Fires_OnEdit_WithNote()
    {
        // Replaces the older PromptAsync-based EditButton tests:
        // the layers-panel Edit button no longer surfaces a prompt;
        // it raises OnEdit which the parent wires to open the
        // create-dialog pre-filled with title + description. This
        // test pins the new contract so a future drift back to a
        // prompt-based flow surfaces here first.
        SignalkNote? captured = null;
        var (ctx, _) = Context();
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp") })
            .Add(x => x.OnEdit, EventCallback.Factory.Create<SignalkNote>(
                this, n => captured = n)));
        Expand(cut);

        cut.FindAll("button[title^='Edit title']")[0].Click();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Id).IsEqualTo("n1");
        await Assert.That(captured.Title).IsEqualTo("Kelp");
    }

    [Test]
    public async Task DeleteButton_FiresAfterConfirm()
    {
        SignalkNote? deleted = null;
        var (ctx, confirm) = Context();
        using var _ctx = ctx;
        confirm.AutoConfirm = true;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp") })
            .Add(x => x.OnDelete, EventCallback.Factory.Create<SignalkNote>(
                this, n => deleted = n)));
        Expand(cut);

        cut.FindAll("button[title='Delete note']")[0].Click();
        await Task.Delay(10);

        await Assert.That(deleted).IsNotNull();
        await Assert.That(deleted!.Id).IsEqualTo("n1");
    }

    [Test]
    public async Task DeleteButton_DeclinedConfirm_DoesNothing()
    {
        SignalkNote? deleted = null;
        var (ctx, confirm) = Context();
        using var _ctx = ctx;
        confirm.AutoConfirm = false;
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp") })
            .Add(x => x.OnDelete, EventCallback.Factory.Create<SignalkNote>(
                this, n => deleted = n)));
        Expand(cut);

        cut.FindAll("button[title='Delete note']")[0].Click();
        await Task.Delay(10);

        await Assert.That(deleted).IsNull();
    }
}
