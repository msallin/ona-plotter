# agents.md

Pickup notes for contributors. Keep it short; add only what bit you.

## Commands

```bash
dotnet build                                          # root builds everything
dotnet run --project OnaPlotter.Tests --no-build      # C# + bUnit (~390 tests)
node --test OnaPlotter/wwwroot/js/geoMath.test.js     # JS math tests
cd OnaPlotter.UiTests && BASE_URL=http://localhost:5282/ npm test   # Playwright
```

`dotnet test` does **not** work. .NET 10 SDK dropped VSTest dispatch; TUnit runs as
an executable via `dotnet run`. CI (`.github/workflows/ci.yml`) does it this way too.

Local dev: `dotnet workload install wasm-tools`, then `dotnet run --project OnaPlotter`.
App starts on `http://localhost:5282/`. Point at a server via `wwwroot/appsettings.json`;
`ServerUrl: "auto"` uses the page origin when deployed as a SignalK webapp.

## Scope

Safety-critical marine chartplotter. Blazor WASM, net10.0, no server-side Blazor.
Runs on iPad / phone / 21" helm touchscreen. Any exception reaching Blazor's error
UI is a user-visible failure on a boat with flaky wifi. Benchmark UX against B&G
Vulcan / Raymarine Axiom, not Freeboard-SK.

## Layout map

- `Components/Pages/*.razor` + `Map.razor.cs` (code-behind: resource CRUD, routing, NearestWP)
- `Components/Map/*` -- HUD, controls, per-resource `*Section.razor` rows in LayersPanel
- `Services/Api/*Api.cs` -- one HTTP client per SignalK resource; share `ResourceHttp`
- `Services/Alarms/*AlarmRule.cs` -- rule-per-file, DI-registered, `IAlarmRule.Check(ctx)`
- `Utilities/*.cs` -- pure math (Cpa, IsochroneRouter, Colregs, Format, AisPalette)
- `wwwroot/js/leafletInterop.js` -- 2000+ lines. **JS is a renderer**; classification
  (ship-type colour, SART, CPA) is resolved in C# and pushed as payload fields.

`SignalkClient` owns the WebSocket and fans deltas out to `NavigationData` (own
vessel), `TrackBuffer` (30-min rolling track), `AisStore` (others). New delta paths
wire into one of those three; don't subscribe elsewhere.

## Rules

- `sealed` by default. No inheritance hierarchy.
- `async void` only on Blazor event handlers, body wrapped in
  `try/catch (ObjectDisposedException)`. Page switches tear down the
  SynchronizationContext mid-render.
- Every `module.InvokeVoidAsync` catches `JSDisconnectedException`.
- No raw `localStorage.*` in JS. Persistence goes through `IKeyValueStore` (C# side),
  with `.v1`-style suffixed keys so schemas can migrate.
- Use the `MarkerLayer` JS class for any new resource type. Do not hand-roll another
  `{}` + `delete` dict.
- Alarm colours: reference `--sev-ok / --sev-warn / --sev-danger` tokens from
  `app.css :root`. Never hardcode red or green.
- Every SignalK resource round-trip needs a test pinning the POST payload shape
  (Freeboard-SK compatibility). See `WaypointCourseApiTests`.

## Footguns

**Resource payload shape.** `{ name, feature: { type, geometry, properties: {...} } }`.
`properties` is mandatory for Freeboard interop -- waypoints / routes / regions need
it even if it's near-empty. Notes are the exception: bare
`position: { latitude, longitude }`.

**Razor string-parameter binding.** On a `string`-typed parameter, `Param="var"` is
a literal, not an expression. Typed parameters (`bool`, `int`, collections) don't
have this trap. This bit us twice:

```razor
<RouteEditPanel Name="@routeEditName" />   @* correct *@
<RouteEditPanel Name="routeEditName" />    @* passes the 14-char literal *@
```

**Alarm stack order.** `(severity desc, TTI asc, priority asc)`. SHALLOW at TTI=0
beats pending CPA regardless of rule priority. Set `AlarmInfo.TimeToEventMinutes`
when the rule knows when the event fires.

**Long-press gesture.** Fires after 700 ms of stillness within an 8 px envelope,
cancelled by Leaflet's `movestart`/`dragstart`/`zoomstart`. Don't retune without
testing on a rocking boat with cold fingers.

**Three independent first-run signals** (welcome card, touch coachmark, offline
toast) each gate on their own KV key. Don't collapse them.

**Cruise/Race mode is a preset, not a lockout.** Mode flips defaults and surfaces
mode-specific overlays; every individual toggle still works in either mode.

## Testing

- C# unit + bUnit in `OnaPlotter.Tests`. `FakeSettings` is the shared `IAppSettings`
  stub -- reuse it.
- One test file per alarm rule, branch coverage for each early-return path.
- Playwright in `OnaPlotter.UiTests` -- UI-only, no SignalK server needed. Fuzz
  knobs (`FUZZ_SEED`, `FUZZ_CLICKS`) documented in its own README.
- Coverage: `dotnet run --project OnaPlotter.Tests -- --coverage` produces a
  TRX-adjacent `.coverage` file; merge with `dotnet-coverage merge ... -f
  cobertura` and run `reportgenerator` for a readable summary. Utilities,
  alarm rules, API clients, and `AisStore` sit 85-100%. Razor pages drag
  the overall line rate to ~28% because Map.razor is 2000 lines of view
  code; don't chase that number directly, add bUnit tests for any stateful
  sub-component instead.
- Stryker is BLOCKED on .NET 10: stryker-net hasn't shipped
  Microsoft.Testing.Platform support yet (issue #3094). Until it does,
  mutation testing is manual -- walk the alarm rules by eye after any
  change, or temporarily flip the test project back to VSTest for a
  one-off Stryker run in a branch.

## After a non-trivial change

Skip rounds that have nothing to say. Pedantic but pragmatic.

1. **Review** as if presenting to senior engineers who know the domain: functional
   correctness, maintainability, performance, UX. Fix what you find; flag larger
   refactors without doing them.
2. **Tests**: 100% of the unit under change (equivalence classes + boundaries +
   realistic values). Integration for CRUD / reconnect / alarm lifecycle. Playwright
   for important, cheap flows only.
3. **Sailor-persona check** for larger changes: cruiser + race crew. Gaps that only
   show under real use go to GitHub issues, not this file.

## Roadmap / known gaps

Tracked as GitHub issues, not here (they rot otherwise). If something is
safety-adjacent or mid-migration, note it as a footgun above instead.
