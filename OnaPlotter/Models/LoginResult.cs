using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// Response from <c>POST /signalk/v1/auth/login</c>. Spec'd by
/// SignalK 1.7 security
/// (https://signalk.org/specification/1.7.0/doc/security.html). The
/// server returns the JWT in <see cref="Token"/>; <see cref="TimeToLive"/>
/// is the lifetime in seconds the token is valid.
///
/// <para>Field names match the wire format exactly so the parser is a
/// one-shot deserialise; no defensive renaming.</para>
/// </summary>
public sealed class LoginResult
{
    /// <summary>JWT bearer token. Attach as
    /// <c>Authorization: Bearer &lt;token&gt;</c> on REST calls or as
    /// <c>?token=&lt;token&gt;</c> on the WebSocket stream URL.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>Lifetime of <see cref="Token"/> in seconds. The server
    /// also encodes this in the JWT's <c>exp</c> claim, but we honour
    /// the field-level value here so we don't have to base64-decode the
    /// JWT just to read its expiry.</summary>
    [JsonPropertyName("timeToLive")]
    public int? TimeToLive { get; set; }
}
