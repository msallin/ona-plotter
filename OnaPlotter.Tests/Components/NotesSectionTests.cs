using Bunit;
using Microsoft.AspNetCore.Components;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit coverage for the Notes section in the Layers panel. Pins:
/// list rendering, Focus callback wiring, Show-toggle wiring, and
/// the empty-state (section hides itself entirely when no notes).
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

    [Test]
    public async Task EmptyList_SectionHidden()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, Array.Empty<SignalkNote>()));

        // The SectionHeader markup includes the word "Notes"; with 0
        // notes the entire section returns, so no header should render.
        await Assert.That(cut.Markup).DoesNotContain("Notes");
    }

    [Test]
    public async Task NotesPresent_RendersTitleAndCount()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp patch"), Note("n2", "Reef") }));

        await Assert.That(cut.Markup).Contains("Kelp patch");
        await Assert.That(cut.Markup).Contains("Reef");
        // Section count badge shows the list length.
        await Assert.That(cut.Markup).Contains(">2<");
    }

    [Test]
    public async Task UntitledNote_ShownAsPlaceholder()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "") }));

        await Assert.That(cut.Markup).Contains("(untitled)");
    }

    [Test]
    public async Task DescriptionTruncated_ToSixtyChars()
    {
        var longDesc = new string('x', 80);
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Title", desc: longDesc) }));

        // The 80-char description should be truncated to 60 chars + "..."
        await Assert.That(cut.Markup).Contains(new string('x', 60) + "...");
        // Full 80-char run must NOT appear.
        await Assert.That(cut.Markup).DoesNotContain(new string('x', 80));
    }

    [Test]
    public async Task FocusButton_InvokesCallback()
    {
        SignalkNote? focused = null;
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp") })
            .Add(x => x.OnFocus, EventCallback.Factory.Create<SignalkNote>(
                this, n => focused = n)));

        cut.Find("button.map-btn").Click();

        await Assert.That(focused).IsNotNull();
        await Assert.That(focused!.Id).IsEqualTo("n1");
    }

    [Test]
    public async Task VisibleToggle_InvokesCallback_WithToggledValue()
    {
        bool? received = null;
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<NotesSection>(p => p
            .Add(x => x.Notes, new[] { Note("n1", "Kelp") })
            .Add(x => x.Visible, true)
            .Add(x => x.OnToggleVisible, EventCallback.Factory.Create<bool>(
                this, v => received = v)));

        // Clicking the checkbox (currently checked=true) fires with the new
        // value. bUnit's Change event simulates the browser's change flow.
        var checkbox = cut.Find("input[type=checkbox]");
        await checkbox.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });

        await Assert.That(received).IsFalse();
    }
}
