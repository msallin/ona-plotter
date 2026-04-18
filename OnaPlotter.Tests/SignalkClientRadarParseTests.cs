using OnaPlotter.Services;

namespace OnaPlotter.Tests;

// Pinning the radar-path parser: Mayara emits target deltas under
// vessels.self on paths like radars.<rid>.targets.<tid>.<field>. A naive
// split('.') mis-parses any ID that contains a dot (IPv4 hardware IDs,
// dotted hex hashes); these tests cover the happy path and the edge cases
// that motivated splitting on the literal ".targets." separator instead.
public class SignalkClientRadarParseTests
{
    [Test]
    public async Task Parse_HappyPath()
    {
        var r = SignalkClient.TryParseRadarTargetPath("radars.r1.targets.t1.position");
        await Assert.That(r.HasValue).IsTrue();
        await Assert.That(r!.Value.radarId).IsEqualTo("r1");
        await Assert.That(r.Value.targetId).IsEqualTo("t1");
        await Assert.That(r.Value.field).IsEqualTo("position");
    }

    [Test]
    public async Task Parse_NestedField()
    {
        // Fields like navigation.position arrive dotted; everything after
        // the target id must be preserved verbatim.
        var r = SignalkClient.TryParseRadarTargetPath("radars.r1.targets.t1.navigation.position");
        await Assert.That(r!.Value.field).IsEqualTo("navigation.position");
    }

    [Test]
    public async Task Parse_DottedRadarId()
    {
        // Radar ID with dots, e.g. an IPv4 hardware identifier.
        var r = SignalkClient.TryParseRadarTargetPath("radars.192.168.1.42.targets.T001.position");
        await Assert.That(r.HasValue).IsTrue();
        await Assert.That(r!.Value.radarId).IsEqualTo("192.168.1.42");
        await Assert.That(r.Value.targetId).IsEqualTo("T001");
        await Assert.That(r.Value.field).IsEqualTo("position");
    }

    [Test]
    public async Task Parse_RejectsWrongPrefix()
    {
        await Assert.That(SignalkClient.TryParseRadarTargetPath("navigation.position").HasValue).IsFalse();
        await Assert.That(SignalkClient.TryParseRadarTargetPath("radar.r1.targets.t1.position").HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_RejectsMissingTargets()
    {
        await Assert.That(SignalkClient.TryParseRadarTargetPath("radars.r1.status").HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_RejectsEmptyRadarId()
    {
        await Assert.That(SignalkClient.TryParseRadarTargetPath("radars..targets.t1.position").HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_RejectsMissingField()
    {
        // Must have a field after the target id, not just a bare target.
        await Assert.That(SignalkClient.TryParseRadarTargetPath("radars.r1.targets.t1").HasValue).IsFalse();
        await Assert.That(SignalkClient.TryParseRadarTargetPath("radars.r1.targets.t1.").HasValue).IsFalse();
    }
}
