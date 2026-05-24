// Kestrel host for the OnaPlotter Blazor WebAssembly bundle. Two
// shapes consume this entry point:
//
//   1. Docker: the multi-stage Dockerfile publishes this project
//      as a NativeAOT-compiled, self-contained native binary that
//      runs on top of mcr.microsoft.com/dotnet/runtime-deps:alpine.
//      Container image lands at ~35 to 50 MB total.
//
//   2. Local dev: `dotnet run --project OnaPlotter.Server` is a
//      fast iteration path that exercises the Docker-shape
//      hosting (env-driven appsettings, base-href templating,
//      SPA fallback) without spinning up a container. NativeAOT is
//      gated on Release publish, so dev runs use the regular JIT.
//
// Standalone WASM dev (`dotnet run --project OnaPlotter`) still
// works unchanged; that path runs the Blazor app's own dev server,
// which is faster for tight UI iteration. This Server is for
// end-to-end testing of the deployable shape.
//
// Runtime configuration via env vars:
//
//   SK_SERVER_URL  - written into wwwroot/appsettings.json as
//                    SignalK:ServerUrl. Default "auto" = the WASM
//                    client uses the page origin (Settings >
//                    Standalone mode can still override at runtime).
//
//   BASE_HREF      - written into wwwroot/index.html's <base href>.
//                    Default "/". Set to "/onaplotter/" if hosting
//                    behind a reverse proxy at that subpath.
//
//   ASPNETCORE_URLS - standard ASP.NET Core; Dockerfile sets it to
//                     http://+:8080 to bind all interfaces on 8080.

// CreateBuilder, not CreateSlimBuilder: the slim variant skips the
// configuration sources that wire in the static-web-asset dev
// manifest, which the SPA fallback to index.html depends on under
// `dotnet run`. NativeAOT in net10 is compatible with CreateBuilder;
// CreateSlimBuilder is a startup-time / binary-size optimisation,
// not a hard requirement.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Template the static bundle from env vars before the first request.
// Idempotent: re-running with the same env produces byte-identical
// files, so a container restart is safe and a hot-reload of env vars
// is one restart away. Writing on every cold start avoids the
// surprise of stale files when the deployer flips an env var.
ApplyRuntimeConfig(app.Environment.WebRootPath, app.Logger);

if (!app.Environment.IsDevelopment())
{
    // Brotli-precompressed .wasm / .js are shipped by the Blazor
    // publish; the framework files middleware below picks them up
    // automatically when the client advertises Accept-Encoding: br.
    // Nothing additional needed here.
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// SPA fallback: any GET that doesn't match a static file gets
// index.html so the Blazor client-side router can take over. The
// browser then loads /map, /settings etc. via in-app routing rather
// than a server-side 404 on reload.
app.MapFallbackToFile("index.html");

app.Run();

static void ApplyRuntimeConfig(string? webRootPath, ILogger logger)
{
    var skServerUrl = Environment.GetEnvironmentVariable("SK_SERVER_URL");
    var baseHref    = Environment.GetEnvironmentVariable("BASE_HREF");
    if (string.IsNullOrWhiteSpace(skServerUrl) && string.IsNullOrWhiteSpace(baseHref))
    {
        // Neither env var set; nothing to template. Skip silently so
        // the `dotnet run` path stays log-quiet for defaults.
        return;
    }

    if (string.IsNullOrEmpty(webRootPath) || !Directory.Exists(webRootPath))
    {
        // Reached in `dotnet run` if either env var is set: dev mode
        // serves the Blazor files from the WASM project's wwwroot via
        // the static web asset manifest, not from the Server's own
        // wwwroot, so there's nothing physical to template here.
        // Surface a warning so the operator notices the env vars
        // aren't being applied; the deployed (publish) shape is
        // where templating actually runs.
        logger.LogWarning(
            "Env-driven config requested but wwwroot is not materialized at {Path}; " +
            "this is normal under `dotnet run`. The published Docker image will template " +
            "settings as expected.",
            webRootPath);
        return;
    }

    if (!string.IsNullOrWhiteSpace(skServerUrl))
    {
        var appSettingsPath = Path.Combine(webRootPath, "appsettings.json");
        // Hand-rolled JSON so we don't pull JsonSerializer into the
        // AOT graph for what is a two-field object. The shape is
        // stable: { "SignalK": { "ServerUrl": "..." } } - matches
        // what the WASM client reads in SignalKBaseUrl.cs.
        var escaped = skServerUrl.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var json = $"{{ \"SignalK\": {{ \"ServerUrl\": \"{escaped}\" }} }}\n";
        File.WriteAllText(appSettingsPath, json);
        logger.LogInformation(
            "Wrote SignalK:ServerUrl = {Url} to appsettings.json", skServerUrl);
    }

    if (!string.IsNullOrWhiteSpace(baseHref))
    {
        var indexPath = Path.Combine(webRootPath, "index.html");
        if (File.Exists(indexPath))
        {
            // Two normalisations on the env var:
            //   1. Force a leading slash so "onaplotter/" becomes
            //      "/onaplotter/" (no relative-base footgun).
            //   2. Force a trailing slash so the browser resolves
            //      sibling resources correctly. <base href="/x">
            //      resolves "_framework/foo.js" against "/" - we
            //      need "/x/" to land inside the subpath.
            var normalized = baseHref;
            if (!normalized.StartsWith('/')) normalized = "/" + normalized;
            if (!normalized.EndsWith('/')) normalized += "/";

            var html = File.ReadAllText(indexPath);
            // Substitute the href value inside the existing
            // <base href="..."> tag. Blazor's publish emits this in
            // a consistent shape, so the literal-match here is
            // robust enough and keeps the AOT graph free of the
            // regex engine. Find the tag opening, walk to the next
            // double-quote, slice in the new value.
            const string startTag = "<base href=\"";
            var startIdx = html.IndexOf(startTag, StringComparison.Ordinal);
            if (startIdx < 0)
            {
                logger.LogWarning(
                    "BASE_HREF set but no <base href=\"...\"> tag found in index.html");
            }
            else
            {
                var valueStart = startIdx + startTag.Length;
                var valueEnd = html.IndexOf('"', valueStart);
                if (valueEnd < 0)
                {
                    logger.LogWarning(
                        "index.html has a malformed <base href> tag; skipping patch");
                }
                else
                {
                    var patched = string.Concat(
                        html.AsSpan(0, valueStart),
                        normalized,
                        html.AsSpan(valueEnd));
                    File.WriteAllText(indexPath, patched);
                    logger.LogInformation(
                        "Patched <base href> in index.html to {BaseHref}", normalized);
                }
            }
        }
    }
}
