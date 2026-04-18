using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OnaPlotter.Components;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Shared HttpClient used by every *Api.
builder.Services.AddSingleton(new HttpClient());

// Storage + settings.
builder.Services.AddSingleton<IKeyValueStore, LocalStorageKeyValueStore>();
builder.Services.AddSingleton<IAppSettings, AppSettingsService>();

// Domain state.
builder.Services.AddSingleton<TrackBuffer>();
builder.Services.AddSingleton<AisStore>();

// UI services.
builder.Services.AddSingleton<IToastService, ToastService>();
builder.Services.AddSingleton<IPolarService, PolarService>();
// Alarm rules are DI-registered; AlarmManager picks them up via
// IEnumerable<IAlarmRule>. Adding a new rule is a one-line registration.
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.ShallowAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.CpaAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.WindShiftAlarmRule>();
builder.Services.AddSingleton<IAlarmManager, AlarmManager>();

// SignalK REST API clients (one per concern).
builder.Services.AddSingleton<ISignalKBaseUrl, SignalKBaseUrl>();
builder.Services.AddSingleton<IChartApi, ChartApi>();
builder.Services.AddSingleton<IRouteApi, RouteApi>();
builder.Services.AddSingleton<IWaypointApi, WaypointApi>();
builder.Services.AddSingleton<INoteApi, NoteApi>();
builder.Services.AddSingleton<IRegionApi, RegionApi>();
builder.Services.AddSingleton<ICourseApi, CourseApi>();
builder.Services.AddSingleton<IAutopilotApi, AutopilotApi>();
builder.Services.AddSingleton<IPathApi, PathApi>();
builder.Services.AddSingleton<ITrackApi, TrackApi>();
// Optional: detects sbender9/signalk-buddylist-plugin at runtime.
builder.Services.AddSingleton<IBuddyListApi, BuddyListApi>();
builder.Services.AddSingleton<IWeatherForecastApi, WeatherForecastApi>();

// SignalK WebSocket delta stream.
builder.Services.AddSingleton<SignalkClient>();

var host = builder.Build();

// Kick off the WebSocket loop (no IHostedService in Blazor WASM).
var signalkClient = host.Services.GetRequiredService<SignalkClient>();
_ = signalkClient.StartAsync();

await host.RunAsync();
