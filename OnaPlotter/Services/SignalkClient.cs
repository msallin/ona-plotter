using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services;

/// <summary>
/// Maintains a persistent WebSocket connection to a SignalK server, receives
/// delta messages, and distributes parsed navigation/AIS data to the shared
/// <see cref="NavigationData"/>, <see cref="TrackBuffer"/>, and <see cref="AisStore"/>.
/// Automatically reconnects with exponential backoff on connection loss.
/// </summary>
public sealed class SignalkClient : IAsyncDisposable
{
    private const int InitialBackoffMs = 1_000;
    private const int MaxBackoffMs = 30_000;
    private const int ReceiveBufferBytes = 8 * 1024;
    private const int StaleDataThresholdSec = 5;

    /// <summary>
    /// Default SignalK subscription period for our standard paths:
    /// position / speed / heading / wind / depth / course / autopilot /
    /// tide. 1000 ms with <c>policy: "ideal"</c> caps per-path updates
    /// at 1 Hz on the server side, coalescing the 10+ Hz firehose a
    /// well-instrumented N2K bus produces. Cuts websocket bytes and
    /// downstream Blazor renders without any client-side throttling.
    /// </summary>
    public const int StandardSubscriptionPeriodMs = 1_000;

    /// <summary>
    /// Cap for slow-changing self paths (anchor radii, tide heights,
    /// solar state, active-route static fields). These don't change on
    /// a per-second cadence; subscribing at 1 Hz wastes bandwidth and
    /// (on Raspi Chrome) costs frame time the WASM client could be
    /// using to stay smooth. 10 s lines up with typical plugin
    /// publication cadences. User-facing effect: the anchor card still
    /// updates within a tick of a change; everyday traffic is an order
    /// of magnitude lower.
    /// </summary>
    public const int SlowSubscriptionPeriodMs = 10_000;

    /// <summary>
    /// Tighter period for Raw-Stream user-added subscriptions. The
    /// RawStream page is for inspecting live data; a 200 ms cap
    /// (5 Hz ceiling under <c>policy: "instant"</c>) keeps the display
    /// feeling real-time without drowning the viewer.
    /// </summary>
    public const int RawStreamSubscriptionPeriodMs = 200;

    private readonly NavigationData _data;
    private readonly TrackBuffer _track;
    private readonly AisStore _ais;
    private readonly Uri _wsUri;
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private string _selfContext;
    private readonly ILogger<SignalkClient> _logger;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Fired when the user pokes "Reconnect now" via <see cref="ReconnectNow"/>.
    // The reconnect loop breaks its backoff Task.Delay on this token and
    // resets backoffMs so the next connect attempt is immediate.
    private CancellationTokenSource? _backoffCts;

    // Extra paths subscribed to by the RawStream page (re-applied on reconnect).
    private readonly HashSet<string> _extraPaths = [];

    /// <summary>
    /// Subscription tier for a SignalK path. The ReceiveLoopAsync
    /// groups paths by tier and issues one subscribe per tier with the
    /// matching context + period + policy. Adding a new path is a
    /// one-line addition to <see cref="Paths"/>; no set math to keep
    /// in sync.
    /// </summary>
    internal enum PathTier
    {
        /// <summary>vessels.self at 1 Hz. Self-only fields (wind, depth,
        /// course-next-point, autopilot, tidal current). The shared
        /// nav fields (position / SOG / COG / heading) are NOT here --
        /// they live in <see cref="Ais"/> and reach self via the
        /// vessels.* wildcard, avoiding duplicate delivery.</summary>
        SelfFast,

        /// <summary>vessels.self at 10 s. Plugin-driven slow fields
        /// (anchor radii, tide, solar state, active-route progress).
        /// Subscribed separately so we don't wake the WASM main
        /// thread once per second for data that changes every few
        /// minutes.</summary>
        SelfSlow,

        /// <summary>vessels.* (which matches self too) at 1 Hz. Shared
        /// nav fields + AIS-only static data (name, MMSI, callsign,
        /// ship type, buddy). Going via the wildcard means self gets
        /// these once AND every AIS vessel gets them once -- no
        /// duplication on self.</summary>
        Ais,
    }

    internal readonly record struct PathSubscription(string Path, PathTier Tier);

    /// <summary>
    /// The single registry of every path the app subscribes to. One
    /// entry per path, exactly one tier per entry. Exposed as internal
    /// so the test project (InternalsVisibleTo) can pin the partition
    /// contract. Order within a tier is preserved because SendSubscription
    /// iterates in list order; that doesn't affect delivery semantics
    /// but keeps the wire format stable for the /raw viewer.
    /// </summary>
    internal static readonly PathSubscription[] Paths =
    [
        // --- AIS + shared nav (vessels.* reaches self + others) ---
        new("navigation.position",                                           PathTier.Ais),
        new("navigation.speedOverGround",                                    PathTier.Ais),
        new("navigation.courseOverGroundTrue",                               PathTier.Ais),
        new("navigation.headingTrue",                                        PathTier.Ais),
        new("name",                                                          PathTier.Ais),
        new("mmsi",                                                          PathTier.Ais),
        new("communication.callsignVhf",                                     PathTier.Ais),
        new("design.aisShipType",                                            PathTier.Ais),
        // Published by sbender9/signalk-buddylist-plugin when installed.
        // AisVessel.Apply sets IsBuddy; unknown when the plugin is absent.
        new("buddy",                                                         PathTier.Ais),

        // --- Self-only fast fields (1 Hz) ---
        new("environment.depth.belowTransducer",                             PathTier.SelfFast),
        new("environment.wind.angleApparent",                                PathTier.SelfFast),
        new("environment.wind.speedApparent",                                PathTier.SelfFast),
        new("environment.wind.angleTrueWater",                               PathTier.SelfFast),
        new("environment.wind.speedTrue",                                    PathTier.SelfFast),
        new("environment.wind.directionTrue",                                PathTier.SelfFast),
        // Mayara radar ARPA targets. Paths arrive as
        // radars.<radarId>.targets.<targetId>.(position|course|speed|...)
        // under context vessels.self; ProcessSelfDelta detects and routes
        // them into AisStore with a synthesised radar.* context.
        new("radars.*.targets.*",                                            PathTier.SelfFast),
        // Active course / route next-WP fields (HUD updates per fix).
        new("navigation.courseGreatCircle.activeRoute.href",                 PathTier.SelfFast),
        new("navigation.courseGreatCircle.activeRoute.name",                 PathTier.SelfFast),
        new("navigation.courseGreatCircle.nextPoint.position",               PathTier.SelfFast),
        new("navigation.courseGreatCircle.nextPoint.distance",               PathTier.SelfFast),
        new("navigation.courseGreatCircle.nextPoint.bearingTrue",            PathTier.SelfFast),
        new("navigation.courseGreatCircle.nextPoint.timeToGo",               PathTier.SelfFast),
        new("navigation.courseGreatCircle.nextPoint.velocityMadeGood",       PathTier.SelfFast),
        new("navigation.courseRhumbline.nextPoint.position",                 PathTier.SelfFast),
        new("navigation.courseRhumbline.nextPoint.distance",                 PathTier.SelfFast),
        new("navigation.courseRhumbline.nextPoint.bearingTrue",              PathTier.SelfFast),
        new("navigation.courseRhumbline.nextPoint.timeToGo",                 PathTier.SelfFast),
        new("navigation.courseRhumbline.nextPoint.velocityMadeGood",         PathTier.SelfFast),
        new("navigation.courseGreatCircle.crossTrackError",                  PathTier.SelfFast),
        new("navigation.courseRhumbline.crossTrackError",                    PathTier.SelfFast),
        new("navigation.courseGreatCircle.previousPoint.position",           PathTier.SelfFast),
        new("navigation.courseRhumbline.previousPoint.position",             PathTier.SelfFast),
        // Autopilot state + target heading.
        new("steering.autopilot.state",                                      PathTier.SelfFast),
        new("steering.autopilot.target.headingTrue",                         PathTier.SelfFast),
        // Tidal current: drift + set, used for the map arrow overlay.
        new("environment.current.setTrue",                                   PathTier.SelfFast),
        new("environment.current.drift",                                     PathTier.SelfFast),

        // --- Self-only slow fields (10 s) ---
        // Anchor alarm plugin (sbender9/signalk-anchoralarm-plugin).
        new("navigation.anchor.position",                                    PathTier.SelfSlow),
        new("navigation.anchor.maxRadius",                                   PathTier.SelfSlow),
        new("navigation.anchor.currentRadius",                               PathTier.SelfSlow),
        // Tide height + next extremes (openwatersio/signalk-tides &
        // similar plugins). No-ops when the plugin isn't installed.
        new("environment.tide.heightNow",                                    PathTier.SelfSlow),
        new("environment.tide.heightHigh",                                   PathTier.SelfSlow),
        new("environment.tide.heightLow",                                    PathTier.SelfSlow),
        new("environment.tide.timeHigh",                                     PathTier.SelfSlow),
        new("environment.tide.timeLow",                                      PathTier.SelfSlow),
        new("environment.tide.stationName",                                  PathTier.SelfSlow),
        // Solar state string for auto night-mode. Published by
        // signalk-solar / signalk-sun-position: "day" / "dawn" /
        // "dusk" / "night". Servers without such a plugin get a
        // dormant auto toggle.
        new("environment.sun",                                               PathTier.SelfSlow),
        // Route-total progress (WP index/total, distance/TTG to end).
        // Per-leg fields are in SelfFast; these aggregate across the
        // route and only advance on a waypoint boundary.
        new("navigation.courseGreatCircle.activeRoute.distanceRemaining",    PathTier.SelfSlow),
        new("navigation.courseRhumbline.activeRoute.distanceRemaining",      PathTier.SelfSlow),
        new("navigation.courseGreatCircle.activeRoute.timeToGo",             PathTier.SelfSlow),
        new("navigation.courseRhumbline.activeRoute.timeToGo",               PathTier.SelfSlow),
        new("navigation.courseGreatCircle.activeRoute.pointIndex",           PathTier.SelfSlow),
        new("navigation.courseRhumbline.activeRoute.pointIndex",             PathTier.SelfSlow),
        new("navigation.courseGreatCircle.activeRoute.pointTotal",           PathTier.SelfSlow),
        new("navigation.courseRhumbline.activeRoute.pointTotal",             PathTier.SelfSlow),
    ];

    /// <summary>Back-compat view: every path the self-subscriptions
    /// cover (fast + slow). Used by <c>CoreSelfPaths</c> / RawStream's
    /// path-filter defaults.</summary>
    internal static IReadOnlyList<string> SelfPaths =>
        Paths.Where(p => p.Tier is PathTier.SelfFast or PathTier.SelfSlow)
             .Select(p => p.Path).ToArray();

    /// <summary>Back-compat view: only the slow tier. Subscription tests
    /// assert SlowSelfPaths doesn't leak fast-moving fields.</summary>
    internal static IReadOnlyList<string> SlowSelfPaths =>
        Paths.Where(p => p.Tier == PathTier.SelfSlow)
             .Select(p => p.Path).ToArray();

    /// <summary>Back-compat view: AIS tier. vessels.* matches self too,
    /// so these reach own-boat without appearing in SelfPaths.</summary>
    internal static IReadOnlyList<string> AisPaths =>
        Paths.Where(p => p.Tier == PathTier.Ais)
             .Select(p => p.Path).ToArray();

    /// <summary>
    /// Raised when navigation data changes. Subscribers must handle thread-safety themselves
    /// (e.g. InvokeAsync(StateHasChanged) in Blazor components).
    /// </summary>
    public event Action? OnDataChanged;

    /// <summary>
    /// Raised for every raw JSON message received from the websocket.
    /// </summary>
    public event Action<string>? OnRawMessage;

    /// <summary>
    /// Raised when connection status changes.
    /// </summary>
    public event Action? OnConnectionChanged;

    public NavigationData Data => _data;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// UTC ticks of the last websocket message received (any type, including heartbeats).
    /// Uses <see cref="Interlocked"/> for thread-safe cross-thread reads.
    /// </summary>
    private long _lastMessageTicks;

    /// <summary>
    /// Returns true when the websocket is open but no message has arrived
    /// for more than 5 seconds.
    /// </summary>
    public bool IsDataStale => IsConnected
        && (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMessageTicks)) > StaleDataThresholdSec * TimeSpan.TicksPerSecond;

    public SignalkClient(ISignalKBaseUrl baseUrl, ILogger<SignalkClient> logger,
        TrackBuffer track, AisStore ais, HttpClient http)
    {
        _logger = logger;
        _data = new NavigationData();
        _track = track;
        _ais = ais;
        _http = http;
        _baseUrl = baseUrl;
        _wsUri = baseUrl.StreamUri();
        _selfContext = "";
    }

    /// <summary>
    /// Starts the websocket receive loop. Called from Program.cs after host build.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        int backoffMs = InitialBackoffMs;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                _logger.LogInformation("Connecting to SignalK at {Uri}", _wsUri);
                await ws.ConnectAsync(_wsUri, ct);
                _ws = ws;
                _logger.LogInformation("Connected to SignalK");
                IsConnected = true;
                OnConnectionChanged?.Invoke();
                backoffMs = InitialBackoffMs;

                // Group the Paths registry by tier and issue one
                // subscription per tier. Each path lives in exactly
                // one tier now; no set math needed, no overlap to
                // strip. See PathTier docs for the context+period
                // story.
                //
                //   - SelfFast (1 Hz, vessels.self): nav / wind / depth
                //     / course next-point / autopilot / tidal current.
                //     Safety-critical / high-dynamic.
                //   - SelfSlow (10 s, vessels.self): anchor radii, tide,
                //     solar, route-total progress. Plugin-driven
                //     fields that change on the scale of minutes.
                //   - Ais (1 Hz, vessels.*): shared nav + AIS-specific.
                //     vessels.* matches self too, so position / SOG /
                //     COG / heading reach own-boat via the wildcard
                //     (no duplicate delivery).
                //
                // Extra paths added by the RawStream page use the
                // instant-with-minPeriod profile for live debugging;
                // they stay outside the registry because they're
                // runtime-discovered, not a shipped-with-app set.
                var byTier = Paths.ToLookup(p => p.Tier, p => p.Path);
                await SendSubscriptionAsync("vessels.self", byTier[PathTier.SelfFast]);
                await SendSubscriptionAsync("vessels.self", byTier[PathTier.SelfSlow],
                    periodMs: SlowSubscriptionPeriodMs);
                await SendSubscriptionAsync("vessels.*", byTier[PathTier.Ais]);
                if (_extraPaths.Count > 0)
                    await SendSubscriptionAsync("vessels.self", _extraPaths,
                        periodMs: RawStreamSubscriptionPeriodMs, policy: "instant");

                // SignalK "ideal" subscriptions only send deltas as values
                // change; static AIS data (names, MMSI) received BEFORE we
                // connected is never replayed over the stream. Seed those
                // from the REST snapshot so vessels show their name instead
                // of a bare MMSI on first paint. Best-effort -- any failure
                // just leaves names to trickle in via live deltas.
                _ = Task.Run(() => SeedVesselNamesFromRestAsync(ct), ct);

                // Identify ourselves via REST so we can filter own-boat out
                // of the AIS list even on servers that never emit a hello
                // with "self" or push own-boat only as "vessels.<urn>".
                _ = Task.Run(() => ResolveSelfContextFromRestAsync(ct), ct);

                var buffer = new byte[ReceiveBufferBytes];
                var messageBuffer = new StringBuilder();

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        ProcessMessage(messageBuffer.ToString());
                        messageBuffer.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalK connection lost, reconnecting in {BackoffMs}ms", backoffMs);
            }

            _ws = null;
            IsConnected = false;
            OnConnectionChanged?.Invoke();

            // Linked delay token: cancels either when the whole client
            // is disposed (ct) OR when the user asks for an immediate
            // retry via ReconnectNow (_backoffCts). The two dispositions
            // are distinguished inside the catch so we exit on disposal
            // but loop on user-retry with a reset backoff.
            _backoffCts?.Dispose();
            _backoffCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _backoffCts.Token);
            try
            {
                await Task.Delay(backoffMs, linked.Token);
                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;              // client disposed
            }
            catch (OperationCanceledException)
            {
                // User tapped Reconnect now -- break out of the delay and
                // retry immediately. Reset the backoff so the next drop
                // doesn't inherit a fast-retry cadence.
                backoffMs = InitialBackoffMs;
            }
        }
    }

    /// <summary>
    /// Cancel the current reconnect-backoff delay (if any) and retry
    /// immediately. No-op when already connected. Safe to call from UI
    /// threads; the receive loop handles the cancellation inline.
    /// </summary>
    public void ReconnectNow()
    {
        if (IsConnected) return;
        try { _backoffCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void ProcessMessage(string json)
    {
        Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);
        OnRawMessage?.Invoke(json);

        try
        {
            // Single parse: deserialize to SignalkDelta, then check for hello message.
            var delta = JsonSerializer.Deserialize<SignalkDelta>(json);

            // The hello message has no updates but contains "self" in the raw JSON.
            // SignalkDelta ignores unknown properties, so check if updates are present.
            if (delta?.Updates is null)
            {
                // Could be the hello message with "self" identifier.
                if (json.Contains("\"self\"", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("self", out var selfProp))
                    {
                        SetSelfContext(selfProp.GetString());
                    }
                }
                return;
            }

            // Own-boat identification. Own-boat data may arrive BEFORE the
            // hello message resolves _selfContext, or on servers that only
            // emit the URN form ("vessels.urn:mrn:imo:mmsi:..."). Match all
            // three shapes: empty, "vessels.self", and the normalised
            // _selfContext. SetSelfContext also retro-evicts any vessel
            // we stored in AIS before we knew who we were.
            bool isSelf = IsSelfContext(delta.Context);

            if (isSelf)
                ProcessSelfDelta(delta);
            else
                ProcessAisDelta(delta);
        }
        catch (JsonException)
        {
            // Ignore malformed messages.
        }
        catch (Exception ex)
        {
            // Don't let a single bad message crash the receive loop.
            _logger.LogWarning(ex, "Error processing SignalK message");
        }
    }

    /// <summary>
    /// Normalises the self identifier supplied by the server's hello
    /// message and retro-evicts any vessel we may have stored in
    /// <see cref="AisStore"/> for that context before we knew about it.
    /// Servers supply self either as "vessels.urn:mrn:imo:mmsi:..." OR
    /// just "urn:mrn:imo:mmsi:..."; we store the prefixed form so
    /// equality checks against delta contexts (always prefixed) match.
    /// </summary>
    internal void SetSelfContext(string? raw)
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

        // Retro-evict: if an AIS delta for this context arrived before
        // the hello (or from a server that never sends "vessels.self"),
        // we'll have own-boat sitting in the AIS list. Remove it and
        // blocklist so subsequent deltas can't re-add.
        _ais.Evict(_selfContext);
    }

    internal bool IsSelfContext(string? context)
    {
        if (string.IsNullOrEmpty(context)) return true;
        if (context == "vessels.self") return true;
        if (string.IsNullOrEmpty(_selfContext)) return false;
        return context == _selfContext;
    }

    private void ProcessSelfDelta(SignalkDelta delta)
    {
        bool changed = false;

        foreach (var update in delta.Updates!)
        {
            _data.SetTimestamp(update.Timestamp);

            if (update.Values is null)
                continue;

            foreach (var val in update.Values)
            {
                if (val.Path is null)
                    continue;

                // Mayara radar ARPA targets come in under vessels.self on
                // paths like radars.<rid>.targets.<tid>.position. Route
                // each target field into AisStore with a synthesised
                // radar.<rid>.<tid> context so the alarm pipeline treats
                // them uniformly with AIS vessels.
                if (val.Path.StartsWith("radars.", StringComparison.Ordinal))
                {
                    RouteRadarDelta(val.Path, val.Value);
                    continue;
                }

                if (val.Path == "navigation.position" && val.Value is JsonElement posEl
                    && posEl.ValueKind == JsonValueKind.Object)
                {
                    if (posEl.TryGetProperty("latitude", out var lat)
                        && posEl.TryGetProperty("longitude", out var lon)
                        && lat.ValueKind == JsonValueKind.Number
                        && lon.ValueKind == JsonValueKind.Number)
                    {
                        _data.ApplyPosition(lat.GetDouble(), lon.GetDouble());
                        changed = true;
                    }
                    continue;
                }

                // Anchor position from signalk-anchoralarm-plugin. Three
                // delta shapes we observed from the plugin in the field:
                //   1. { lat, lon } object   -> drop/update anchor
                //   2. explicit JSON null    -> anchor weighed on another
                //      plotter ("Weigh Anchor" button in the plugin UI)
                //   3. value = null (not a JsonElement at all) -- same
                //      as case 2 but surfaced by some SK server versions
                //      as a property-missing rather than JSON null.
                // User-reported bug: anchor stayed on ONA after being
                // cleared in freeboard-sk because only case 1 was
                // handled. All three now clear our side.
                if (val.Path == "navigation.anchor.position")
                {
                    if (val.Value is JsonElement anchorEl)
                    {
                        if (anchorEl.ValueKind == JsonValueKind.Object
                            && anchorEl.TryGetProperty("latitude", out var aLat)
                            && anchorEl.TryGetProperty("longitude", out var aLon)
                            && aLat.ValueKind == JsonValueKind.Number
                            && aLon.ValueKind == JsonValueKind.Number)
                        {
                            _data.ApplyAnchorPosition(aLat.GetDouble(), aLon.GetDouble());
                            changed = true;
                        }
                        else if (anchorEl.ValueKind == JsonValueKind.Null)
                        {
                            _data.ClearAnchor();
                            changed = true;
                        }
                    }
                    else if (val.Value is null)
                    {
                        _data.ClearAnchor();
                        changed = true;
                    }
                    continue;
                }

                // Plugins also deactivate by nulling maxRadius (the alarm
                // is only armed when both position + radius are set).
                // Treat a null maxRadius as "anchor no longer armed" too,
                // covering servers that don't re-emit a null position on
                // deactivation.
                if (val.Path == "navigation.anchor.maxRadius" && IsNullDelta(val.Value))
                {
                    _data.ClearAnchor();
                    changed = true;
                    continue;
                }

                // Course next-point position (lat/lon object).
                if ((val.Path == "navigation.courseGreatCircle.nextPoint.position"
                    || val.Path == "navigation.courseRhumbline.nextPoint.position")
                    && val.Value is JsonElement wpEl
                    && wpEl.ValueKind == JsonValueKind.Object)
                {
                    if (wpEl.TryGetProperty("latitude", out var wpLat)
                        && wpEl.TryGetProperty("longitude", out var wpLon)
                        && wpLat.ValueKind == JsonValueKind.Number
                        && wpLon.ValueKind == JsonValueKind.Number)
                    {
                        _data.ApplyCourseNextPointPosition(wpLat.GetDouble(), wpLon.GetDouble());
                        changed = true;
                    }
                    continue;
                }

                // Course previous-point position (lat/lon object).
                if ((val.Path == "navigation.courseGreatCircle.previousPoint.position"
                    || val.Path == "navigation.courseRhumbline.previousPoint.position")
                    && val.Value is JsonElement prevWpEl
                    && prevWpEl.ValueKind == JsonValueKind.Object)
                {
                    if (prevWpEl.TryGetProperty("latitude", out var prevWpLat)
                        && prevWpEl.TryGetProperty("longitude", out var prevWpLon)
                        && prevWpLat.ValueKind == JsonValueKind.Number
                        && prevWpLon.ValueKind == JsonValueKind.Number)
                    {
                        _data.ApplyCoursePreviousPointPosition(prevWpLat.GetDouble(), prevWpLon.GetDouble());
                        changed = true;
                    }
                    continue;
                }

                // String-valued paths (route href, route name).
                if (val.Value is JsonElement strEl && strEl.ValueKind == JsonValueKind.String)
                {
                    if (_data.ApplyString(val.Path, strEl.GetString()))
                    {
                        changed = true;
                        continue;
                    }
                }

                // Route deactivation: href arrives as null.
                if ((val.Path == "navigation.courseGreatCircle.activeRoute.href"
                    || val.Path == "navigation.courseRhumbline.activeRoute.href")
                    && (val.Value is null
                        || (val.Value is JsonElement nullEl && nullEl.ValueKind == JsonValueKind.Null)))
                {
                    _data.ClearCourse();
                    changed = true;
                    continue;
                }

                if (_data.Apply(val.Path, val.Value))
                    changed = true;
            }
        }

        if (changed)
        {
            if (_data.Latitude is not null && _data.Longitude is not null)
            {
                _track.Add(new TrackPoint(
                    DateTime.UtcNow,
                    _data.Latitude.Value,
                    _data.Longitude.Value,
                    _data.SpeedOverGround,
                    _data.CourseOverGround,
                    _data.Heading,
                    _data.WindAngleApparent,
                    _data.WindSpeedApparent,
                    _data.WindAngleTrue,
                    _data.WindSpeedTrue,
                    _data.Depth));
            }

            OnDataChanged?.Invoke();
        }
    }

    private void ProcessAisDelta(SignalkDelta delta)
    {
        foreach (var update in delta.Updates!)
        {
            if (update.Values is null)
                continue;

            foreach (var val in update.Values)
            {
                if (val.Path is null)
                    continue;

                _ais.Apply(delta.Context!, val.Path, val.Value);
            }
        }
    }

    /// <summary>
    /// Converts a raw Mayara radar-target delta path like
    /// <c>radars.&lt;radarId&gt;.targets.&lt;targetId&gt;.&lt;field&gt;</c>
    /// into a synthesised AIS context plus a short path name, then
    /// dispatches to <see cref="AisStore"/>. Unknown shapes are dropped
    /// silently; malformed ones should not crash the receive loop.
    /// </summary>
    private void RouteRadarDelta(string path, object? value)
    {
        if (TryParseRadarTargetPath(path) is not var (radarId, targetId, field)) return;
        string ctx = $"{AisStore.RadarContextPrefix}{radarId}.{targetId}";
        _ais.Apply(ctx, field, value);
    }

    /// <summary>
    /// Parses <c>radars.&lt;radarId&gt;.targets.&lt;targetId&gt;.&lt;field&gt;</c>
    /// by splitting on the literal <c>.targets.</c> separator instead of on
    /// <c>.</c>, so radar IDs containing dots (e.g. an IPv4-style hardware
    /// identifier) parse correctly. Returns <c>null</c> for anything that
    /// doesn't match the expected shape.
    /// </summary>
    internal static (string radarId, string targetId, string field)? TryParseRadarTargetPath(string path)
    {
        if (!path.StartsWith("radars.", StringComparison.Ordinal)) return null;

        const string sep = ".targets.";
        int sepIdx = path.IndexOf(sep, StringComparison.Ordinal);
        if (sepIdx < 0) return null;

        string radarId = path.Substring("radars.".Length, sepIdx - "radars.".Length);
        if (radarId.Length == 0) return null;

        string after = path[(sepIdx + sep.Length)..];
        int dot = after.IndexOf('.');
        if (dot <= 0 || dot == after.Length - 1) return null;

        string targetId = after[..dot];
        string field = after[(dot + 1)..];
        return (radarId, targetId, field);
    }

    /// <summary>
    /// Subscribes to additional paths on the active WebSocket (used by RawStream).
    /// The path is remembered and re-applied on reconnect.
    /// </summary>
    public async Task SubscribeExtraPathAsync(string path)
    {
        if (_extraPaths.Add(path))
            await SendSubscriptionAsync("vessels.self", [path],
                periodMs: RawStreamSubscriptionPeriodMs, policy: "instant");
    }

    /// <summary>
    /// Unsubscribes from a previously added extra path.
    /// </summary>
    public async Task UnsubscribeExtraPathAsync(string path)
    {
        if (_extraPaths.Remove(path))
            await SendUnsubscribeAsync("vessels.self", [path]);
    }

    public IReadOnlyCollection<string> CoreSelfPaths => SelfPaths;

    /// <summary>
    /// Sends a SignalK subscribe request with a per-path period + policy.
    /// Defaults are the <see cref="StandardSubscriptionPeriodMs"/> + "ideal"
    /// profile, which caps updates at 1 Hz and lets the server coalesce
    /// bursts. RawStream usage overrides with a shorter period and "instant"
    /// policy so raw deltas keep flowing.
    /// </summary>
    private async Task SendSubscriptionAsync(string context, IEnumerable<string> paths,
        int periodMs = StandardSubscriptionPeriodMs, string policy = "ideal")
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;

        var message = JsonSerializer.Serialize(new
        {
            context,
            subscribe = paths.Select(p => new
            {
                path = p,
                period = periodMs,
                policy,
            })
        });
        await SendRawAsync(message);
    }

    private async Task SendUnsubscribeAsync(string context, IEnumerable<string> paths)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;

        var message = JsonSerializer.Serialize(new
        {
            context,
            unsubscribe = paths.Select(p => new { path = p })
        });
        await SendRawAsync(message);
    }

    private async Task SendRawAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_ws is null || _ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            var ct = _cts?.Token ?? CancellationToken.None;
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// One-shot REST fetch of every vessel known to the server, walking the
    /// tree for static-data leaves (name / mmsi / callsign / ship type /
    /// position) and feeding them to <see cref="AisStore"/> as if they had
    /// arrived on the delta stream. Bridges the gap where SignalK's
    /// subscription stream only sends values as they change.
    /// <para>
    /// Never throws: any network or parse failure is logged at Warning.
    /// </para>
    /// </summary>
    /// <summary>
    /// Asks SignalK who "self" is by hitting /signalk/v1/api/self. That
    /// endpoint returns just the self URN (as a JSON string), which
    /// lets us tag own-boat even when the delta stream never publishes
    /// the hello envelope or uses only the prefixed-URN form.
    /// </summary>
    private async Task ResolveSelfContextFromRestAsync(CancellationToken ct)
    {
        try
        {
            var url = _baseUrl.Combine("/signalk/v1/api/self");
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return;
            var raw = (await res.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(raw)) SetSelfContext(raw);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REST self-context resolution failed");
        }
    }

    private async Task SeedVesselNamesFromRestAsync(CancellationToken ct)
    {
        try
        {
            var url = _baseUrl.Combine("/signalk/v1/api/vessels");
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return;

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            int seeded = 0;
            foreach (var vesselProp in doc.RootElement.EnumerateObject())
            {
                if (ct.IsCancellationRequested) break;
                // Each property key is the short vessel id ("urn:mrn:imo:mmsi:...");
                // AIS contexts on the delta stream use "vessels." prefix.
                string context = vesselProp.Name.StartsWith("vessels.", StringComparison.Ordinal)
                    ? vesselProp.Name
                    : $"vessels.{vesselProp.Name}";
                if (ApplyRestVesselTree(context, vesselProp.Value)) seeded++;
            }
            if (seeded > 0)
                _logger.LogInformation("Seeded static data for {Count} vessels from REST snapshot", seeded);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REST vessel-snapshot seed failed (names will trickle in via deltas)");
        }
    }

    /// <summary>
    /// Walks the value tree for a single vessel and forwards the leaves
    /// <see cref="AisVessel.Apply"/> understands. Returns true if any leaf
    /// was applied so the caller can count "interesting" vessels.
    /// </summary>
    private bool ApplyRestVesselTree(string context, JsonElement vessel)
    {
        if (vessel.ValueKind != JsonValueKind.Object) return false;
        bool any = false;

        // SignalK wraps leaf values as { "value": ..., "timestamp": ... }
        // -- so "name" might be under vessel.name.value or vessel.name
        // depending on the server. Try both shapes.
        any |= TryApplyScalar(context, "name", vessel, "name");
        any |= TryApplyScalar(context, "mmsi", vessel, "mmsi");
        if (vessel.TryGetProperty("communication", out var comm)
            && comm.ValueKind == JsonValueKind.Object)
        {
            any |= TryApplyScalar(context, "communication.callsignVhf", comm, "callsignVhf");
        }
        if (vessel.TryGetProperty("design", out var design)
            && design.ValueKind == JsonValueKind.Object
            && design.TryGetProperty("aisShipType", out var typeWrap))
        {
            var typeVal = UnwrapValue(typeWrap);
            if (typeVal.ValueKind != JsonValueKind.Undefined && typeVal.ValueKind != JsonValueKind.Null)
            {
                _ais.Apply(context, "design.aisShipType", typeVal);
                any = true;
            }
        }
        if (vessel.TryGetProperty("navigation", out var nav)
            && nav.ValueKind == JsonValueKind.Object
            && nav.TryGetProperty("position", out var posWrap))
        {
            var posVal = UnwrapValue(posWrap);
            if (posVal.ValueKind == JsonValueKind.Object)
            {
                _ais.Apply(context, "navigation.position", posVal);
                any = true;
            }
        }
        return any;
    }

    private bool TryApplyScalar(string context, string path, JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var wrap)) return false;
        var val = UnwrapValue(wrap);
        if (val.ValueKind != JsonValueKind.String) return false;
        _ais.Apply(context, path, val);
        return true;
    }

    // Returns the wrapped .value if this is a SignalK leaf, otherwise the
    // element itself. Some servers publish bare scalars; most wrap them.
    private static JsonElement UnwrapValue(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("value", out var inner))
            return inner;
        return el;
    }

    // True if the value in a SignalK delta represents "no data here" --
    // either a literal JSON null, or a C# null deserialized as such.
    // Used by anchor / course deactivation handlers to treat both wire
    // shapes uniformly.
    private static bool IsNullDelta(object? value)
    {
        if (value is null) return true;
        if (value is JsonElement el && el.ValueKind == JsonValueKind.Null) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; }
            catch (OperationCanceledException) { }
        }

        _backoffCts?.Dispose();
        _sendLock.Dispose();
    }
}
