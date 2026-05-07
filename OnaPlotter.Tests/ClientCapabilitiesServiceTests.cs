using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the slow-client heuristic that drives Leaflet's preferCanvas,
/// detectRetina, AIS-during-drag gating. The pure rule moved out of
/// <c>leafletInterop.js::initMap</c> into
/// <see cref="ClientCapabilitiesService.ResolveIsSlowClient"/>; these
/// tests guard against accidental regressions when the regex or core
/// threshold gets tweaked.
/// </summary>
public class ClientCapabilitiesServiceTests
{
    [Test]
    public async Task DesktopClass_8Cores_X86_IsFast()
    {
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(
            8, "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) ...");
        await Assert.That(slow).IsFalse();
    }

    [Test]
    public async Task FewCores_TriggersSlow()
    {
        // Pi 4 reports 4 cores - the boundary is "<= 4" so this lands
        // on the slow side of the gate.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(
            4, "Mozilla/5.0 (X11; Linux x86_64) ...");
        await Assert.That(slow).IsTrue();
    }

    [Test]
    public async Task ArmUserAgent_TriggersSlow_EvenWithManyCores()
    {
        // ARM signal alone is enough - a hypothetical 8-core ARM
        // tablet should still get the slow-path treatment because the
        // bottleneck on those devices is GPU compositing, not core
        // count, and the JS perf gates were tuned around that.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(
            8, "Mozilla/5.0 (Linux; armv7l) ...");
        await Assert.That(slow).IsTrue();
    }

    [Test]
    public async Task RaspberryUserAgent_TriggersSlow()
    {
        // The string "raspberry" appears in some Raspbian Chromium
        // builds; the regex matches case-insensitively so a UA like
        // "Raspbian armv7l" (which has both signals) is also fine.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(
            8, "Chromium/116.0 Raspberry Pi OS");
        await Assert.That(slow).IsTrue();
    }

    [Test]
    public async Task ZeroCores_NoUa_IsFast()
    {
        // Defensive: if both probes return zero / empty (e.g. browser
        // strips navigator for privacy), the heuristic falls through
        // to the desktop-class default.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(0, "");
        await Assert.That(slow).IsFalse();
    }

    [Test]
    public async Task NullUa_HighCores_IsFast()
    {
        // Null UA must not throw; treat as "no signal" and fall back
        // to the cores-only path.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(8, null);
        await Assert.That(slow).IsFalse();
    }

    [Test]
    public async Task FiveCores_IsFast()
    {
        // Boundary check the other way: 5 cores == not slow. Some
        // mobile chipsets cluster 4+4 big.LITTLE; the helm doesn't
        // benefit from forcing canvas on those.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(
            5, "Mozilla/5.0 (Linux; Android 13)");
        await Assert.That(slow).IsFalse();
    }

    [Test]
    public async Task ArmInAnUnrelatedWord_DoesNotMatch()
    {
        // "alarm" / "harmful" contain "arm" as a substring - the regex
        // uses \barm\b so unrelated words don't false-positive.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(
            8, "Mozilla/5.0 (Windows; harmful-extension/1.0)");
        await Assert.That(slow).IsFalse();
    }

    [Test]
    public async Task CaseInsensitiveArm()
    {
        // ARM in caps shows up in some embedded UAs; the regex must
        // match case-insensitively.
        bool slow = ClientCapabilitiesService.ResolveIsSlowClient(
            8, "Mozilla/5.0 (Linux; ARM; Tizen/4.0)");
        await Assert.That(slow).IsTrue();
    }
}
