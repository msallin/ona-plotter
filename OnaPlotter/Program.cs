using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OnaPlotter.Components;
using OnaPlotter.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<TrackBuffer>();
builder.Services.AddSingleton<AisStore>();
builder.Services.AddSingleton<ChartService>();
builder.Services.AddSingleton<SignalkClient>();

var host = builder.Build();

// Start the SignalK websocket connection (no IHostedService in WASM).
var signalkClient = host.Services.GetRequiredService<SignalkClient>();
_ = signalkClient.StartAsync();

await host.RunAsync();
