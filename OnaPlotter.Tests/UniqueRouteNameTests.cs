using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the route-name disambiguation rule used when the Add Route
/// flow auto-suggests "Route yyyyMMdd". Helms create a handful of
/// routes per day; without a suffix the second one silently shares
/// the first's display name in the routes panel.
/// </summary>
public class UniqueRouteNameTests
{
    [Test]
    public async Task Suggest_NoCollision_ReturnsBaseName()
    {
        // Empty list: the base name is always free.
        var result = UniqueRouteName.Suggest("Route 20260427", []);
        await Assert.That(result).IsEqualTo("Route 20260427");

        // Populated list, no collision.
        var result2 = UniqueRouteName.Suggest(
            "Route 20260427", ["Route 20260101", "Crossing"]);
        await Assert.That(result2).IsEqualTo("Route 20260427");
    }

    [Test]
    public async Task Suggest_FirstCollision_AppendsTwo()
    {
        var result = UniqueRouteName.Suggest(
            "Route 20260427", ["Route 20260427"]);
        await Assert.That(result).IsEqualTo("Route 20260427 (2)");
    }

    [Test]
    public async Task Suggest_MultipleCollisions_AppendsNextFreeNumber()
    {
        var result = UniqueRouteName.Suggest(
            "Route 20260427",
            ["Route 20260427", "Route 20260427 (2)", "Route 20260427 (3)"]);
        await Assert.That(result).IsEqualTo("Route 20260427 (4)");
    }

    [Test]
    public async Task Suggest_GappyCollisions_FillsTheGap()
    {
        // (2) is taken but (3) isn't -- we don't need to walk past
        // existing higher numbers; the first free slot wins.
        var result = UniqueRouteName.Suggest(
            "Route 20260427",
            ["Route 20260427", "Route 20260427 (2)", "Route 20260427 (5)"]);
        await Assert.That(result).IsEqualTo("Route 20260427 (3)");
    }

    [Test]
    public async Task Suggest_CaseSensitive_TreatsDifferentCasingsAsDifferent()
    {
        // "Crossing" and "crossing" are legitimately different routes.
        var result = UniqueRouteName.Suggest(
            "crossing", ["Crossing"]);
        await Assert.That(result).IsEqualTo("crossing");
    }

    [Test]
    public async Task Suggest_NullOrEmptyInExistingList_AreIgnored()
    {
        // SignalK occasionally returns a route resource with no name
        // (deleted-mid-list, plugin bug). Those can't collide with
        // anything; the helper should treat them as "not present".
        var result = UniqueRouteName.Suggest(
            "Route 20260427",
            ["", null!, "Route 20260427"]);
        await Assert.That(result).IsEqualTo("Route 20260427 (2)");
    }

    [Test]
    public async Task Suggest_UserSuppliedSuffixedBaseName_StillDisambiguates()
    {
        // The helm renames a route to "Crossing (2)" by hand. If
        // another route with that exact name already exists, we still
        // produce a unique candidate. Reads odd ("Crossing (2) (2)")
        // but the user-typed-name path doesn't go through Suggest in
        // production -- this case only matters if someone wires the
        // helper into the manual path later.
        var result = UniqueRouteName.Suggest(
            "Crossing (2)", ["Crossing (2)"]);
        await Assert.That(result).IsEqualTo("Crossing (2) (2)");
    }
}
