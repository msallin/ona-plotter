using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// SignalK subscribe / unsubscribe envelopes sent over the WebSocket
/// from the client to the server. Spec: https://signalk.org/specification/1.7.0/doc/subscription_protocol.html
///
/// <para>These named records replace the earlier
/// <c>JsonSerializer.Serialize(new {...})</c> anonymous shapes in
/// <see cref="OnaPlotter.Services.SignalkClient"/>. The migration is
/// part of the trim-readiness work tracked in
/// <c>docs/research-trimmode.md</c>: anonymous types are reachable
/// from the static call graph today, but a future <c>TrimMode=full</c>
/// flip would require either <c>[DynamicDependency]</c> hints or named
/// types each call site can reference. Named records keep the wire
/// shape explicit and let the source-gen
/// <see cref="OnaPlotter.Services.Json.OnaJsonContext"/> generate a
/// trim-clean serialiser.</para>
/// </summary>
internal sealed record SignalkSubscribeRequest(
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("subscribe")] SignalkSubscribePath[] Subscribe)
{
    /// <summary>Materialise a subscribe envelope from a path
    /// enumeration + uniform period / policy. Hides the
    /// <c>Select(p =&gt; new ...).ToArray()</c> at the call site so
    /// <see cref="OnaPlotter.Services.SignalkClient"/> reads as a
    /// single line per send.</summary>
    public static SignalkSubscribeRequest For(
        string context, IEnumerable<string> paths, int periodMs, string policy)
    {
        var rows = paths.Select(p => new SignalkSubscribePath(p, periodMs, policy)).ToArray();
        return new SignalkSubscribeRequest(context, rows);
    }
}

/// <summary>One row of a subscribe envelope: path + cadence policy.
/// <c>policy</c> is "ideal" (server coalesces) for the standard
/// subscription tier, "instant" for the RawStream debug page that
/// wants every delta the server emits.</summary>
internal sealed record SignalkSubscribePath(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("period")] int Period,
    [property: JsonPropertyName("policy")] string Policy);

/// <summary>Unsubscribe envelope shape. The server treats a missing
/// <c>period</c> / <c>policy</c> as "drop all subscriptions on these
/// paths"; only the path string is needed in each row.</summary>
internal sealed record SignalkUnsubscribeRequest(
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("unsubscribe")] SignalkUnsubscribePath[] Unsubscribe)
{
    /// <summary>Materialise an unsubscribe envelope from a path
    /// enumeration. Mirror of
    /// <see cref="SignalkSubscribeRequest.For"/> for the cancel
    /// side.</summary>
    public static SignalkUnsubscribeRequest For(string context, IEnumerable<string> paths)
    {
        var rows = paths.Select(p => new SignalkUnsubscribePath(p)).ToArray();
        return new SignalkUnsubscribeRequest(context, rows);
    }
}

internal sealed record SignalkUnsubscribePath(
    [property: JsonPropertyName("path")] string Path);
