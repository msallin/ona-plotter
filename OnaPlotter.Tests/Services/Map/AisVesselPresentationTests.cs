using OnaPlotter.Services.Map;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the presentation rules <see cref="AisLayerController"/> stamps on
/// every AIS payload: name fallbacks, colour precedence, label and
/// overlay visibility, popup rounding.
/// </summary>
public class AisVesselPresentationTests
{
    // - DisplayName (chart label) ---------------------------------

    [Test]
    [Arguments("Aurora", "244123456", false, "Aurora")]
    [Arguments("Aurora", "244123456", true, "★ Aurora")]
    [Arguments(null, "244123456", false, "244123456")]
    [Arguments("", "244123456", true, "★ 244123456")]
    public async Task DisplayName_NameThenMmsi_BuddyStarred(string? name, string? mmsi, bool buddy, string expected)
    {
        await Assert.That(AisVesselPresentation.DisplayName(name, mmsi, buddy)).IsEqualTo(expected);
    }

    [Test]
    public async Task DisplayName_NoNameNoMmsi_IsNull()
    {
        // Radar ARPA tracks have neither: no label is drawn.
        await Assert.That(AisVesselPresentation.DisplayName(null, "", true)).IsNull();
    }

    // - Title (popup + CPA chip) ----------------------------------

    [Test]
    [Arguments("Aurora", "ZZZ1", "244123456", false, "Aurora")]
    [Arguments("Aurora", "ZZZ1", "244123456", true, "★ Aurora")]
    [Arguments(null, "ZZZ1", "244123456", false, "ZZZ1")]
    [Arguments("", "", "244123456", false, "MMSI 244123456")]
    [Arguments("", "", "244123456", true, "★ MMSI 244123456")]
    [Arguments(null, null, null, false, "Unknown")]
    [Arguments(null, null, null, true, "★ Unknown")]
    public async Task Title_NameCallsignMmsiUnknown(string? name, string? callsign, string? mmsi, bool buddy, string expected)
    {
        await Assert.That(AisVesselPresentation.Title(name, callsign, mmsi, buddy)).IsEqualTo(expected);
    }

    // - Subtitle -------------------------------------------------

    [Test]
    public async Task Subtitle_AllParts()
    {
        // Typical class A target with full static data.
        await Assert.That(AisVesselPresentation.Subtitle("538071935", "V7A6238", "Sailing", 12.5, 4.2))
            .IsEqualTo("MMSI 538071935 · Call V7A6238 · Sailing · 12.5 × 4.2 m");
    }

    [Test]
    public async Task Subtitle_OnlyLengthOrBeam()
    {
        await Assert.That(AisVesselPresentation.Subtitle(null, null, null, 32.0, null)).IsEqualTo("L 32.0 m");
        await Assert.That(AisVesselPresentation.Subtitle(null, null, null, null, 6.24)).IsEqualTo("B 6.2 m");
    }

    [Test]
    public async Task Subtitle_NonFiniteDimensionsIgnored_NothingKnownIsNull()
    {
        await Assert.That(AisVesselPresentation.Subtitle(null, "", null, double.NaN, double.PositiveInfinity)).IsNull();
    }

    // - Colour roles ---------------------------------------------

    [Test]
    [Arguments(false, false, false, AisTone.Ship)]
    [Arguments(false, false, true, AisTone.Danger)]
    [Arguments(false, true, false, AisTone.Buddy)]
    [Arguments(false, true, true, AisTone.Buddy)]
    [Arguments(true, false, false, AisTone.Radar)]
    [Arguments(true, false, true, AisTone.Danger)]
    [Arguments(true, true, false, AisTone.Radar)]
    public async Task MarkerTone_RadarThenBuddyThenAlarmThenShip(bool radar, bool buddy, bool alarm, AisTone expected)
    {
        await Assert.That(AisVesselPresentation.MarkerTone(radar, buddy, alarm)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false, false, AisTone.Ship)]
    [Arguments(false, true, AisTone.Buddy)]
    [Arguments(true, false, AisTone.Radar)]
    [Arguments(true, true, AisTone.Radar)]
    public async Task VectorTone_NeverDanger(bool radar, bool buddy, AisTone expected)
    {
        await Assert.That(AisVesselPresentation.VectorTone(radar, buddy)).IsEqualTo(expected);
    }

    // - Label visibility -----------------------------------------

    [Test]
    public async Task LabelVisible_FreshNamedTarget()
    {
        await Assert.That(AisVesselPresentation.LabelVisible("Aurora", 10, 300, false, false)).IsTrue();
    }

    [Test]
    public async Task LabelVisible_HiddenAtAndPastInactiveThreshold()
    {
        await Assert.That(AisVesselPresentation.LabelVisible("Aurora", 299.9, 300, false, false)).IsTrue();
        await Assert.That(AisVesselPresentation.LabelVisible("Aurora", 300, 300, false, false)).IsFalse();
    }

    [Test]
    public async Task LabelVisible_SartAndBuddyIgnoreStaleness()
    {
        await Assert.That(AisVesselPresentation.LabelVisible("SART", 900, 300, true, false)).IsTrue();
        await Assert.That(AisVesselPresentation.LabelVisible("★ Friend", 900, 300, false, true)).IsTrue();
    }

    [Test]
    public async Task LabelVisible_NoDisplayName_Never()
    {
        await Assert.That(AisVesselPresentation.LabelVisible(null, 0, 300, true, true)).IsFalse();
    }

    // - CPA overlay ----------------------------------------------

    [Test]
    [Arguments(Cpa.Threat.Alarm, AisCpaOverlay.Alarm)]
    [Arguments(Cpa.Threat.Awareness, AisCpaOverlay.Awareness)]
    [Arguments(Cpa.Threat.None, AisCpaOverlay.None)]
    public async Task CpaOverlay_FollowsThreatForClosingTarget(Cpa.Threat threat, AisCpaOverlay expected)
    {
        await Assert.That(AisVesselPresentation.CpaOverlay(threat, 4.0, true)).IsEqualTo(expected);
    }

    [Test]
    public async Task CpaOverlay_NoneWhenNotClosingOrNoPoint()
    {
        await Assert.That(AisVesselPresentation.CpaOverlay(Cpa.Threat.Alarm, 0.0, true)).IsEqualTo(AisCpaOverlay.None);
        await Assert.That(AisVesselPresentation.CpaOverlay(Cpa.Threat.Alarm, -1.0, true)).IsEqualTo(AisCpaOverlay.None);
        await Assert.That(AisVesselPresentation.CpaOverlay(Cpa.Threat.Alarm, null, true)).IsEqualTo(AisCpaOverlay.None);
        await Assert.That(AisVesselPresentation.CpaOverlay(Cpa.Threat.Alarm, 4.0, false)).IsEqualTo(AisCpaOverlay.None);
    }

    // - Rotation + rounding --------------------------------------

    [Test]
    public async Task RotationRad_CogThenHeading_SartUnrotated()
    {
        await Assert.That(AisVesselPresentation.RotationRad(false, 1.0, 2.0)).IsEqualTo(1.0);
        await Assert.That(AisVesselPresentation.RotationRad(false, null, 2.0)).IsEqualTo(2.0);
        await Assert.That(AisVesselPresentation.RotationRad(false, null, null)).IsNull();
        await Assert.That(AisVesselPresentation.RotationRad(true, 1.0, 2.0)).IsNull();
    }

    [Test]
    public async Task WholeDegrees_NormalisesToZeroTo360()
    {
        await Assert.That(AisVesselPresentation.WholeDegrees(Math.PI)).IsEqualTo(180.0);
        await Assert.That(AisVesselPresentation.WholeDegrees(2 * Math.PI)).IsEqualTo(0.0);
        await Assert.That(AisVesselPresentation.WholeDegrees(-Math.PI / 2)).IsEqualTo(270.0);
        await Assert.That(AisVesselPresentation.WholeDegrees(null)).IsNull();
        await Assert.That(AisVesselPresentation.WholeDegrees(double.NaN)).IsNull();
    }

    [Test]
    public async Task Round_AwayFromZero_NullAndNonFiniteToNull()
    {
        await Assert.That(AisVesselPresentation.Round(0.125, 2)).IsEqualTo(0.13);
        await Assert.That(AisVesselPresentation.Round(2.5, 0)).IsEqualTo(3.0);
        await Assert.That(AisVesselPresentation.Round(null, 1)).IsNull();
        await Assert.That(AisVesselPresentation.Round(double.PositiveInfinity, 1)).IsNull();
    }
}
