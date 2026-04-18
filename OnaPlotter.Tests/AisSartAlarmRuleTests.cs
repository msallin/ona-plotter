using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class AisSartAlarmRuleTests
{
    // --- AisSart classifier --------------------------------------------

    [Test]
    [Arguments("970123456", "SART")]
    [Arguments("972987654", "MOB")]
    [Arguments("974111222", "EPIRB")]
    public async Task Category_DistressRanges(string mmsi, string expected)
        => await Assert.That(AisSart.Category(mmsi)).IsEqualTo(expected);

    [Test]
    [Arguments("211234567")]     // German flag (21x) -- normal vessel
    [Arguments("123456789")]     // no known prefix
    [Arguments("971234567")]     // 971 is reserved but not one of the three we watch
    [Arguments("979999999")]     // 979 ditto
    [Arguments("")]              // empty
    [Arguments("97012345")]      // only 8 digits
    [Arguments("9701234567")]    // 10 digits
    public async Task Category_NonDistress_ReturnsNull(string mmsi)
        => await Assert.That(AisSart.Category(mmsi)).IsNull();

    [Test]
    public async Task Category_Null_ReturnsNull()
        => await Assert.That(AisSart.Category(null)).IsNull();

    [Test]
    public async Task CategoryFromAny_UsesContextWhenMmsiMissing()
    {
        // When the MMSI delta hasn't landed yet, fall back to extracting
        // from the context string. This matters because position often
        // arrives before the MMSI, so we want SART detection to kick in
        // on the first position fix, not on the second delta.
        var cat = AisSart.CategoryFromAny(null, "vessels.urn:mrn:imo:mmsi:972111222");
        await Assert.That(cat).IsEqualTo("MOB");
    }

    // --- Alarm rule ----------------------------------------------------

    private static AisVessel MakeVessel(string context, string? mmsi, double? lat = null, double? lon = null)
    {
        var v = new AisVessel(context);
        v.Mmsi = mmsi;
        if (lat is not null) v.Latitude = lat;
        if (lon is not null) v.Longitude = lon;
        return v;
    }

    private static AlarmEvaluationContext Ctx(IReadOnlyCollection<AisVessel> vessels,
        double? ownLat = null, double? ownLon = null)
    {
        var nav = new NavigationData();
        if (ownLat is not null && ownLon is not null) nav.ApplyPosition(ownLat.Value, ownLon.Value);
        return new AlarmEvaluationContext(nav, vessels, new FakeSettings(), DateTime.UtcNow, _ => false);
    }

    [Test]
    public async Task NoVessels_NoAlarm()
    {
        var rule = new AisSartAlarmRule();
        await Assert.That(rule.Check(Ctx([]))).IsNull();
    }

    [Test]
    public async Task OnlyRegularVessels_NoAlarm()
    {
        var rule = new AisSartAlarmRule();
        var vessels = new[]
        {
            MakeVessel("vessels.urn:mrn:imo:mmsi:211234567", "211234567", 47.5, 8.5),
            MakeVessel("vessels.urn:mrn:imo:mmsi:538123456", "538123456", 47.6, 8.6),
        };
        await Assert.That(rule.Check(Ctx(vessels))).IsNull();
    }

    [Test]
    public async Task SartPresent_FiresDangerAlarm()
    {
        var rule = new AisSartAlarmRule();
        var vessels = new[]
        {
            MakeVessel("vessels.urn:mrn:imo:mmsi:970111222", "970111222", 47.401, 8.501),
        };

        var alarm = rule.Check(Ctx(vessels, ownLat: 47.4, ownLon: 8.5));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("SART");
        await Assert.That(alarm.Severity).IsEqualTo(AlarmSeverity.Danger);
        // Life-safety: the Snoozeable flag must be false so the UI
        // hides the snooze button and the manager refuses snooze calls.
        await Assert.That(alarm.Snoozeable).IsFalse();
    }

    [Test]
    public async Task MobMmsi_FiresWithMobTitle()
    {
        var rule = new AisSartAlarmRule();
        var alarm = rule.Check(Ctx([MakeVessel("vessels.urn:mrn:imo:mmsi:972555555", "972555555", 47.5, 8.5)]));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("MOB");
    }

    [Test]
    public async Task EpirbMmsi_FiresWithEpirbTitle()
    {
        var rule = new AisSartAlarmRule();
        var alarm = rule.Check(Ctx([MakeVessel("vessels.urn:mrn:imo:mmsi:974777777", "974777777", 47.5, 8.5)]));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("EPIRB");
    }

    [Test]
    public async Task MultipleSarts_PicksNearest()
    {
        // Two SARTs on the air; the alarm should name and locate the
        // nearest one so the user gets the most-relevant bearing.
        var rule = new AisSartAlarmRule();
        var vessels = new[]
        {
            MakeVessel("vessels.urn:mrn:imo:mmsi:970000001", "970000001", 48.0, 8.5),   // ~36 nm north
            MakeVessel("vessels.urn:mrn:imo:mmsi:970000002", "970000002", 47.41, 8.5),  // ~0.6 nm north
        };

        var alarm = rule.Check(Ctx(vessels, ownLat: 47.4, ownLon: 8.5));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.TargetKey).IsEqualTo("vessels.urn:mrn:imo:mmsi:970000002");
    }

    [Test]
    public async Task SartMessage_IncludesDistanceWhenPositionKnown()
    {
        var rule = new AisSartAlarmRule();
        var alarm = rule.Check(Ctx(
            [MakeVessel("vessels.urn:mrn:imo:mmsi:970111222", "970111222", 47.41, 8.5)],
            ownLat: 47.4, ownLon: 8.5));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Message).Contains("nm");
    }

    [Test]
    public async Task MmsiMissing_FallsBackToContextParse()
    {
        // Even if the MMSI delta hasn't arrived yet, the context
        // already contains the MMSI. The rule should still fire.
        var rule = new AisSartAlarmRule();
        var v = MakeVessel("vessels.urn:mrn:imo:mmsi:970333444", mmsi: null, lat: 47.41, lon: 8.5);
        var alarm = rule.Check(Ctx([v], ownLat: 47.4, ownLon: 8.5));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("SART");
    }
}
