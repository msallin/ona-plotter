using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OnaPlotter.Components;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Auth;
using OnaPlotter.Services.Mob;
using OnaPlotter.Services.Places;
using OnaPlotter.Services.Pois;
using OnaPlotter.Services.Radar;
using OnaPlotter.Services.Resources;
using OnaPlotter.Services.ServerNotifications;
using OnaPlotter.Services.Settings;
using OnaPlotter.Services.Startup;
using OnaPlotter.Services.State;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Token store: holds the JWT returned by /signalk/v1/auth/login plus
// its expiry. Singleton so AuthHandler (HTTP) and SignalkClient (WS)
// see the same value and a logout from one path drops it for both.
// Registered BEFORE HttpClient because the AuthHandler factory pulls
// it out of the service provider.
builder.Services.AddSingleton<ITokenStore, TokenStore>();

// Shared HttpClient used by every *Api. The 8-second timeout caps the
// .NET default of 100s so a half-baked TLS handshake on flaky LTE / a
// dropped sat link surfaces as a fast Toast.Error instead of a frozen
// UI - Stop Nav, Drop / Raise Anchor, Activate Route, MOB position
// write would otherwise sit on a dead socket up to 100s before the
// helm's tap registers a failure. Per-call timeouts on hot paths
// (NotificationsApi.CallTimeout) override this when they need a tighter
// budget; longer flows (TrackApi history pages, GPX import) pass an
// explicit CancellationToken with their own deadline.
//
// Wrapped in AuthHandler so every outgoing REST call automatically
// attaches Authorization: Bearer when a token is set. The platform-
// default HttpClientHandler routes through the browser fetch API in
// WASM (no separate WebAssemblyHttpHandler hookup needed - the runtime
// substitutes it under the hood when HttpClientHandler is constructed
// in a WASM context).
builder.Services.AddSingleton(sp =>
{
    var auth = new AuthHandler(sp.GetRequiredService<ITokenStore>())
    {
        InnerHandler = new HttpClientHandler(),
    };
    return new HttpClient(auth) { Timeout = TimeSpan.FromSeconds(8) };
});

// Storage + settings.
builder.Services.AddSingleton<IKeyValueStore, LocalStorageKeyValueStore>();
builder.Services.AddSingleton<IAppSettings, AppSettingsService>();
// Narrow settings slices that components inject instead of the whole
// IAppSettings; each forwards to the same singleton.
builder.Services.AddSingleton<IMapDisplaySettings>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<INavPreferences>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<IChartSettings>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<IMarinePoiSettings>(
    sp => sp.GetRequiredService<IAppSettings>());
builder.Services.AddSingleton<IServerSettings>(
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
builder.Services.AddSingleton<ShareService>();
builder.Services.AddSingleton<IConfirmationService, ConfirmationService>();
builder.Services.AddSingleton<IPolarService, PolarService>();
builder.Services.AddSingleton<UpdateChecker>();
// Page-state caches so History / Stats keep their range and data across navigation.
builder.Services.AddSingleton<StatsPageState>();
builder.Services.AddSingleton<HistoryPageState>();
builder.Services.AddSingleton<ServerNotificationStore>();
builder.Services.AddSingleton<AtonStore>();
// Alarm rules are DI-registered; AlarmManager picks them up via
// IEnumerable<IAlarmRule>. Adding a new rule is a one-line registration.
builder.Services.AddSingleton<IAlarmRule, AisSartAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, ShallowAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, AnchorTideAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, AnchorDragAlarmRule>();
// One moored-vessel classifier shared by CpaAlarmRule and the Harbor-mode filter.
builder.Services.AddSingleton<IMooredVesselTracker, MooredVesselTracker>();
builder.Services.AddSingleton<IAlarmRule, CpaAlarmRule>();
// RegionStore subscribes to ResourceStore on construction, so
// HazardousRegionAlarmRule sees fresh regions with Map unmounted.
builder.Services.AddSingleton<RegionStore>();
builder.Services.AddSingleton<IRegionStore>(
    sp => sp.GetRequiredService<RegionStore>());
builder.Services.AddSingleton<IAlarmRule, HazardousRegionAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, WindShiftAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, WaypointApproachAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, ServerNotificationsAlarmRule>();
builder.Services.AddSingleton<DeadmanTracker>();
builder.Services.AddSingleton<IAlarmRule, DeadmanAlarmRule>();
builder.Services.AddSingleton<IAlarmManager, AlarmManager>();
// Cross-plotter publish: the tracker lets ServerNotificationsAlarmRule skip our
// own publications; AlarmPublisher POSTs raises / clears to the SK server.
builder.Services.AddSingleton<PublishedAlarmTracker>();
builder.Services.AddSingleton<IPublishedAlarmTracker>(
    sp => sp.GetRequiredService<PublishedAlarmTracker>());
builder.Services.AddSingleton<AlarmPublisher>();
// Alarm evaluation (data changes + 1 Hz timer) and waypoint auto-advance
// run as services, independent of any mounted component. SafetyBootstrap
// resolves them so their subscriptions exist from startup.
builder.Services.AddSingleton<AlarmEvaluationDriver>();
builder.Services.AddSingleton<AutoAdvanceService>();

// SignalK REST API clients (one per concern).
builder.Services.AddSingleton<ISignalKBaseUrl, SignalKBaseUrl>();
builder.Services.AddSingleton<IChartApi, ChartApi>();
builder.Services.AddSingleton<IRouteApi, RouteApi>();
builder.Services.AddSingleton<IWaypointApi, WaypointApi>();
builder.Services.AddSingleton<INoteApi, NoteApi>();
builder.Services.AddSingleton<IRegionApi, RegionApi>();
builder.Services.AddSingleton<ICourseApi, CourseApi>();
builder.Services.AddSingleton<INotificationsApi, NotificationsApi>();

// Survives a reload so the MOB chart marker reappears (signalk-server drops the POSTed position).
builder.Services.AddSingleton<ResolvedPositionStore>();

builder.Services.AddSingleton<IWaypointReader>(
    sp => sp.GetRequiredService<ResourceStore>());
builder.Services.AddSingleton<IMobService, MobService>();

builder.Services.AddSingleton<IAutopilotApi, AutopilotApi>();
// Optional: signalk-anchoralarm-plugin. Endpoint 404s when the plugin
// isn't installed; the map surfaces that as a toast rather than failing
// the app startup.
builder.Services.AddSingleton<IAnchorAlarmApi, AnchorAlarmApi>();
builder.Services.AddSingleton<IPathApi, PathApi>();
builder.Services.AddSingleton<IAuthApi, AuthApi>();
builder.Services.AddSingleton<AuthSession>();
builder.Services.AddSingleton<ITrackApi, TrackApi>();
// Signal K Radar API v3.1. Optional; empty list when no provider plugin.
builder.Services.AddSingleton<IRadarApi, RadarApi>();
// The host's module ref is wired by the page once the JS module loads.
builder.Services.AddSingleton<JsRadarOverlayHost>();
builder.Services.AddSingleton<IRadarOverlayHost>(
    sp => sp.GetRequiredService<JsRadarOverlayHost>());
builder.Services.AddSingleton<RadarOverlayManager>();
// Optional: detects sbender9/signalk-buddylist-plugin at runtime.
builder.Services.AddSingleton<IBuddyListApi, BuddyListApi>();

// Wall-clock provider. Every service reads "now" through this (staleness,
// alarm snooze, AIS pruning, toast expiry, resource createdAt, ...) so
// tests can advance time deterministically (FakeTimeProvider from
// Microsoft.Extensions.TimeProvider.Testing) instead of sleeping.
builder.Services.AddSingleton(TimeProvider.System);

// SignalK WebSocket delta stream. NavigationData is the own-boat
// store the stream writes into; services that only observe the
// connection take ISignalkClient (same instance).
builder.Services.AddSingleton<NavigationData>();
builder.Services.AddSingleton<SignalkClient>();
builder.Services.AddSingleton<ISignalkClient>(sp => sp.GetRequiredService<SignalkClient>());

// Resolved once at startup so the sampler subscribes before any page renders.
builder.Services.AddSingleton<INavigationAverages, NavigationAverages>();

// Entry point for explicitly relaying a caught exception to the SignalK plugin's /log
// (the global handler is installed by wwwroot/js/errorRelayBoot.js).
builder.Services.AddSingleton<ClientErrorRelay>();

// Single source of truth for resources.{routes,waypoints,notes,regions}: WS deltas plus REST load.
builder.Services.AddSingleton<ResourceStore>();

// Place search: Merged(own places, Caching(Fallback(Photon, Nominatim))).
// Photon takes a position-provider thunk so each search call can
// add &lat=&lon= to bias results by proximity to the helm's current
// fix. The thunk reads NavigationData live each call - if the SK
// feed hasn't yielded a position yet, returns null and Photon skips
// the bias params (global ranking).
builder.Services.AddSingleton<PhotonPlaceSearchService>(sp =>
    new PhotonPlaceSearchService(
        sp.GetRequiredService<HttpClient>(),
        sp.GetRequiredService<ILogger<PhotonPlaceSearchService>>(),
        () =>
        {
            var nav = sp.GetRequiredService<NavigationData>();
            return nav.Latitude is double lat && nav.Longitude is double lon
                ? (lat, lon)
                : null;
        }));
builder.Services.AddSingleton<NominatimPlaceSearchService>();
builder.Services.AddSingleton<PlaceSearchCache>();
builder.Services.AddSingleton<OwnPlacesIndex>();
builder.Services.AddSingleton<IPlaceSearchService>(sp =>
{
    var fallback = new FallbackPlaceSearchService(
        primary:   sp.GetRequiredService<PhotonPlaceSearchService>(),
        secondary: sp.GetRequiredService<NominatimPlaceSearchService>());
    var caching = new CachingPlaceSearchService(
        fallback,
        sp.GetRequiredService<PlaceSearchCache>());
    return new MergedPlaceSearchService(
        sp.GetRequiredService<OwnPlacesIndex>(),
        caching);
});

// MarinePoiController is built by Map.razor once the JS module ref lands.
builder.Services.AddSingleton<OverpassPoiService>();
builder.Services.AddSingleton<IMarinePoiService>(
    sp => sp.GetRequiredService<OverpassPoiService>());
builder.Services.AddSingleton<MarinePoiCache>();

// Startup work runs as hosted services; see StartupServiceCollectionExtensions.
builder.Services.AddOnaStartup();

await builder.Build().RunAsync();
