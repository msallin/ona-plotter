# AGENTS.md

Pickup notes for contributors and agents. Architecture, decisions, tests,
SignalK setup.

## Scope

Safety-critical marine chartplotter. Blazor WASM, net10.0, no server-side
Blazor. Runs on iPad / phone / 21" helm touchscreen. Any exception reaching
Blazor's error UI is a user-visible failure on a boat with flaky wifi.
Benchmark UX against B&G Vulcan / Raymarine Axiom, not Freeboard-SK.

## Architecture

```
SignalK server  --WS deltas-->  SignalkClient
                                   |  fans out
                                   v
                NavigationData    AisStore    TrackBuffer    ServerNotificationStore
                    |                |             |              |
                    +----------------+-------------+--------------+
                    v
            Pages + Components observe these stores; Services/Map/* are
            controllers that diff store state and push to JS interop.

JS (leafletInterop.js + helpers): renderer ONLY. C# resolves
classification, colour, CPA, ETA, etc. and pushes shaped payloads.
JS-side decisions (e.g. "is this ship a SART?") are a code smell.
```

**SignalkClient** owns the WebSocket and routes deltas to the four stores
above. New paths wire into one of those stores; don't subscribe elsewhere.

**Subscription tiers.** Single `SubscriptionTier` table near the top of
`SignalkClient.cs`: `SelfFast`, `SelfFastNotifications`, `SelfSlow`, `Ais`,
`ServerNotifications`. Each entry: context glob, paths, period, policy
(`ideal` vs `instant`). Adding a path = add it to the matching tier or add a
new tier entry. Don't sprinkle ad-hoc `subscribe` calls; reconnect replays
from the table.

**Services/Map/*** controllers wrap JS interop and own diff-and-push state
(`ServerAnchorSync`, `ServerTrackController`, ...). Pages call into them;
controllers decide when JS actually needs an update. Stops pages from
spamming interop on every render.

**Services/Api/*Api.cs** is one HTTP client per SignalK resource sharing
`ResourceHttp`. Wire-shape contracts are pinned by tests
(`AnchorAlarmApiTests`, `WaypointCourseApiTests`).

**Services/Alarms/*AlarmRule.cs** is rule-per-file, DI-registered,
`IAlarmRule.Check(ctx)` returns 0..N alarms. Manager sorts by
`(severity desc, TTI asc, priority asc)` and surfaces the head.

## Design decisions

- **C# over JS.** JS is a thin renderer. Decisions, math, classification
  live in C# with C# tests. JS gets a typed payload and renders it.
- **Plugin-required, not manual fallback.** When a feature depends on a
  SignalK plugin (e.g. signalk-anchoralarm-plugin v2.0.0+), surface a clear
  "plugin vX.Y+ required" toast plus a Paths-page callout and stop. No
  shadow client-side implementation that drifts from server reality.
- **`sealed` by default.** No inheritance hierarchy.
- **Alarm severity is a token, not a colour.** Reference
  `--sev-ok / --sev-warn / --sev-danger` in `app.css :root`. Never hardcode
  red or green.
- **Persistence through `IKeyValueStore`.** No raw `localStorage.*` in JS.
  Keys end in `.v1` so schemas can migrate.
- **`MarkerLayer` JS class for any new resource type.** No hand-rolled
  `{}` + `delete` dicts.
- **`async void` only on Blazor event handlers**, body wrapped in
  `try/catch (ObjectDisposedException)`. Page switches tear down the
  SynchronizationContext mid-render.
- **Every `module.InvokeVoidAsync` catches `JSDisconnectedException`.**
- **Zero compiler warnings.** Treat the analyzer as authoritative.

## Testing

Six test surfaces, each does one job. Run the appropriate one for the
change you made; CI runs all of them.

### 1. Unit tests (C#)

`OnaPlotter.Tests/*.cs` (root). Pure logic, services, math, store
classes. TUnit runner.

```bash
dotnet test OnaPlotter.Tests/OnaPlotter.Tests.csproj
```

One file per unit. Equivalence classes + boundaries + a realistic case.
`FakeSettings` is the shared `IAppSettings` stub.

### 2. Component tests (bUnit)

`OnaPlotter.Tests/Components/*Tests.cs`. Renders Razor components in
isolation, exercises DOM + parameter contracts.

```bash
dotnet test OnaPlotter.Tests/OnaPlotter.Tests.csproj   # same runner
```

For any component whose UX you'd describe in helm-words ("tap chip
previews ring; tap Set commits"), pin the wire contract here. See
`AnchorEditPanelTests`, `HudAnchorCardTests`.

### 3. Fuzz tests (C#)

`OnaPlotter.Tests/*FuzzTests.cs`. Property-based: feed a seeded PRNG of
inputs through a pure-math invariant. Used for Cpa, Colregs,
RouteProgress, AnchorRadiusHeuristic, NavigationData, AisVessel,
AnchorTideAlarmRule.

```bash
dotnet test OnaPlotter.Tests/OnaPlotter.Tests.csproj   # same runner
```

Add when the function under test has algebraic invariants
(commutativity, monotonicity, bounded output range) that a hand-picked
case set wouldn't catch. Seed the PRNG so failures reproduce.

### 4. API contract tests

`OnaPlotter.Tests/*ApiTests.cs`. `ApiTestHelpers.MockClient` captures
URL + method + JSON body for every call. Pin shape so a refactor that
renames an arg or drops a field breaks the test, not the helm trying to
drop anchor at sundown.

### 5. JS unit tests

`OnaPlotter/wwwroot/js/*.test.js`. Pure-JS modules (geoMath, geomPixels,
markerLayer, popupHelpers, overzoomLayer, chartDownshift, aisLayer).
node:test runner.

```bash
node --test 'OnaPlotter/wwwroot/js/*.test.js'
```

JS hosts only renderers + low-level geometry helpers; everything else
is C#. So this surface stays small.

### 6. Smoke + fuzz UI (Playwright)

`OnaPlotter.UiTests/`. End-to-end against a live deployment.

```bash
cd OnaPlotter.UiTests
npm install && npm run install:deps     # one-time
BASE_URL=http://localhost:5282 npm run test:smoke   # walks every route
FUZZ_SEED=12345 npm run test:fuzz                   # 40 random clicks
```

- `smoke.spec.js` walks every top-level route, exercises all map
  control buttons, opens/closes layers panel. Fails if
  `#blazor-error-ui` ever becomes visible or the page raises errors.
- `fuzz.spec.js` is a seeded-PRNG walk over safe interactive selectors
  (`.ctrl-btn`, `.chart-quick-chip`, `.vessel-row`, ...). Avoids
  destructive actions (route edit, MOB).
- When fuzz finds a crash, reproduce with the logged `FUZZ_SEED`, fix
  the C#/JS root cause, then add the specific sequence as a named
  smoke test (so it becomes a regression case rather than a
  fuzz-might-find-it-again case).

Coverage report: `dotnet run --project OnaPlotter.Tests - --coverage`
produces a `.coverage` file; merge with `dotnet-coverage merge ... -f
cobertura` and run `reportgenerator`. Don't chase the overall line rate
(Map.razor is a 2000-line view); target stateful sub-components with
bUnit instead.

## SignalK setup for manual testing

Two flavours: a **dev SK with fake data** for fast iteration, and a
**staged-bundle deploy** for verifying what the helm will actually see.

### Dev SK with fake data

```bash
scripts/dev-sk.sh up           # signalk-server + fake-data pump
scripts/dev-sk.sh down
scripts/dev-sk.sh logs
```

Position circling 1 km, 5 AIS targets, wind + depth. Seed
deterministic (`FAKE_SEED=42`) so scripted scenarios reproduce. Server
on `http://localhost:3000`, admin UI at `/admin/` (creds `admin /
admin` from `docker/signalk-config/security.json`).

Point OnaPlotter at it: edit `OnaPlotter/wwwroot/appsettings.json` ->
`"SignalK": { "ServerUrl": "http://localhost:3000" }`, then
`dotnet run --project OnaPlotter`. App on
`http://localhost:5282/`.

### Plugins (anchor + tides)

The fake-data SK doesn't ship plugins. To exercise the anchor /
tide-alarm flows you need them installed:

```bash
scripts/dev-sk-plugins.sh      # installs anchoralarm + tides-api
```

Idempotent. Lands the plugins in the named volume so they survive a
`down`/`up`.

### Staged-bundle deploy (test instance)

For verifying the deployed shape (subpath base href, service worker,
PWA manifest) the helm will actually load:

```bash
rm -rf OnaPlotter/obj/Release OnaPlotter/bin/Release deploy/publish
dotnet publish OnaPlotter/OnaPlotter.csproj -c Release -o deploy/publish
pwsh ./deploy/stage-only.ps1
docker compose -f docker/docker-compose.dev.yml \
               -f docker/docker-compose.testing.yml restart sk
```

Served at `http://localhost:3000/signalk-onaplotter/`.

`stage-only.ps1` does NOT run `dotnet publish` itself; it stages
whatever's in `deploy/publish/`. Forgetting the publish step ships
yesterday's WASM with today's HTML.

## Layout map

- `Components/Pages/*.razor` + code-behind. `Map.razor.cs` is the resource
  CRUD + routing.
- `Components/Map/*` - HUD, controls, per-resource `*Section.razor` rows in
  LayersPanel.
- `Services/Api/*Api.cs` - one HTTP client per SignalK resource.
- `Services/Alarms/*AlarmRule.cs` - rule-per-file, DI-registered.
- `Services/Map/*` - diff-and-push controllers around JS interop.
- `Utilities/*.cs` - pure math (Cpa, Colregs, Format, AisPalette,
  GeoBearing, RouteProgress).
- `Models/*.cs` - value records (TrackPoint, TrackBbox, ...).
- `wwwroot/js/leafletInterop.js` + sibling modules - renderer.

## Branching + commits

`master` is the deploy branch. `v1-ship` is active development, typically
matching `master` modulo the in-flight commit. Pattern: commit on
`v1-ship`, push, then ff master via `git push origin v1-ship:master`.
When the local `master` worktree is locked (worktree dir under
`.claude/worktrees/`), direct push is the only way; don't checkout master
from the main worktree.

Squash before push. Semantic prefix in subject: `feat(scope):`,
`fix(scope):`, `refactor(scope):`, `chore(scope):`. Helm is the user;
quote them in the body when the change traces back to a verbatim request.
Never mention AI.

## Footguns

**Service-worker cache stickiness.** `/_framework/*.wasm` and
`blazor.boot.json` go through cache-first. Component-shape change without
bumping `CACHE_NAME` in `OnaPlotter/wwwroot/service-worker.js` =
render-time crash for any returning helm
(`Object of type ... does not have a property matching the name 'X'`).
The version-history block in service-worker.js is the canonical
changelog; add a one-line entry per bump.

**Resource payload shape.**
`{ name, feature: { type, geometry, properties: {...} } }`. `properties`
is mandatory for Freeboard-SK interop, even when near-empty. Notes are
the exception: bare `position: { latitude, longitude }`.

**Razor string-parameter binding.** On a `string`-typed parameter,
`Param="var"` is a literal, not an expression. Typed parameters
(`bool`, `int`, collections) don't have this trap.

```razor
<RouteEditPanel Name="@routeEditName" />   @* correct *@
<RouteEditPanel Name="routeEditName" />    @* passes the 14-char literal *@
```

**Alarm stack order.** `(severity desc, TTI asc, priority asc)`. SHALLOW
at TTI=0 beats pending CPA regardless of rule priority. Set
`AlarmInfo.TimeToEventMinutes` when the rule knows when the event fires.

**Long-press gesture.** Fires after 700 ms of stillness within an 8 px
envelope, cancelled by Leaflet's `movestart`/`dragstart`/`zoomstart`.
Don't retune without testing on a rocking boat with cold fingers.

**Three independent first-run signals** (welcome card, touch coachmark,
offline toast) each gate on their own KV key. Don't collapse them.

**Country flag in AIS popup** hits
`/signalk/v2/api/resources/flags/mmsi/{mmsi}` (the `signalk-flags`
plugin). Relative URL resolves against the page origin, which is the SK
server when deployed as a webapp. Pointing at a remote server via
`SignalK:ServerUrl` 404s the flag and hides via `onerror`. Don't make
anything safety-critical depend on it.

**Chart overzoom vs overlays.** `recomputeChartOverzoom()` in
`leafletInterop.js` picks the chart with the highest native maxZoom and
stretches it past its limit. Overlays (OpenSeaMap seamarks, anything
whose id is `openseamap` / contains `seamark` or whose tile URL has
`/seamark/`) are excluded from the "top native" race; otherwise an
overlay's native 18 beats a base chart's native 17 and the base chart
disappears past its limit. New overlay plugins: match the heuristic or
extend `isOverlayChart`.

**Anchor flow is plugin-driven (v2.0.0+).** Drop = `POST
/plugins/anchoralarm/dropAnchor` (empty body, plugin reads GPS).
SetMaxRadius = `PUT navigation.anchor.maxRadius {value: N}`. Raise =
`PUT navigation.anchor.position {value: null}`. Panel uses pick-then-Set
with on-map preview: chip taps fire `OnPreviewRadius` (visual only via
`MapAnchorJs.UpdateAnchorRadiusAsync`); Set fires `OnSetRadius` (the PUT).
`_anchorPreviewActive` tracks "preview pushed but not committed" so
cancel paths can revert the on-map ring to server MaxRadius.

**Cruise/Race mode is a preset, not a lockout.** Mode flips defaults and
surfaces mode-specific overlays; every individual toggle still works in
either mode.

## After a non-trivial change

Skip rounds with nothing to say.

1. **Review** as if presenting to senior engineers who know the domain:
   correctness, maintainability, performance, UX. Fix what you find;
   flag larger refactors without doing them.
2. **Tests** at the right level (unit / component / fuzz / contract /
   smoke). 100% of the unit under change.
3. **Sailor-persona check** for larger changes: cruiser + race crew.
4. **If component shape changed**, bump `CACHE_NAME` in
   `service-worker.js` with a one-line entry in the version block.

## Roadmap / known gaps

Tracked as GitHub issues, not here. If something is safety-adjacent or
mid-migration, note it as a footgun above instead.
