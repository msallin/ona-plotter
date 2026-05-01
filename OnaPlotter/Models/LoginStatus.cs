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

    /// <summary>Server-config flag, NOT a per-user authority.
    /// signalk-server returns this true when the server's default
    /// is "sessions are read-only unless explicitly granted" --
    /// even for a logged-in admin. Treating it as the gate (the
    /// original implementation did) made the chip light up for an
    /// admin helm who was, in fact, fully logged in. Kept as a
    /// model field for parser-shape parity, but excluded from
    /// <see cref="ShouldShowLoginWarning"/>.</summary>
    [JsonPropertyName("readOnlyAccess")]
    public bool ReadOnlyAccess { get; set; }

    /// <summary>"Show the not-logged-in banner" rule, centralised
    /// here so call sites don't repeat the boolean math.
    ///
    /// <para>Banner shows when the server has security enabled AND
    /// the current session is NOT logged in. Field-tested against
    /// signalk-server: when the helm IS logged in (status =
    /// "loggedIn", username present), the response can still carry
    /// <c>readOnlyAccess: true</c> -- that flag is a server-config
    /// signal ("by default sessions are read-only"), not a
    /// per-user authority. The corrected rule trusts
    /// <c>status == "loggedIn"</c> as the single source of truth
    /// for the per-session state.</para>
    /// </summary>
    [JsonIgnore]
    public bool ShouldShowLoginWarning =>
        AuthenticationRequired
        && !string.Equals(Status, "loggedIn", System.StringComparison.OrdinalIgnoreCase);
}
