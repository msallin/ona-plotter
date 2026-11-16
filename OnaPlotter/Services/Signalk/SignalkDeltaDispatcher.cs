using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Turns one WebSocket frame into store updates. Owns the self-vessel
/// identity (needed to tell own-boat deltas from AIS traffic) and picks
/// the handler per delta:
/// <list type="bullet">
/// <item><c>resources.&lt;type&gt;.&lt;id&gt;</c> paths, any context:
///   <see cref="OnResourceDelta"/>.</item>
/// <item>Self context (empty, <c>vessels.self</c> or the resolved URN):
///   <see cref="SelfDeltaHandler"/>, which forwards radar targets and
///   server notifications.</item>
/// <item><c>atons.*</c>: <see cref="AtonDeltaHandler"/>.</item>
/// <item>Any other context: <see cref="AisDeltaHandler"/>.</item>
/// </list>
/// Transport concerns (socket, reconnect, staleness, raw-frame fan-out)
/// stay in <see cref="SignalkClient"/>.
/// </summary>
internal sealed class SignalkDeltaDispatcher
{
    /// <summary>"self" key as a UTF-8 literal. Frames are scanned for it
    /// only until the self context resolves.</summary>
    private static readonly byte[] SelfKeyUtf8 = "\"self\""u8.ToArray();

    /// <summary>Reusable Null-kind element for resource deletes. Sourced
    /// from a never-disposed document (the same pattern System.Text.Json
    /// uses for sentinel elements) so a delete costs no Parse + Clone.</summary>
    private static readonly JsonElement NullJsonElement = JsonDocument.Parse("null").RootElement;

    private readonly AisStore _ais;
    private readonly SelfDeltaHandler _self;
    private readonly AisDeltaHandler _aisHandler;
    private readonly AtonDeltaHandler _atons;
    private readonly ServerNotificationDeltaHandler _notifications;
    private readonly OwnTrackRecorder _track;
    private readonly ILogger _logger;
    private string _selfContext = "";

    public SignalkDeltaDispatcher(NavigationData data, TrackBuffer track, AisStore ais,
        AtonStore atons, ServerNotificationStore serverNotifications, TimeProvider time, ILogger logger)
    {
        _ais = ais;
        _logger = logger;
        _aisHandler = new AisDeltaHandler(ais);
        _atons = new AtonDeltaHandler(atons);
        _notifications = new ServerNotificationDeltaHandler(serverNotifications);
        _self = new SelfDeltaHandler(data, _aisHandler, _notifications, logger);
        _track = new OwnTrackRecorder(data, track, time);
    }

    /// <summary>
    /// Raised for every <c>resources.&lt;type&gt;.&lt;id&gt;</c> value. Args
    /// are (type, id, value): type is "routes" / "waypoints" / "notes" /
    /// "regions"; id is everything after the type segment (urn-form ids
    /// keep their colons and dots); value is the full resource document
    /// on create / update and a <c>ValueKind == Null</c> element on
    /// delete.
    /// </summary>
    public event Action<string, string, JsonElement>? OnResourceDelta;

    /// <summary>Own-boat VHF callsign, see <see cref="SelfDeltaHandler.OwnCallsign"/>.</summary>
    public string? OwnCallsign => _self.OwnCallsign;

    /// <summary>MMSI from the self URN; null until the server identifies
    /// us or when the URN carries no mmsi segment.</summary>
    public string? OwnMmsi =>
        string.IsNullOrEmpty(_selfContext) ? null : AisVessel.ExtractMmsi(_selfContext);

    /// <summary>
    /// Parses and routes one frame. Returns true when own-boat navigation
    /// state (or the server notification set) changed, i.e. when the
    /// caller should raise its data-changed event. A hello frame (no
    /// <c>updates</c>) only feeds the self-context detection.
    /// Malformed JSON throws <see cref="JsonException"/>; the caller
    /// decides how to survive it.
    /// </summary>
    public bool Dispatch(ReadOnlySpan<byte> utf8Bytes)
    {
        var delta = JsonSerializer.Deserialize(utf8Bytes, OnaJsonContext.Default.SignalkDelta);

        if (delta?.Updates is null)
        {
            // Hello frame: {"name":..., "version":..., "self":"vessels.urn:..."}.
            // It arrives once per session, so skip the scan once the
            // self context is known.
            if (string.IsNullOrEmpty(_selfContext) && utf8Bytes.IndexOf(SelfKeyUtf8) >= 0)
            {
                using var doc = JsonDocument.Parse(utf8Bytes.ToArray());
                if (doc.RootElement.TryGetProperty("self", out var selfProp))
                {
                    SetSelfContext(selfProp.GetString());
                }
            }
            return false;
        }

        // Resource paths are self-describing whatever context the server
        // stamps on them, so they're scanned before context routing.
        DispatchResourceUpdates(delta);

        if (IsSelfContext(delta.Context))
        {
            if (!_self.Apply(delta)) return false;
            _track.OnSelfDataChanged();
            return true;
        }
        if (AtonDeltaHandler.IsAtonContext(delta.Context))
            _atons.Apply(delta);
        else
            _aisHandler.Apply(delta);
        return false;
    }

    /// <summary>
    /// Normalises the self identifier from the hello frame or the REST
    /// seed and retro-evicts that vessel from <see cref="AisStore"/>.
    /// Servers supply it as "vessels.urn:mrn:imo:mmsi:..." or bare
    /// "urn:mrn:imo:mmsi:..."; the prefixed form is stored so it
    /// compares equal to delta contexts.
    /// </summary>
    public void SetSelfContext(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            _selfContext = "";
            return;
        }
        _selfContext = raw.StartsWith("vessels.", StringComparison.Ordinal)
            ? raw
            : "vessels." + raw;
        _logger.LogInformation("Self context: {Self}", _selfContext);

        // An AIS delta for this context may have arrived before we knew
        // who we are; remove own-boat from the AIS list and blocklist it.
        _ais.Evict(_selfContext);
    }

    /// <summary>Own-boat data may arrive before the self context
    /// resolves, or only in URN form; all three shapes count as self:
    /// empty, <c>vessels.self</c>, and the resolved URN.</summary>
    public bool IsSelfContext(string? context)
    {
        if (string.IsNullOrEmpty(context)) return true;
        if (context == "vessels.self") return true;
        if (string.IsNullOrEmpty(_selfContext)) return false;
        return context == _selfContext;
    }

    /// <summary>Clears state the server re-publishes on reconnect
    /// (notifications, AtoNs) so nothing stale shows while offline.</summary>
    public void ResetOnDisconnect()
    {
        _notifications.Reset();
        _atons.Reset();
    }

    /// <summary>
    /// Re-fires each <c>resources.&lt;type&gt;.&lt;id&gt;</c> value as
    /// <see cref="OnResourceDelta"/>. The id is everything after the
    /// type segment:
    /// <code>
    /// "resources.routes.urn:mrn:signalk:uuid:1a2b" -> ("routes", "urn:mrn:signalk:uuid:1a2b")
    /// "resources.notes.n1"                         -> ("notes", "n1")
    /// "resources.routes", "resources.routes."      -> skipped
    /// </code>
    /// </summary>
    private void DispatchResourceUpdates(SignalkDelta delta)
    {
        var handler = OnResourceDelta;
        if (handler is null) return;

        foreach (var update in delta.Updates!)
        {
            if (update.Values is null) continue;
            foreach (var val in update.Values)
            {
                if (string.IsNullOrEmpty(val.Path)) continue;
                if (!val.Path.StartsWith("resources.", StringComparison.Ordinal)) continue;

                var rest = val.Path.AsSpan("resources.".Length);
                var dotIdx = rest.IndexOf('.');
                if (dotIdx <= 0 || dotIdx >= rest.Length - 1) continue;

                var type = rest[..dotIdx].ToString();
                var id = rest[(dotIdx + 1)..].ToString();

                // Resource documents arrive as Element; ToJsonElement
                // only allocates for scalar shapes a well-behaved server
                // never sends here.
                JsonElement payload = val.Value.IsNull ? NullJsonElement : val.Value.ToJsonElement();

                try { handler.Invoke(type, id, payload); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Resource delta handler threw on {Type}/{Id}", type, id);
                }
            }
        }
    }
}
