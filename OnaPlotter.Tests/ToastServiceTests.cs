using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the toast service's added contracts: dedup of repeated
/// (message, level) pairs, pinned non-auto-dismiss flow, and the
/// LogException helper that keeps raw exception strings out of the
/// helm-facing UI. The pre-existing Show / Success / etc. behaviour
/// is exercised indirectly through these tests; if a regression
/// drops them the dedup test fails first.
/// </summary>
public class ToastServiceTests
{
    [Test]
    public async Task Show_DedupesIdenticalActiveToasts_RefreshesExpiry()
    {
        // A flaky transport firing the same "Failed to fetch" 5 times
        // in 200 ms must not pile up. Same (message, level) -> the
        // existing toast's expiry is bumped, no second card appears.
        var svc = new ToastService();

        svc.Show("Failed to fetch", ToastLevel.Error, durationSec: 6);
        var firstExpiry = svc.Active[0].ExpiresAt;
        await Task.Delay(20);
        svc.Show("Failed to fetch", ToastLevel.Error, durationSec: 6);

        await Assert.That(svc.Active.Count).IsEqualTo(1);
        await Assert.That(svc.Active[0].ExpiresAt).IsGreaterThan(firstExpiry);
    }

    [Test]
    public async Task Show_DifferentMessages_StackNormally()
    {
        // Dedup is keyed on (message, level); a different message
        // (or the same message at a different level) must coexist.
        var svc = new ToastService();

        svc.Show("Save route failed");
        svc.Show("Saved");
        svc.Show("Save route failed", ToastLevel.Error);   // same text, different level

        await Assert.That(svc.Active.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Show_SkipsDedupForActionToasts()
    {
        // Action toasts (with an Undo / Next-WP button) are dedup-
        // ineligible because the action callback is bound to a
        // specific moment-in-time state; a refresh would silently
        // swap the helm's "Undo" target. Pinned by the
        // ActionLabel-is-null branch in the dedup test inside Show.
        var svc = new ToastService();

        svc.ShowAction("Discarded edit", "Undo", () => Task.CompletedTask);
        svc.ShowAction("Discarded edit", "Undo", () => Task.CompletedTask);

        await Assert.That(svc.Active.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Pinned_DoesNotAutoDismiss()
    {
        // The MOB read-back use case: helm reads lat/lon aloud over
        // VHF; a 30 s timer dropping the card mid-sentence is the
        // bug we're fixing. Pinned toasts must still be active a
        // second after creation (any reasonable duration after).
        var svc = new ToastService();

        var id = svc.Pinned("MOB! 47.123 N 8.456 E", ToastLevel.Error);
        await Task.Delay(50);

        await Assert.That(svc.Active.Count).IsEqualTo(1);
        await Assert.That(svc.Active[0].Id).IsEqualTo(id);
        await Assert.That(svc.Active[0].IsPinned).IsTrue();
    }

    [Test]
    public async Task Pinned_DismissedExplicitly_Removes()
    {
        // The caller's responsibility: when the underlying state
        // clears (helm tapped MOB-recovered), explicitly Dismiss
        // the pinned toast.
        var svc = new ToastService();

        var id = svc.Pinned("Pinned card", ToastLevel.Info);
        await Assert.That(svc.Active.Count).IsEqualTo(1);

        svc.Dismiss(id);

        await Assert.That(svc.Active.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LogException_ShowsGenericToast_DoesNotLeakExceptionMessage()
    {
        // The contract: ex.Message must never reach the helm-facing
        // toast text. Devs see the trace in the browser console;
        // helms see "{action} failed -- check the browser console".
        var svc = new ToastService();
        var ex = new InvalidOperationException("internal-server-only details");

        svc.LogException(ex, "Save route");

        await Assert.That(svc.Active.Count).IsEqualTo(1);
        var toast = svc.Active[0];
        await Assert.That(toast.Level).IsEqualTo(ToastLevel.Error);
        await Assert.That(toast.Message).Contains("Save route");
        await Assert.That(toast.Message)
            .DoesNotContain("internal-server-only details")
            .Because("ex.Message must NEVER leak into the helm-facing toast");
        await Assert.That(toast.Message)
            .Contains("browser console")
            .Because("helm needs to know where to look if they want details");
    }

    [Test]
    public async Task LogException_DedupesAcrossRepeatedCalls()
    {
        // Same action firing repeatedly (e.g. a polling fetch that
        // keeps failing) must dedup like ordinary Show. The toast
        // text is generated identically by the helper so the
        // (message, level) tuple is stable.
        var svc = new ToastService();
        var ex = new InvalidOperationException("oops");

        svc.LogException(ex, "Fetch chart list");
        svc.LogException(ex, "Fetch chart list");
        svc.LogException(ex, "Fetch chart list");

        await Assert.That(svc.Active.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Pinned_NotDeduped_AllowsMultiplePinnedCards()
    {
        // Pinned toasts skip the dedup path. A simultaneous MOB +
        // anchor-drag scenario needs both pinned cards visible.
        var svc = new ToastService();

        svc.Pinned("Same text", ToastLevel.Error);
        svc.Pinned("Same text", ToastLevel.Error);

        await Assert.That(svc.Active.Count).IsEqualTo(2);
    }
}
