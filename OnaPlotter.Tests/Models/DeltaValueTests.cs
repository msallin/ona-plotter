using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Tests.Models;

/// <summary>
/// Pins <see cref="DeltaValueConverter"/> for every JSON value kind a
/// SignalK delta can carry, and keeps <see cref="DeltaValue.FromElement"/>
/// classifying exactly like the wire path so REST-seeded values and
/// test fixtures behave the same as live deltas.
/// </summary>
public class DeltaValueTests
{
    /// <summary>Deserialises <c>{"path":"p","value":&lt;json&gt;}</c>
    /// through the source-generated context the receive loop uses.</summary>
    private static DeltaValue Wire(string valueJson)
    {
        var json = $$"""{"updates":[{"values":[{"path":"p","value":{{valueJson}}}]}]}""";
        var delta = JsonSerializer.Deserialize(json, OnaJsonContext.Default.SignalkDelta)!;
        return delta.Updates![0].Values![0].Value;
    }

    private static DeltaValue FromParsed(string valueJson) =>
        DeltaValue.FromElement(JsonDocument.Parse(valueJson).RootElement.Clone());

    [Test]
    [Arguments("0", 0.0)]
    [Arguments("-1", -1.0)]
    [Arguments("1", 1.0)]
    [Arguments("3.52", 3.52)]
    [Arguments("-3.14159", -3.14159)]
    [Arguments("1.7976931348623157e308", double.MaxValue)]
    [Arguments("5e-324", double.Epsilon)]
    public async Task Number(string json, double expected)
    {
        foreach (var v in new[] { Wire(json), FromParsed(json) })
        {
            await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.Number);
            await Assert.That(v.TryGetNumber(out var d)).IsTrue();
            await Assert.That(d).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Number_OutOfDoubleRange_KeptAsElement()
    {
        // 1e400 overflows double; never surface it as Infinity.
        foreach (var v in new[] { Wire("1e400"), FromParsed("1e400") })
        {
            await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.Element);
            await Assert.That(v.TryGetNumber(out _)).IsFalse();
        }
    }

    [Test]
    [Arguments("true", true)]
    [Arguments("false", false)]
    public async Task Bool(string json, bool expected)
    {
        foreach (var v in new[] { Wire(json), FromParsed(json) })
        {
            await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.Bool);
            await Assert.That(v.TryGetBool(out var b)).IsTrue();
            await Assert.That(b).IsEqualTo(expected);
            await Assert.That(v.TryGetNumber(out _)).IsFalse();
        }
    }

    [Test]
    [Arguments("\"motoring\"", "motoring")]
    [Arguments("\"\"", "")]
    [Arguments("\"caf\\u00e9\"", "café")]
    public async Task String(string json, string expected)
    {
        foreach (var v in new[] { Wire(json), FromParsed(json) })
        {
            await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.String);
            await Assert.That(v.TryGetString(out var s)).IsTrue();
            await Assert.That(s).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Null_Explicit_And_Missing_AreTheSame()
    {
        await Assert.That(Wire("null").IsNull).IsTrue();
        await Assert.That(FromParsed("null").IsNull).IsTrue();

        var missing = JsonSerializer.Deserialize(
            """{"updates":[{"values":[{"path":"p"}]}]}""", OnaJsonContext.Default.SignalkDelta)!;
        await Assert.That(missing.Updates![0].Values![0].Value.IsNull).IsTrue();
        await Assert.That(default(DeltaValue)).IsEqualTo(DeltaValue.Null);
    }

    [Test]
    [Arguments("""{"latitude":59.91,"longitude":10.75}""", 59.91, 10.75)]
    [Arguments("""{"longitude":10.75,"latitude":59.91,"altitude":2.1}""", 59.91, 10.75)]
    [Arguments("""{"latitude":59.91,"longitude":10.75,"altitude":null}""", 59.91, 10.75)]
    [Arguments("""{"latitude":-90,"longitude":180}""", -90.0, 180.0)]
    public async Task Position_FlatNumericObject(string json, double lat, double lon)
    {
        foreach (var v in new[] { Wire(json), FromParsed(json) })
        {
            await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.Position);
            await Assert.That(v.TryGetLatLon(out var la, out var lo)).IsTrue();
            await Assert.That((la, lo)).IsEqualTo((lat, lon));
            await Assert.That(v.TryGetElement(out _)).IsFalse();
        }
    }

    [Test]
    [Arguments("""{"latitude":59.91}""")]
    [Arguments("""{"latitude":59.91,"longitude":null}""")]
    [Arguments("""{"latitude":"59.91","longitude":10.75}""")]
    [Arguments("""{"latitude":59.91,"longitude":10.75,"name":"buoy"}""")]
    [Arguments("""{"latitude":59.91,"longitude":10.75,"extra":{"a":1}}""")]
    [Arguments("""{"latitude":59.91,"longitude":10.75,"list":[1]}""")]
    [Arguments("""{"latitude":1e400,"longitude":10.75}""")]
    [Arguments("""{}""")]
    public async Task Object_NotAFlatPosition_KeptAsElement(string json)
    {
        foreach (var v in new[] { Wire(json), FromParsed(json) })
        {
            await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.Element);
            await Assert.That(v.TryGetElement(out var el)).IsTrue();
            await Assert.That(el.ValueKind).IsEqualTo(JsonValueKind.Object);
        }
    }

    [Test]
    public async Task Element_NestedPosition_StillReadableAsLatLon()
    {
        // navigation.course.nextPoint parent object.
        const string json = """{"type":"RoutePoint","position":{"latitude":-33.86,"longitude":151.21}}""";
        var v = Wire(json);

        await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.Element);
        await Assert.That(v.TryGetLatLon(out var lat, out var lon)).IsTrue();
        await Assert.That((lat, lon)).IsEqualTo((-33.86, 151.21));
    }

    [Test]
    public async Task Array_KeptAsElement()
    {
        foreach (var v in new[] { Wire("[1,2,3]"), FromParsed("[1,2,3]") })
        {
            await Assert.That(v.Kind).IsEqualTo(DeltaValueKind.Element);
            await Assert.That(v.TryGetElement(out var el)).IsTrue();
            await Assert.That(el.GetArrayLength()).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Converter_LeavesReaderOnNextProperty_AfterPositionScan()
    {
        // The position scan runs on a reader copy; the sibling property
        // after the value must still deserialise.
        var delta = JsonSerializer.Deserialize(
            """{"context":"vessels.self","updates":[{"timestamp":"t","values":[{"value":{"latitude":1,"longitude":2},"path":"navigation.position"},{"path":"x","value":{"a":"b"}}]}]}""",
            OnaJsonContext.Default.SignalkDelta)!;

        var values = delta.Updates![0].Values!;
        await Assert.That(values[0].Path).IsEqualTo("navigation.position");
        await Assert.That(values[0].Value.Kind).IsEqualTo(DeltaValueKind.Position);
        await Assert.That(values[1].Path).IsEqualTo("x");
        await Assert.That(values[1].Value.Kind).IsEqualTo(DeltaValueKind.Element);
        await Assert.That(delta.Updates[0].Timestamp).IsEqualTo("t");
    }

    [Test]
    public async Task Accessors_ReturnFalse_ForOtherKinds()
    {
        DeltaValue n = 1.5;
        await Assert.That(n.TryGetBool(out _)).IsFalse();
        await Assert.That(n.TryGetString(out _)).IsFalse();
        await Assert.That(n.TryGetLatLon(out _, out _)).IsFalse();
        await Assert.That(n.TryGetElement(out _)).IsFalse();
        await Assert.That(n.IsNull).IsFalse();
        await Assert.That(DeltaValue.Null.TryGetNumber(out _)).IsFalse();
    }

    [Test]
    public async Task ImplicitConversions()
    {
        DeltaValue fromDouble = 2.5;
        DeltaValue fromNullableNull = (double?)null;
        DeltaValue fromInt = 3;
        DeltaValue fromBool = true;
        DeltaValue fromString = "x";
        DeltaValue fromNullString = (string?)null;

        await Assert.That(fromDouble).IsEqualTo(DeltaValue.FromNumber(2.5));
        await Assert.That(fromNullableNull.IsNull).IsTrue();
        await Assert.That(fromInt).IsEqualTo(DeltaValue.FromNumber(3));
        await Assert.That(fromBool).IsEqualTo(DeltaValue.FromBool(true));
        await Assert.That(fromString).IsEqualTo(DeltaValue.FromString("x"));
        await Assert.That(fromNullString.IsNull).IsTrue();
    }

    [Test]
    [Arguments("3.5", "3.5")]
    [Arguments("211234567", "211234567")]
    [Arguments("true", "True")]
    [Arguments("\"abc\"", "abc")]
    [Arguments("null", "")]
    [Arguments("""{"a":1}""", """{"a":1}""")]
    public async Task ToString_MatchesJsonElementText(string json, string expected)
    {
        await Assert.That(Wire(json).ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("3.5")]
    [Arguments("true")]
    [Arguments("\"abc\"")]
    [Arguments("null")]
    [Arguments("""{"latitude":1.5,"longitude":2.5}""")]
    [Arguments("""{"name":"x","position":{"latitude":1,"longitude":2}}""")]
    public async Task ToJsonElement_RoundTripsEveryKind(string json)
    {
        var v = Wire(json);
        var el = v.ToJsonElement();
        await Assert.That(DeltaValue.FromElement(el)).IsEqualTo(v);
    }

    [Test]
    public async Task Write_SerialisesEveryKind()
    {
        var values = new[]
        {
            DeltaValue.FromNumber(1.5), DeltaValue.FromBool(false), DeltaValue.FromString("s"),
            DeltaValue.FromPosition(1, 2), DeltaValue.Null, FromParsed("[1]"),
        };
        var json = JsonSerializer.Serialize(values.Select(v => v.ToJsonElement()).ToArray());
        await Assert.That(json).IsEqualTo("""[1.5,false,"s",{"latitude":1,"longitude":2},null,[1]]""");
    }
}
