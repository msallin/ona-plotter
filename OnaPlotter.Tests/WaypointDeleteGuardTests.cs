using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class WaypointDeleteGuardTests
{
    private static SignalkWaypoint Wp(bool isMob = false) => new() { Id = "w1", IsMob = isMob };

    [Test]
    public async Task OrdinaryWaypoint_InBothCopies_IsAllowed()
    {
        await Assert.That(WaypointDeleteGuard.Check(Wp(), Wp())).IsEqualTo(WaypointDeleteVerdict.Allowed);
    }

    [Test]
    public async Task LocalCopyIsMob_IsRefused()
    {
        await Assert.That(WaypointDeleteGuard.Check(Wp(true), Wp())).IsEqualTo(WaypointDeleteVerdict.IsMob);
        await Assert.That(WaypointDeleteGuard.Check(Wp(true), null)).IsEqualTo(WaypointDeleteVerdict.IsMob);
    }

    [Test]
    public async Task StaleLocalCopy_StoreSaysMob_IsRefused()
    {
        // The delta flipped the waypoint to MOB; the page copy has not drained yet.
        await Assert.That(WaypointDeleteGuard.Check(Wp(false), Wp(true))).IsEqualTo(WaypointDeleteVerdict.IsMob);
        await Assert.That(WaypointDeleteGuard.Check(null, Wp(true))).IsEqualTo(WaypointDeleteVerdict.IsMob);
    }

    [Test]
    public async Task UnknownEverywhere_IsRefused()
    {
        await Assert.That(WaypointDeleteGuard.Check(null, null)).IsEqualTo(WaypointDeleteVerdict.Unknown);
    }

    [Test]
    public async Task KnownToOnlyOneSide_AndNotMob_IsAllowed()
    {
        await Assert.That(WaypointDeleteGuard.Check(Wp(), null)).IsEqualTo(WaypointDeleteVerdict.Allowed);
        await Assert.That(WaypointDeleteGuard.Check(null, Wp())).IsEqualTo(WaypointDeleteVerdict.Allowed);
    }
}
