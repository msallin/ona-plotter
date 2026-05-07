using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OnaPlotter.Components;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Shared HttpClient used by every *Api. The 8-second timeout caps the
// .NET default of 100s so a half-baked TLS handshake on flaky LTE / a
// dropped sat link surfaces as a fast Toast.Error instead of a frozen
// UI -- Stop Nav, Drop / Raise Anchor, Activate Route, MOB position
// write would otherwise sit on a dead socket up to 100s before the
// helm's tap registers a failure. Per-call timeouts on hot paths
// (NotificationsApi.CallTimeout) override this when they need a tighter
// budget; longer flows (TrackApi history pages, GPX import) pass an
// explicit CancellationToken with their own deadline.
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(8) });

// Storage + settings.
builder.Services.AddSingleton<IKeyValueStore, LocalStorageKeyValueStore>();
builder.Services.AddSingleton<IAppSettings, AppSettingsService>();
// IAppSettings extends seven narrow ISP-carve-out interfaces from
// Services/Settings/. Components that only need a slice can inject
// just that slice (smaller test doubles, narrower change-amplification
// surface). The DI container doesn't auto-forward sub-interfaces, so
// register each narrow one as a forwarder to the same singleton.
// Pattern: every interface IAppSettings inherits from gets one line
// here. Forwarding ensures both wide and narrow consumers see the
// SAME instance (settings persist across all of them).
builder.Services.AddSingleton<OnaPlotter.Services.Settings.IAlarmThresholds>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<OnaPlotter.Services.Settings.IThemeSettings>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<OnaPlotter.Services.Settings.IMapDisplaySettings>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<OnaPlotter.Services.Settings.INavPreferences>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<OnaPlotter.Services.Settings.IChartSettings>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<OnaPlotter.Services.Settings.IPersistedView>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<OnaPlotter.Services.Settings.IWindPageSettings>(
    sp => sp.GetRequiredService<IAppSettings>());
// In-progress route-edit snapshots survive a page reload via
// localStorage. The store is consulted on app start so a save that
// failed mid-edit (no network, not logged in, accidental refresh)
// can be recovered rather than silently lost.
builder.Services.AddSingleton<IRouteDraftStore, RouteDraftStore>();
// Detected once per session (navigator.hardwareConcurrency + UA).
// Drives renderer / tile-prefetch decisions; no user-facing knob.
builder.Services.AddSingleton<IClientCapabilities, ClientCapabilitiesService>();

// Domain state.
builder.Services.AddSingleton<TrackBuffer>();
builder.Services.AddSingleton<AisStore>();

// UI services.
builder.Services.AddSingleton<IToastService, ToastService>();
// Wraps the platform fileTransfer.js shareOrCopy helper + outcome
// toast handling so popup-side "Share" buttons (waypoint, note, MOB)
// reuse a single failure / success path.
builder.Services.AddSingleton<OnaPlotter.Services.ShareService>();
builder.Services.AddSingleton<IConfirmationService, ConfirmationService>();
builder.Services.AddSingleton<IPolarService, PolarService>();
// Polls the server's version.g.js every 5 min and surfaces a
// "Reload to update" chip when the published bundle hash changes.
// The check fires fire-and-forget; a 404 / offline / timeout is
// swallowed and retried on the next tick.
builder.Services.AddSingleton<OnaPlotter.Services.UpdateChecker>();
// Page-state caches: keep the helm's last range / loaded data on
// History + Stats so navigating to /map and back doesn't reset
// the view. Singleton-scoped (one per circuit); cleared on a
// hard reload.
builder.Services.AddSingleton<OnaPlotter.Services.State.StatsPageState>();
builder.Services.AddSingleton<OnaPlotter.Services.State.HistoryPageState>();
// Server-side SignalK notifications store. SignalkClient pushes
// notifications.* deltas here; the rule below reads the active set
// every Evaluate tick and surfaces each as an alarm-banner entry.
builder.Services.AddSingleton<OnaPlotter.Services.ServerNotifications.ServerNotificationStore>();
// AIS Aids to Navigation. atons.* deltas land here; the map
// renderer subscribes to OnAtonsUpdated and pushes the snapshot
// to Leaflet. Static enough to live on a 60s subscription tier.
builder.Services.AddSingleton<OnaPlotter.Services.AtonStore>();
// Alarm rules are DI-registered; AlarmManager picks them up via
// IEnumerable<IAlarmRule>. Adding a new rule is a one-line registration.
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.AisSartAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.ShallowAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.AnchorTideAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.AnchorDragAlarmRule>();
// Shared moored-vessel classifier. Singleton so CpaAlarmRule and the
// Harbor-mode filter in Map.razor.PushAisTargets see the same dwell
// state and the same SK navigation.state interpretation.
builder.Services.AddSingleton<OnaPlotter.Services.IMooredVesselTracker, OnaPlotter.Services.MooredVesselTracker>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.CpaAlarmRule>();
// Region store is a thin singleton AisStore-shaped: Map.razor pushes
// regions in on every resource refresh; HazardousRegionAlarmRule reads
// the snapshot per tick. Threaded as a separate dependency rather than
// added to AlarmEvaluationContext so the context shape stays minimal
// for every other rule that doesn't care about regions.
builder.Services.AddSingleton<OnaPlotter.Services.IRegionStore, OnaPlotter.Services.RegionStore>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.HazardousRegionAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.WindShiftAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.WaypointApproachAlarmRule>();
// Bridge rule needs the v2 notifications API (to attach an
// IAlarmAcknowledger to each AlarmInfo it emits) and the published-
// alarms tracker (to skip our own publication echoes). Both are
// optional on the rule's ctor for backward compat with legacy test
// constructors that don't wire them; production DI threads them in.
builder.Services.AddSingleton<IAlarmRule>(sp =>
    new OnaPlotter.Services.Alarms.ServerNotificationsAlarmRule(
        sp.GetRequiredService<OnaPlotter.Services.ServerNotifications.ServerNotificationStore>(),
        sp.GetRequiredService<OnaPlotter.Services.Api.INotificationsApi>(),
        sp.GetRequiredService<OnaPlotter.Services.Alarms.IPublishedAlarmTracker>()));
builder.Services.AddSingleton<DeadmanTracker>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.DeadmanAlarmRule>();
builder.Services.AddSingleton<IAlarmManager, AlarmManager>();
// Phase B: cross-plotter publish. Tracker is the read-side that the
// bridge rule queries to avoid echoing our own publications back into
// the banner; AlarmPublisher is the writer that POSTs raises/clears
// to the SK server when the local alarm stack changes. Resolved
// post-build so subscription wiring happens once at startup.
builder.Services.AddSingleton<OnaPlotter.Services.Alarms.PublishedAlarmTracker>();
builder.Services.AddSingleton<OnaPlotter.Services.Alarms.IPublishedAlarmTracker>(
    sp => sp.GetRequiredService<OnaPlotter.Services.Alarms.PublishedAlarmTracker>());
builder.Services.AddSingleton<OnaPlotter.Services.Alarms.AlarmPublisher>();

// SignalK REST API clients (one per concern).
builder.Services.AddSingleton<ISignalKBaseUrl, SignalKBaseUrl>();
builder.Services.AddSingleton<IChartApi, ChartApi>();
builder.Services.AddSingleton<IRouteApi, RouteApi>();
builder.Services.AddSingleton<IWaypointApi, WaypointApi>();
builder.Services.AddSingleton<INoteApi, NoteApi>();
builder.Services.AddSingleton<IRegionApi, RegionApi>();
builder.Services.AddSingleton<ICourseApi, CourseApi>();
// SignalK v2 notifications client. Drives cross-plotter alarm sync
// via Acknowledge / Silence and (Phase B) lets client-side rules
// publish their alarms back to SK so other plotters can see them.
builder.Services.AddSingleton<INotificationsApi, NotificationsApi>();

// Persistable cache of resolved MOB positions (serverId -> lat/lon).
// signalk-server discards the position from the /mob POST body so
// this client-side cache is what survives a reload and lets the
// chart marker reappear on the next session.
builder.Services.AddSingleton<OnaPlotter.Services.Mob.ResolvedPositionStore>();

// Local-first MOB pipeline. Synthesises a notification into the
// ServerNotificationStore + queues a background POST that retries
// until the server confirms. The alarm pipeline wakes up via
// MainLayout's subscription to ServerNotificationStore.OnPathChanged
// (no separate callback needed -- the store fires synchronously
// from inside Apply / Clear).
builder.Services.AddSingleton<OnaPlotter.Services.Mob.IMobService, OnaPlotter.Services.Mob.MobService>();

// Drives the MOB chart marker from store changes. Singleton so the
// subscription survives page navigation and the marker reappears
// when the Map remounts. The factory thunk for OwnMmsi reads the
// SignalkClient.OwnMmsi at the moment setMob fires (not at
// construction time) so a delayed self-context resolution still
// makes the helm vessel's MMSI land in the popup.
builder.Services.AddSingleton<OnaPlotter.Services.Mob.MobChartRenderer>(sp =>
    new OnaPlotter.Services.Mob.MobChartRenderer(
        sp.GetRequiredService<OnaPlotter.Services.ServerNotifications.ServerNotificationStore>(),
        () => sp.GetRequiredService<SignalkClient>().OwnMmsi));
builder.Services.AddSingleton<IAutopilotApi, AutopilotApi>();
// Optional: signalk-anchoralarm-plugin. Endpoint 404s when the plugin
// isn't installed; the map surfaces that as a toast rather than failing
// the app startup.
builder.Services.AddSingleton<IAnchorAlarmApi, AnchorAlarmApi>();
builder.Services.AddSingleton<IPathApi, PathApi>();
// Auth probe for the not-logged-in banner. signalk-server's
// /skServer/loginStatus is the de-facto source of truth for "can
// this session write?"; banner reads from it and hides when the
// server has security disabled (open homelab / dev installs).
builder.Services.AddSingleton<IAuthApi, AuthApi>();
builder.Services.AddSingleton<ITrackApi, TrackApi>();
// Signal K Radar API v3.1. Optional; empty list when no provider plugin.
builder.Services.AddSingleton<IRadarApi, RadarApi>();
// Radar overlay: host wraps the JS module; manager owns session
// state (capabilities cache, sticky-off prefs, last-range cache,
// auto-toggle decisions). The page injects the manager and calls
// into it; the host's module ref is wired by the page after the
// JS module loads.
builder.Services.AddSingleton<OnaPlotter.Services.Radar.JsRadarOverlayHost>();
builder.Services.AddSingleton<OnaPlotter.Services.Radar.IRadarOverlayHost>(
    sp => sp.GetRequiredService<OnaPlotter.Services.Radar.JsRadarOverlayHost>());
builder.Services.AddSingleton<OnaPlotter.Services.Radar.RadarOverlayManager>();
// Optional: detects sbender9/signalk-buddylist-plugin at runtime.
builder.Services.AddSingleton<IBuddyListApi, BuddyListApi>();

// Wall-clock provider. Stale-data detection and track-point timestamps go
// through this so tests can advance time deterministically (FakeTimeProvider
// from Microsoft.Extensions.TimeProvider.Testing) instead of sleeping.
builder.Services.AddSingleton(TimeProvider.System);

// SignalK WebSocket delta stream.
builder.Services.AddSingleton<SignalkClient>();

// Time-windowed averages over the nav channels (TWS / AWS / SOG /
// VMG / COG / TWD). Singleton so the buffers fill across page
// navigations and every consumer (chart HUD, WindRose, future trend
// chips) reads from the same series. Subscribes to OnDataChanged in
// its ctor; resolving once at startup wires the sampler.
builder.Services.AddSingleton<OnaPlotter.Services.INavigationAverages, OnaPlotter.Services.NavigationAverages>();

// Client-error relay to the SignalK plugin's /log endpoint. Makes
// iPad Safari exceptions visible in the SignalK server log for
// SSH-based debugging at the helm. The actual install happens in
// wwwroot/js/errorRelayBoot.js (loaded synchronously from index.html
// BEFORE the Blazor runtime, so it catches boot-time exceptions);
// this DI singleton is the C# entry-point for code that wants to
// relay a caught exception explicitly. See Services/ClientErrorRelay.cs.
builder.Services.AddSingleton<ClientErrorRelay>();

// Single source of truth for resources.{routes,waypoints,notes,regions}.
// Composes over SignalkClient.OnResourceDelta (WS push) and the four
// *Api REST clients (initial load + reconcile-on-reconnect). Map.razor
// and Resources.razor read from here and subscribe to typed change
// events; this closes the multi-plotter sync gap where a route edited
// on plotter A used to never appear on plotter B until a manual reload.
builder.Services.AddSingleton<OnaPlotter.Services.Resources.ResourceStore>();

// Place search (topbar geocoder). The helm-facing IPlaceSearchService
// is a Merged(Own + Caching(Fallback(Photon -> Nominatim))) composition:
//
//   helm types -> SearchBox calls IPlaceSearchService
//   IPlaceSearchService = MergedPlaceSearchService
//     own-data branch: OwnPlacesIndex (in-mem substring match
//                       over waypoints + notes + regions, lazy-loaded
//                       via the *Api singletons, 5-minute TTL)
//     online branch:   CachingPlaceSearchService(
//                          FallbackPlaceSearchService(
//                              primary   = PhotonPlaceSearchService,
//                              secondary = NominatimPlaceSearchService))
//                       cache hit  -> immediate; miss -> Photon HTTP;
//                       Photon empty / down -> Nominatim HTTP (rate-
//                       limited to <= 1 rps per OSMF policy).
//
// Own-data + online run in parallel; the helm sees own-data hits even
// when both geocoders are slow / offline. All transient failures
// return an empty list per the IPlaceSearchService contract.
// Photon takes a position-provider thunk so each search call can
// add &lat=&lon= to bias results by proximity to the helm's current
// fix. The thunk reads NavigationData live each call -- if the SK
// feed hasn't yielded a position yet, returns null and Photon skips
// the bias params (global ranking).
builder.Services.AddSingleton<OnaPlotter.Services.Places.PhotonPlaceSearchService>(sp =>
    new OnaPlotter.Services.Places.PhotonPlaceSearchService(
        sp.GetRequiredService<HttpClient>(),
        sp.GetRequiredService<ILogger<OnaPlotter.Services.Places.PhotonPlaceSearchService>>(),
        () =>
        {
            var nav = sp.GetRequiredService<SignalkClient>().Data;
            return nav.Latitude is double lat && nav.Longitude is double lon
                ? (lat, lon)
                : null;
        }));
builder.Services.AddSingleton<OnaPlotter.Services.Places.NominatimPlaceSearchService>();
builder.Services.AddSingleton<OnaPlotter.Services.Places.PlaceSearchCache>();
builder.Services.AddSingleton<OnaPlotter.Services.Places.OwnPlacesIndex>();
builder.Services.AddSingleton<OnaPlotter.Services.Places.IPlaceSearchService>(sp =>
{
    var fallback = new OnaPlotter.Services.Places.FallbackPlaceSearchService(
        primary:   sp.GetRequiredService<OnaPlotter.Services.Places.PhotonPlaceSearchService>(),
        secondary: sp.GetRequiredService<OnaPlotter.Services.Places.NominatimPlaceSearchService>());
    var caching = new OnaPlotter.Services.Places.CachingPlaceSearchService(
        fallback,
        sp.GetRequiredService<OnaPlotter.Services.Places.PlaceSearchCache>());
    return new OnaPlotter.Services.Places.MergedPlaceSearchService(
        sp.GetRequiredService<OnaPlotter.Services.Places.OwnPlacesIndex>(),
        caching);
});

var host = builder.Build();

// Kick off the WebSocket loop (no IHostedService in Blazor WASM).
var signalkClient = host.Services.GetRequiredService<SignalkClient>();
_ = signalkClient.StartAsync();

// Resolve ResourceStore at startup so its OnResourceDelta + OnConnectionChanged
// subscriptions are attached to SignalkClient BEFORE the WS pump starts
// delivering frames. Kick off the initial REST reconcile in the background;
// pages that need data poll IsLoaded or subscribe to OnReloaded.
var resourceStore = host.Services.GetRequiredService<OnaPlotter.Services.Resources.ResourceStore>();
_ = resourceStore.RefreshAllAsync();

// Activate the cross-plotter alarm publisher. Resolving the singleton
// runs the constructor which subscribes to IAlarmManager.OnAlarmsChanged;
// without this line the type would never be instantiated (no other
// component injects it -- the bridge rule injects the tracker, not
// the publisher) and locally-emitted alarms would never reach other
// plotters. Stashed in a discard so the GC keeps the subscription alive.
_ = host.Services.GetRequiredService<OnaPlotter.Services.Alarms.AlarmPublisher>();

// NavigationAverages must resolve at startup too -- its ctor wires
// the OnDataChanged subscription, and no component injects it until
// the chart HUD is mounted. Without this kick the rolling buffers
// stay empty until the helm navigates to /map, which means the
// smoothed values would only start filling on first chart-page
// visit instead of from the moment the WS connects.
_ = host.Services.GetRequiredService<OnaPlotter.Services.INavigationAverages>();

await host.RunAsync();
