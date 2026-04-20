using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Tests;

/// <summary>
/// Fuzz-style tests for AisVessel.Apply, focused on the SK-delta
/// shapes that have surfaced as bugs in the field or look likely to
/// in the future. The "empty-path identity" payload is the main new
/// surface area; the rest cover malformed / extreme inputs that a
/// badly-behaved AIS source or a server plugin regression could
/// produce.
/// </summary>
public class AisVesselFuzzTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [Test]
    public async Task EmptyPath_IdentityWithNullName_DoesNotOverwriteExistingName()
    {
        // Rare but observed: SK server sends an identity snapshot with
        // name=null alongside a fresh mmsi. Don't clobber the name we
        // already have; null is "no data", not "name is empty".
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:111");
        v.Apply("name", J("SALTY BREEZE"));
        v.Apply("", J(new { name = (string?)null, mmsi = "111" }));
        await Assert.That(v.Name).IsEqualTo("SALTY BREEZE");
    }

    [Test]
    public async Task EmptyPath_DeepNestedObject_FlattensUpToDepthCap()
    {
        // Future / unusual AIS payloads may nest more than one level.
        // The flatten recurses up to MaxIdentityFlattenDepth; verify
        // that a 2-level nested path still reaches the Apply switch.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:222");
        var nested = J(new
        {
            design = new
            {
                aisShipType = new { name = "Sailing", id = 36 }
            }
        });
        v.Apply("", nested);
        // design.aisShipType maps to an object in Apply; it stores
        // "name" key as the ship type. Accept either the name or id
        // depending on which branch of Apply hit.
        await Assert.That(v.ShipType).IsNotNull();
    }

    [Test]
    public async Task EmptyPath_MalformedObjectWithNonObjectChildren_DoesNotThrow()
    {
        // A badly-written plugin could emit { name: "X", communication: 42 }.
        // The flatten must treat the non-object value as a leaf and try
        // Apply("communication", 42), which returns false; it must NOT
        // crash.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:333");
        var malformed = J(new { name = "GHOST", communication = 42 });
        var ok = v.Apply("", malformed);
        await Assert.That(v.Name).IsEqualTo("GHOST");
        await Assert.That(ok).IsTrue(); // name was applied
    }

    [Test]
    public async Task EmptyPath_EmptyObject_IsNoOp()
    {
        // Defensive: an SK server emitting { path: "", value: {} } is
        // unusual but shouldn't cause a crash or state change.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:444");
        var ok = v.Apply("", J(new { }));
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task EmptyPath_ArrayValue_Ignored()
    {
        // { path: "", value: [...] } isn't in the SK spec for vessel
        // identity but plugins have been known to bend the spec. Arrays
        // aren't objects; we shouldn't try to iterate them as identity
        // properties. Returns false (nothing applied) without throwing.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:555");
        var ok = v.Apply("", J(new[] { 1, 2, 3 }));
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Position_FractionalCloseToZero_StillApplied()
    {
        // A freshly-booted AIS transmitter can emit (0, 0) while the
        // GPS is still acquiring. The code shouldn't filter on value
        // magnitude -- (0, 0) is legitimate if rare. The downstream
        // marker will render in the Gulf of Guinea, which is the right
        // visual cue for the sailor that the AIS source is broken.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:666");
        v.Apply("navigation.position", J(new { latitude = 0.0, longitude = 0.0 }));
        await Assert.That(v.Latitude).IsEqualTo(0.0);
        await Assert.That(v.Longitude).IsEqualTo(0.0);
    }

    [Test]
    public async Task Position_ObjectMissingLongitude_NotApplied()
    {
        // Malformed delta with only latitude. The switch case does a
        // paired TryGetProperty; both must succeed. Nothing applied.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:667");
        var ok = v.Apply("navigation.position", J(new { latitude = 47.0 }));
        await Assert.That(ok).IsFalse();
        await Assert.That(v.Latitude).IsNull();
    }

    [Test]
    public async Task Apply_UnknownPath_ReturnsFalse()
    {
        // A weird path like "environment.wind.kiteAltitude" must not
        // crash and must return false so the store version doesn't tick.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:777");
        var ok = v.Apply("environment.wind.kiteAltitude", J(42.0));
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Name_WithExoticUnicode_RoundTripsClean()
    {
        // Vessel names in the wild include emoji, non-BMP chars, and
        // Hebrew. Make sure the apply path doesn't mangle them via
        // surrogate-pair splitting.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:888");
        v.Apply("name", J("\u06E9\u0926\u06E9 SeaGlider \uD83D\uDEA2"));
        await Assert.That(v.Name).Contains("\uD83D\uDEA2");
    }

    [Test]
    public async Task RepeatedApply_SameValue_StillReturnsTrue()
    {
        // Low-value note: Apply returns true even when the value didn't
        // change. AisStore doesn't rely on "changed-only" semantics; the
        // OnAisUpdated event fires regardless. Pin the contract.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:999");
        var ok1 = v.Apply("name", J("X"));
        var ok2 = v.Apply("name", J("X"));
        await Assert.That(ok1).IsTrue();
        await Assert.That(ok2).IsTrue();
    }

    [Test]
    public async Task Buddy_StringValue_DoesNotFlipFlag()
    {
        // A plugin emitting buddy as "true" (string) instead of true
        // (bool) would previously have been treated as truthy in JS but
        // the .NET side must be strict: we need the actual bool. String
        // "true" should leave IsBuddy false.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:121");
        v.Apply("buddy", J("true"));
        await Assert.That(v.IsBuddy).IsFalse();
    }

    [Test]
    public async Task Buddy_TogglesFromTrueToFalse_ReportsChange()
    {
        // The AisStore version counter relies on Apply returning true
        // when IsBuddy flips, so the snapshot cache invalidates and
        // the buddy star disappears from the map marker.
        var v = new AisVessel("vessels.urn:mrn:imo:mmsi:131");
        var set = v.Apply("buddy", J(true));
        await Assert.That(set).IsTrue();
        await Assert.That(v.IsBuddy).IsTrue();
        var clear = v.Apply("buddy", J(false));
        await Assert.That(clear).IsTrue();
        await Assert.That(v.IsBuddy).IsFalse();
    }
}
