# AGENTS.md

Pickup notes for contributors and agents. Keep it short; add only what bit you.

## Commands

```bash
dotnet build                                          # builds OnaPlotter + tests
dotnet test OnaPlotter.Tests/OnaPlotter.Tests.csproj  # TUnit + bUnit (~2200 tests)
node --test 'OnaPlotter/wwwroot/js/*.test.js'         # JS unit tests
cd OnaPlotter.UiTests && BASE_URL=http://localhost:5282/ npm test   # Playwright
```

`dotnet test` works (TUnit ships Microsoft.Testing.Platform support; older notes
saying it doesn't are stale).

Local dev: `dotnet workload install wasm-tools`, then `dotnet run --project OnaPlotter`.
App on `http://localhost:5282/`. Point at a server via `wwwroot/appsettings.json`;
`ServerUrl: "auto"` uses the page origin when deployed as a SignalK webapp.

**Dev SignalK server without a boat.** `scripts/dev-sk.sh up` spins up
signalk-server + a fake-data pump (position circling 1 km, 5 AIS targets, wind +
depth). Config in `docker/`. Seed is deterministic (`FAKE_SEED=42`). Stop with
`scripts/dev-sk.sh down`.

**Test instance for live deploy verification.**
`docker compose -f docker/docker-compose.dev.yml -f docker/docker-compose.testing.yml up -d`
mounts `deploy/staging/` into the container, served at
`http://localhost:3000/signalk-onaplotter/`.

## Deploy flow (test instance)

```bash
rm -rf OnaPlotter/obj/Release OnaPlotter/bin/Release deploy/publish
dotnet publish OnaPlotter/OnaPlotter.csproj -c Release -o deploy/publish
pwsh ./deploy/stage-only.ps1
docker compose -f docker/docker-compose.dev.yml -f docker/docker-compose.testing.yml restart sk
```

`stage-only.ps1` does NOT run `dotnet publish` itself; it just copies whatever's
in `deploy/publish/` into `deploy/staging/signalk-onaplotter/public/`. Forgetting
the publish step ships yesterday's WASM with today's HTML; bug reports follow.

**Bump `CACHE_NAME` in `OnaPlotter/wwwroot/service-worker.js` on every component-shape
change.** `/_framework/*.wasm` and `blazor.boot.json` go through cache-first, so a
helm with the previous SW will keep loading the old WASM after deploy. Symptom:
`Object of type ... does not have a property matching the name 'X'` at render
time. The version-history block in service-worker.js is the canonical changelog;
add a one-line entry per bump.

## Branching

`master` is the deploy branch. `v1-ship` is the active-development branch and
typically matches `master` modulo the in-flight commit. Pattern: commit on
`v1-ship`, push, then ff master via `git push origin v1-ship:master`. When the
local `master` worktree is locked (worktree dir under `.claude/worktrees/`),
direct push is the only way to update master; don't checkout master from the
main worktree.

Squash before push. Semantic prefix in commit subject: `feat(scope):`,
`fix(scope):`, `refactor(scope):`, `chore(scope):`. Helm is the user; quote them
in the body when the change traces back to a verbatim request. Never mention AI.

## Scope

Safety-critical marine chartplotter. Blazor WASM, net10.0, no server-side Blazor.
Runs on iPad / phone / 21" helm touchscreen. Any exception reaching Blazor's
error UI is a user-visible failure on a boat with flaky wifi. Benchmark UX
against B&G Vulcan / Raymarine Axiom, not Freeboard-SK.

## Layout map

- `Components/Pages/*.razor` + code-behind (`Map.razor.cs`: resource CRUD,
  routing, NearestWP)
- `Components/Map/*` -- HUD, controls, per-resource `*Section.razor` rows in
  LayersPanel. Anchor flow lives in `AnchorEditPanel.razor` (the panel) and
  `Hud/HudAnchorCard.razor` (the active-watch card)
- `Services/Api/*Api.cs` -- one HTTP client per SignalK resource; share
  `ResourceHttp`
- `Services/Alarms/*AlarmRule.cs` -- rule-per-file, DI-registered,
  `IAlarmRule.Check(ctx)`
- `Services/Map/*` -- per-feature controllers wrapping JS interop
  (`ServerAnchorSync`, `ServerTrackController`, ...). Diff-and-push state lives
  here, not in pages
- `Utilities/*.cs` -- pure math (Cpa, Colregs, Format, AisPalette, GeoBearing)
- `wwwroot/js/leafletInterop.js` -- 2000+ lines. **JS is a renderer**;
  classification (ship-type colour, SART, CPA) is resolved in C# and pushed as
  payload fields. JS-side decisions are a smell.

`SignalkClient` owns the WebSocket and fans deltas out to `NavigationData` (own
vessel), `TrackBuffer` (30-min rolling track), `AisStore` (others). New delta
paths wire into one of those three; don't subscribe elsewhere.

**Subscription tiers.** Single `SubscriptionTier` table near the top of
`SignalkClient.cs`: `SelfFast`, `SelfFastNotifications`, `SelfSlow`, `Ais`,
`ServerNotifications`. Each entry carries context glob, paths, period, and
policy (`ideal` vs `instant`). Adding a path = add it to the matching tier or
add a new tier entry. Don't sprinkle ad-hoc `subscribe` calls; reconnect replays
from the table.

## Rules

- `sealed` by default. No inheritance hierarchy.
- `async void` only on Blazor event handlers, body wrapped in
  `try/catch (ObjectDisposedException)`. Page switches tear down the
  SynchronizationContext mid-render.
- Every `module.InvokeVoidAsync` catches `JSDisconnectedException`.
- No raw `localStorage.*` in JS. Persistence goes through `IKeyValueStore`
  (C# side), with `.v1`-style suffixed keys so schemas can migrate.
- Use the `MarkerLayer` JS class for any new resource type. Do not hand-roll
  another `{}` + `delete` dict.
- Alarm colours: reference `--sev-ok / --sev-warn / --sev-danger` tokens from
  `app.css :root`. Never hardcode red or green.
- Every SignalK resource round-trip needs a test pinning the POST payload shape
  (Freeboard-SK compatibility). See `WaypointCourseApiTests`.
- Manual JS-only fallbacks for plugin features are off the table -- the
  alternative is a clear "plugin vX.Y+ required" toast plus a Paths-page
  callout. The anchor flow is the canonical example (plugin v2.0.0+,
  signalk-anchoralarm-plugin).
- Zero compiler warnings. Treat the analyzer as authoritative.

## Footguns

**Resource payload shape.** `{ name, feature: { type, geometry, properties: {...} } }`.
`properties` is mandatory for Freeboard interop -- waypoints / routes / regions
need it even if it's near-empty. Notes are the exception:
bare `position: { latitude, longitude }`.

**Razor string-parameter binding.** On a `string`-typed parameter, `Param="var"`
is a literal, not an expression. Typed parameters (`bool`, `int`, collections)
don't have this trap.

```razor
<RouteEditPanel Name="@routeEditName" />   @* correct *@
<RouteEditPanel Name="routeEditName" />    @* passes the 14-char literal *@
```

**Service-worker cache stickiness.** See "Deploy flow" above. Component-shape
change without `CACHE_NAME` bump = render-time crash for any returning helm.

**Alarm stack order.** `(severity desc, TTI asc, priority asc)`. SHALLOW at
TTI=0 beats pending CPA regardless of rule priority. Set
`AlarmInfo.TimeToEventMinutes` when the rule knows when the event fires.

**Long-press gesture.** Fires after 700 ms of stillness within an 8 px envelope,
cancelled by Leaflet's `movestart`/`dragstart`/`zoomstart`. Don't retune without
testing on a rocking boat with cold fingers.

**Three independent first-run signals** (welcome card, touch coachmark, offline
toast) each gate on their own KV key. Don't collapse them.

**Country flag in AIS popup** hits `/signalk/v2/api/resources/flags/mmsi/{mmsi}`
(the `signalk-flags` plugin). Relative URL resolves against the page origin,
which is the SignalK server when deployed as a webapp. Pointing at a remote
server via `SignalK:ServerUrl` 404s the flag and hides via `onerror`. Don't
make anything safety-critical depend on it.

**Chart overzoom vs overlays.** `recomputeChartOverzoom()` in `leafletInterop.js`
picks the chart with the highest native maxZoom and stretches it past its limit.
Overlays (OpenSeaMap seamarks, anything whose id is `openseamap` / contains
`seamark` or whose tile URL has `/seamark/`) are excluded from the "top native"
race; otherwise an overlay's native 18 beats a base chart's native 17 and the
base chart disappears past its limit. New overlay plugins: match the heuristic
or extend `isOverlayChart`.

**Anchor flow is plugin-driven.** Drop `POST /plugins/anchoralarm/dropAnchor`
(empty body, plugin reads GPS); `SetMaxRadius PUT navigation.anchor.maxRadius`;
`Raise PUT navigation.anchor.position` with `{value: null}`. The panel uses
pick-then-Set with on-map preview: chip taps fire `OnPreviewRadius` (visual
only via `MapAnchorJs.UpdateAnchorRadiusAsync`); the Set button fires
`OnSetRadius` which is the actual PUT. `_anchorPreviewActive` tracks
"preview pushed but not committed" so cancel paths can revert the on-map ring
to the server's MaxRadius.

**Cruise/Race mode is a preset, not a lockout.** Mode flips defaults and surfaces
mode-specific overlays; every individual toggle still works in either mode.

## Testing

- C# unit + bUnit in `OnaPlotter.Tests`. `FakeSettings` is the shared
  `IAppSettings` stub -- reuse it. `ApiTestHelpers.MockClient` for HTTP
  contract tests.
- One test file per alarm rule, branch coverage for each early-return path.
- API contract tests pin URL + method + JSON body shape (see
  `AnchorAlarmApiTests`). A future rename or arg-shape drift breaks the test,
  not the helm trying to drop anchor at sundown.
- Playwright in `OnaPlotter.UiTests` -- UI-only, no SignalK server needed.
  Fuzz knobs (`FUZZ_SEED`, `FUZZ_CLICKS`) documented in its own README.
- Coverage: `dotnet run --project OnaPlotter.Tests -- --coverage` produces a
  TRX-adjacent `.coverage` file; merge with
  `dotnet-coverage merge ... -f cobertura` and run `reportgenerator`. Utilities,
  alarm rules, API clients, and `AisStore` sit 85-100%. Razor pages drag the
  overall line rate to ~28% because Map.razor is 2000 lines of view code; don't
  chase that number directly, add bUnit tests for any stateful sub-component
  instead.
- Stryker is BLOCKED on .NET 10: stryker-net hasn't shipped
  Microsoft.Testing.Platform support yet (issue #3094). Until it does, mutation
  testing is manual.

## After a non-trivial change

Skip rounds that have nothing to say. Pedantic but pragmatic.

1. **Review** as if presenting to senior engineers who know the domain:
   functional correctness, maintainability, performance, UX. Fix what you find;
   flag larger refactors without doing them.
2. **Tests**: 100% of the unit under change (equivalence classes + boundaries +
   realistic values). Integration for CRUD / reconnect / alarm lifecycle.
   Playwright for important, cheap flows only.
3. **Sailor-persona check** for larger changes: cruiser + race crew. Gaps that
   only show under real use go to GitHub issues, not this file.
4. **If component shape changed**, bump `CACHE_NAME` in `service-worker.js`
   with a one-line entry in the version block.

## Roadmap / known gaps

Tracked as GitHub issues, not here (they rot otherwise). If something is
safety-adjacent or mid-migration, note it as a footgun above instead.
