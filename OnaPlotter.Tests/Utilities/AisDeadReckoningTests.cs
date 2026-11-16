using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests.Utilities;

/// <summary>
/// Pins <see cref="AisDeadReckoning.PositionAt"/>: CPA must start from
/// where the target is now, not where its last AIS report put it, and
/// must not invent a position from a fix too old to extrapolate.
/// </summary>
public class AisDeadReckoningTests
{
    private static readonly DateTime Now = TestClock.FixedUtcNow;
    private const double MetresPerDegLat = 111_195.0;

    private static AisVessel Target(double? sogMs = 5.0, double? cogRad = 0.0, TimeSpan? age = null)
    {
        var v = new AisVessel("vessels.x")
        {
            Latitude = 47.0,
            Longitude = 8.0,
            SpeedOverGround = sogMs,
            CourseOverGround = cogRad,
        };
        if (age is TimeSpan a) v.PositionUpdatedUtc = Now - a;
        return v;
    }

    [Test]
    public async Task FreshFix_ReturnsFixUnchanged()
    {
        var p = AisDeadReckoning.PositionAt(Target(age: TimeSpan.Zero), Now);
        await Assert.That(p!.Value.Latitude).IsEqualTo(47.0);
        await Assert.That(p.Value.Longitude).IsEqualTo(8.0);
    }

    [Test]
    public async Task OneMinuteOld_AdvancesAlongCog()
    {
        // 5 m/s due north for 60 s -> 300 m north.
        var p = AisDeadReckoning.PositionAt(Target(age: TimeSpan.FromSeconds(60)), Now);
        double movedM = (p!.Value.Latitude - 47.0) * MetresPerDegLat;
        await Assert.That(movedM).IsEqualTo(300.0).Within(1.0);
        await Assert.That(p.Value.Longitude).IsEqualTo(8.0).Within(1e-9);
    }

    [Test]
    public async Task AtCap_StillProjects_BeyondCap_IsStale()
    {
        var atCap = AisDeadReckoning.PositionAt(Target(age: AisDeadReckoning.MaxProjectionAge), Now);
        var beyond = AisDeadReckoning.PositionAt(
            Target(age: AisDeadReckoning.MaxProjectionAge + TimeSpan.FromSeconds(1)), Now);
        await Assert.That(atCap).IsNotNull();
        await Assert.That(beyond).IsNull();
    }

    [Test]
    public async Task UnknownFixTime_ReturnsFixUnchanged()
    {
        // Radar ARPA targets and hand-built fixtures carry no fix time;
        // treat the position as current rather than dropping the target.
        var p = AisDeadReckoning.PositionAt(Target(age: null), Now);
        await Assert.That(p!.Value.Latitude).IsEqualTo(47.0);
    }

    [Test]
    public async Task FutureFixTime_ClampsToZeroAge()
    {
        var p = AisDeadReckoning.PositionAt(Target(age: TimeSpan.FromSeconds(-30)), Now);
        await Assert.That(p!.Value.Latitude).IsEqualTo(47.0);
    }

    [Test]
    [Arguments(null, 0.0)]
    [Arguments(5.0, null)]
    public async Task MissingMotion_ReturnsFixUnchanged(double? sog, double? cog)
    {
        var p = AisDeadReckoning.PositionAt(Target(sog, cog, TimeSpan.FromSeconds(60)), Now);
        await Assert.That(p!.Value.Latitude).IsEqualTo(47.0);
    }

    [Test]
    public async Task NoPosition_ReturnsNull()
    {
        var v = new AisVessel("vessels.x") { PositionUpdatedUtc = Now };
        await Assert.That(AisDeadReckoning.PositionAt(v, Now)).IsNull();
    }

    [Test]
    public async Task ApplyPosition_StampsFixTime_OtherPathsDoNot()
    {
        var v = new AisVessel("vessels.x");
        v.Apply("navigation.speedOverGround", 3.0);
        await Assert.That(v.PositionUpdatedUtc).IsNull();

        var before = DateTime.UtcNow;
        v.Apply("navigation.position",
            JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 }));
        await Assert.That(v.PositionUpdatedUtc).IsNotNull();
        await Assert.That(v.PositionUpdatedUtc!.Value).IsGreaterThanOrEqualTo(before);
    }
}
