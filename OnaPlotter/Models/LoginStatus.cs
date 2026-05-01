using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// Snapshot of <c>/skServer/loginStatus</c>. Tells the helm whether the
/// SK server has security enabled at all, whether the current browser
/// session is logged in, and whether write actions are gated. Used by
/// the not-logged-in banner so the helm sees a warning BEFORE they
/// try to save a route and lose work to a 401.
///
/// <para>Field names mirror the upstream JSON exactly so the parser
/// is a one-shot deserialise; no defensive renaming.</para>
/// </summary>
public sealed class LoginStatus
{
    /// <summary>"loggedIn" or "notLoggedIn". Anything else is also
    /// treated as "not logged in" by the helm-side classifier --
    /// servers under development have shipped other strings here.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Logged-in user's name. Null when not logged in;
    /// present in the banner so the helm sees who's signed in
    /// during a quick "is this account right?" check.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>True when SK has security enabled. False on a
    /// no-auth dev / homelab server, in which case the banner
    /// stays hidden -- there's nothing to log into.</summary>
    [JsonPropertyName("authenticationRequired")]
    public bool AuthenticationRequired { get; set; }

    /// <summary>True when the current session can read but not
    /// write. The condition the banner actually cares about: any
    /// save attempt will fail.</summary>
    [JsonPropertyName("readOnlyAccess")]
    public bool ReadOnlyAccess { get; set; }

    /// <summary>"Show the not-logged-in banner" rule, centralised
    /// here so call sites don't repeat the boolean math. Banner
    /// shows when the server has auth enabled AND the current
    /// session is read-only OR explicitly not-logged-in.</summary>
    [JsonIgnore]
    public bool ShouldShowLoginWarning =>
        AuthenticationRequired &&
        (ReadOnlyAccess || !string.Equals(Status, "loggedIn", System.StringComparison.OrdinalIgnoreCase));
}
