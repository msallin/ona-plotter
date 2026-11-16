using OnaPlotter.Components.Pages;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the Resources page's active-route banner re-render gate: the
/// snapshot changes exactly when something the banner shows changes.
/// </summary>
public class ResourcesBannerSnapshotTests
{
    private static Resources.ActiveRouteBannerSnapshot Snap(
        bool active = true, string? href = "/resources/routes/abc", string? name = "Solent",
        int? idx = 2, int? total = 8, double? metres = 1852.0)
        => Resources.BannerSnapshot(active, href, name, idx, total, metres);

    [Test]
    public async Task NoActiveCourse_IgnoresEveryOtherField()
    {
        // A stale href / distance left over after Stop must not keep the
        // gate re-rendering an already hidden banner.
        await Assert.That(Snap(active: false)).IsEqualTo(Snap(active: false, href: null, metres: 99));
        await Assert.That(Snap(active: false)).IsEqualTo(default(Resources.ActiveRouteBannerSnapshot));
    }

    [Test]
    public async Task SameDisplayedDistance_IsEqual()
    {
        // 1852 m and 1855 m both render "1.00 nm to next".
        await Assert.That(Snap(metres: 1852)).IsEqualTo(Snap(metres: 1855));
    }

    [Test]
    public async Task DisplayedDistanceChange_IsDifferent()
    {
        // 0.01 nm step = 18.5 m: one more visible tick on the banner.
        await Assert.That(Snap(metres: 1852)).IsNotEqualTo(Snap(metres: 1852 + 18.6));
    }

    [Test]
    public async Task RouteActivatedOrStopped_IsDifferent()
    {
        await Assert.That(Snap(active: true)).IsNotEqualTo(Snap(active: false));
    }

    [Test]
    public async Task HrefNameOrProgressChange_IsDifferent()
    {
        await Assert.That(Snap()).IsNotEqualTo(Snap(href: "/resources/routes/xyz"));
        await Assert.That(Snap()).IsNotEqualTo(Snap(name: "Needles"));
        await Assert.That(Snap()).IsNotEqualTo(Snap(idx: 3));
        await Assert.That(Snap()).IsNotEqualTo(Snap(total: 9));
    }

    [Test]
    public async Task DistanceAppearingOrDisappearing_IsDifferent()
    {
        await Assert.That(Snap(metres: null)).IsNotEqualTo(Snap(metres: 0));
    }
}
