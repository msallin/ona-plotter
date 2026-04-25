using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Coverage for the dual-flavour <see cref="ConfirmationService"/>:
/// ConfirmAsync (yes/no) and PromptAsync (text input) share the same
/// modal host but resolve different return types and have to handle
/// cross-flavour cancellation cleanly. These tests pin the contract
/// the dialog and call sites depend on, so a future refactor that
/// splits the service into two interfaces still preserves behaviour.
/// </summary>
public class ConfirmationServiceTests
{
    [Test]
    public async Task ConfirmAsync_Resolve_True()
    {
        var svc = new ConfirmationService();
        var task = svc.ConfirmAsync("Delete file?");
        await Assert.That(svc.IsPending).IsTrue();
        await Assert.That(svc.IsTextPrompt).IsFalse();
        await Assert.That(svc.Message).IsEqualTo("Delete file?");

        svc.Resolve(true);

        await Assert.That(await task).IsTrue();
        await Assert.That(svc.IsPending).IsFalse();
        await Assert.That(svc.Message).IsEqualTo("");
    }

    [Test]
    public async Task ConfirmAsync_Resolve_False()
    {
        var svc = new ConfirmationService();
        var task = svc.ConfirmAsync("Discard?");
        svc.Resolve(false);
        await Assert.That(await task).IsFalse();
    }

    [Test]
    public async Task PromptAsync_OK_ReturnsTrimmedText()
    {
        var svc = new ConfirmationService();
        var task = svc.PromptAsync("Rename to?", initialValue: "old");
        await Assert.That(svc.IsTextPrompt).IsTrue();
        await Assert.That(svc.TextValue).IsEqualTo("old");

        // Simulate the dialog binding the user's edit through.
        svc.TextValue = "  new name  ";
        svc.Resolve(true);

        await Assert.That(await task).IsEqualTo("new name");
    }

    [Test]
    public async Task PromptAsync_Cancel_ReturnsNull()
    {
        var svc = new ConfirmationService();
        var task = svc.PromptAsync("Rename to?", "old");
        svc.TextValue = "anything";
        svc.Resolve(false);
        await Assert.That(await task).IsNull();
    }

    [Test]
    public async Task PromptAsync_OK_WhitespaceOnly_ReturnsNull()
    {
        // Whitespace-only input should NOT round-trip as an empty
        // rename. Service strips it and returns null so callers can
        // use `is null` to mean "no valid answer". Regression check
        // for the rename dialog where blank trimmed input must not
        // clobber the existing name.
        var svc = new ConfirmationService();
        var task = svc.PromptAsync("Rename to?", "old");
        svc.TextValue = "   ";
        svc.Resolve(true);
        await Assert.That(await task).IsNull();
    }

    [Test]
    public async Task PromptAsync_OK_EmptyText_ReturnsNull()
    {
        var svc = new ConfirmationService();
        var task = svc.PromptAsync("Rename to?", "old");
        svc.TextValue = "";
        svc.Resolve(true);
        await Assert.That(await task).IsNull();
    }

    [Test]
    public async Task NewPrompt_While_Confirm_Pending_Cancels_Confirm()
    {
        // Cross-flavour cancellation contract: starting a new prompt
        // while a different one is pending must resolve the prior
        // task with the safer answer (false / null), never leave it
        // dangling and never deliver someone else's value.
        var svc = new ConfirmationService();
        var firstTask = svc.ConfirmAsync("first?");
        var secondTask = svc.PromptAsync("second?", "x");

        // First confirm should now have resolved to false.
        await Assert.That(firstTask.IsCompleted).IsTrue();
        await Assert.That(await firstTask).IsFalse();

        svc.TextValue = "y";
        svc.Resolve(true);
        await Assert.That(await secondTask).IsEqualTo("y");
    }

    [Test]
    public async Task NewConfirm_While_Prompt_Pending_Cancels_Prompt()
    {
        var svc = new ConfirmationService();
        var firstTask = svc.PromptAsync("rename?", "x");
        var secondTask = svc.ConfirmAsync("delete?");

        await Assert.That(firstTask.IsCompleted).IsTrue();
        await Assert.That(await firstTask).IsNull();

        svc.Resolve(true);
        await Assert.That(await secondTask).IsTrue();
    }

    [Test]
    public async Task OnChanged_Fires_On_Open_And_Resolve()
    {
        var svc = new ConfirmationService();
        int calls = 0;
        svc.OnChanged += () => calls++;

        var task = svc.ConfirmAsync("?");
        svc.Resolve(true);
        await task;

        // Open + resolve = 2 notifications. The dialog host re-renders
        // on each so the modal mounts and unmounts cleanly.
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ConfirmAsync_Destructive_Default_True()
    {
        // Destructive defaulting to true matches the historical
        // "every confirm is a delete" usage; explicit false is for
        // non-destructive prompts ("Save changes?" etc.).
        var svc = new ConfirmationService();
        var task = svc.ConfirmAsync("delete?");
        await Assert.That(svc.Destructive).IsTrue();
        svc.Resolve(false);
        await task;
    }

    [Test]
    public async Task PromptAsync_Always_NonDestructive()
    {
        // Text prompts use the primary palette because rename / edit
        // is almost never destructive; the service forces this so
        // call sites can't accidentally pass destructive: true and
        // get a red OK button on a rename dialog.
        var svc = new ConfirmationService();
        var task = svc.PromptAsync("rename?", "x");
        await Assert.That(svc.Destructive).IsFalse();
        svc.Resolve(false);
        await task;
    }

    [Test]
    public async Task ConfirmAsync_Custom_Labels_Surface_To_UI()
    {
        // The dialog host reads ConfirmLabel / CancelLabel directly.
        // Verbs match the question are the recommended pattern
        // ("Discard / Keep editing" instead of "Confirm / Cancel")
        // so the service stores them verbatim.
        var svc = new ConfirmationService();
        var task = svc.ConfirmAsync("Discard?", confirmLabel: "Discard", cancelLabel: "Keep editing");
        await Assert.That(svc.ConfirmLabel).IsEqualTo("Discard");
        await Assert.That(svc.CancelLabel).IsEqualTo("Keep editing");
        svc.Resolve(false);
        await task;
    }
}
