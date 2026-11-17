# AGENTS.md

Pickup notes for contributors and agents. Architecture, decisions, tests,
SignalK setup.

## Scope

Safety-critical marine chartplotter. Blazor WASM, net11.0, no server-side
Blazor. Runs on iPad / phone / 21" helm touchscreen. Any exception reaching
Blazor's error UI is a user-visible failure on a boat with flaky wifi.
Benchmark UX against dedicated commercial chartplotter hardware.

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

JS (leafletInterop.js + layer modules): renderer ONLY. C# resolves
classification, colour, CPA, ETA, etc. and pushes shaped payloads.
JS-side decisions (e.g. "is this ship a SART?") are a code smell.
```

**SignalkClient** owns the WebSocket (connect, subscribe, reconnect,
staleness) and hands each frame to `Services/Signalk/SignalkDeltaDispatcher`,
which routes it through per-store handlers (`SelfDeltaHandler`,
`AisDeltaHandler`, `AtonDeltaHandler`, `ServerNotificationDeltaHandler`)
into the stores above. New self paths that aren't plain numbers / bools /
strings get an entry in `SelfDeltaHandler.ExactPaths`; don't subscribe
elsewhere. Services that only observe the connection take `ISignalkClient`.

**Subscription tiers.** Single `SubscriptionTier` table near the top of
`SignalkClient.cs`: `SelfFast`, `SelfFastNotifications`, `SelfSlow`, `Ais`,
`ServerNotifications`. Each entry: context glob, paths, period, policy
(`ideal` vs `instant`). Adding a path = add it to the matching tier or add a
new tier entry. Don't sprinkle ad-hoc `subscribe` calls; reconnect replays
from the table.

**Connect-time seeding.** Deltas only carry changes, so state settled
before the socket opened (vessel names, self URN, active course, draft)
comes from REST: `SignalkConnectSeeding` starts the four
`Signalk*Seeder`s per connect. They fetch through `ResourceHttp` and log
through category loggers from the `ILoggerFactory` `SignalkClient` takes.

**Services/Map/*** controllers wrap JS interop and own diff-and-push state
(`AisLayerController`, `AtonLayerController`, `ActiveRouteController`,
`ServerAnchorController`, `ServerTrackController`, `ChartLayerController`,
...). Pages call into them; controllers decide when JS actually needs an
update. Stops pages from spamming interop on every render. Pure rules
sit beside them: `AisVesselPresentation` (name fallbacks, colour roles,
label visibility) and `VesselListBuilder` (Layers-panel list, CPA from
the dead-reckoned target exactly like `CpaAlarmRule` and the chart).

**AIS delta protocol.** `AisLayerController` pushes `AisUpdatePayload`
(`seq`, `baseSeq`, `vessels`, `removed`). The first push, and any push
after a failure, is a full snapshot (`baseSeq = -1`); later pushes carry
only changed vessels, changed identity fields, trail points appended /
trimmed and removed contexts. `aisLayer.js` refuses a delta whose
`baseSeq` isn't the `seq` it holds (`acceptsUpdate`), and C# answers
with a full snapshot on the next tick.

**Services/Api/*Api.cs** is one HTTP client per SignalK resource sharing
`ResourceHttp`. Wire-shape contracts are pinned by tests
(`AnchorAlarmApiTests`, `WaypointCourseApiTests`).

**Services/Alarms/*AlarmRule.cs** is rule-per-file, DI-registered,
`IAlarmRule.Check(ctx)` returns 0..N alarms. Manager sorts by
`(severity desc, TTI asc, priority asc)` and surfaces the head. Each
rule and alarm carries an `AlarmRuleId` (stable identity for persisted
disables, Settings toggles, dismiss keys; adding one is a
compiler-guided checklist in `AlarmRuleId.cs`). `AlarmEvaluationDriver`
runs the manager on every delta / server-notification change plus a
timer, so time-based rules (DEADMAN) fire while the socket is down.
`AutoAdvanceService` moves the route on the false -> true edge of the
course provider's `perpendicularPassed` / `arrivalCircleEntered`.
Dependencies are required, not optional; tests pass fakes
(`NoopNotificationsApi`, `NoopPublishedAlarmTracker`).

**Time.** Read the clock through the injected `TimeProvider`, or take
the instant as a parameter (`Format.Eta`); tests use
`FakeTimeProvider`. No `DateTime.UtcNow` / `DateTime.Now` in services.

**Settings keys.** Every persisted key is declared in `SettingKeys` as
`<name>.vN`. Bump N on a breaking value-format change so a stale value
reads as missing. A renamed key keeps its old spelling as a `Legacy*`
constant plus a `Renamed` row; `KeyValueStoreMigration` moves values on
boot (the new key wins; a storage failure retries next boot).

**JS modules.** `leafletInterop.js` is the chart page's mux; layer
modules (`aisLayer.js`, `anchorLayer.js`, ...) own their state.
`mapRotation.js` does course-up / head-up: `#mapDiv` sits in an
unrotated, clipping `.map-viewport`; while rotation is on it grows past
the viewport to the half-diagonal (`overscanFor`) so the corners stay
covered, and Leaflet's control corners and the range-scale chip live in
the viewport so they stay upright. One transitioned `@property
--map-rot` drives the container rotation and every counter-rotation
(labels, tooltips, popups), so they turn together. Pointer input is
un-rotated in `mouseEventToContainerPoint` / `L.Draggable`. For
anything on screen use `viewportSize()`, `toLayoutPoint()` and
`fitPadding()`, not `map.getSize()` or raw container points.
`historyMapLayer.js` renders the History page's separate map.

**Release bundle.** `dotnet publish -c Release` (needs `npm ci` for
esbuild) minifies JS / CSS, bundles `leafletInterop.js` and its imports
into one hashed module (`scripts/bundle-interop.mjs`), stamps
`CACHE_NAME` with the git hash, deletes the SDK's now-stale `.br` /
`.gz` siblings and regenerates them (`scripts/precompress.mjs`) for
`deploy/index.js` to serve. Debug serves the unbundled modules.

**CSS tokens.** Stacking uses the `--z-*` tiers in `app.css :root`
(map-layer, hud, quick-bar, controls, popup, topbar, alarm, toast,
modal); safe-area insets come from `--safe-top` / `--safe-bottom` and
the derived `--app-height` / `--content-height`. No hardcoded z-index
or `env(safe-area-inset-*)` in new rules.

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
- **Calls and dispose on an existing module reference go through the
  quiet helpers.** `SafeJsModule` (composed by every `Services/Js`
  wrapper) and the `InvokeVoidQuietAsync` / `DisposeQuietlyAsync`
  extensions swallow `JSDisconnectedException` /
  `ObjectDisposedException`, so no hand-written catch for those. A site
  that imports the module and then calls it keeps its own
  `JSDisconnectedException` catch around the import.
- **Zero compiler warnings.** `Directory.Build.props` sets
  `TreatWarningsAsErrors` for every project (NuGet audit warnings
  included), so a warning fails the build. Treat the analyzer as
  authoritative.
- **No history-flavoured comments.** Present-tense rationale only.
  No "was X / now Y", no "from PR #N", no "tech-debt pass dropped Z".
  Git blame carries history; comments carry the *why* of the
  current shape. The version-history block in
  `service-worker.js` is the one intentional exception (it IS the
  SW changelog).

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
`TestClock.FixedUtcNow` is the shared deterministic time seed
(`2025-01-01T12:00:00Z`); use it instead of `DateTime.UtcNow` when
a test needs a reference instant so the suite stays *Repeatable*
across DST flips and leap seconds.

### 2. Component tests (bUnit)

`OnaPlotter.Tests/Components/*Tests.cs`. Renders Razor components in
isolation (bUnit 2: `BunitContext`, `Render<T>`), exercises DOM +
parameter contracts.

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

`OnaPlotter/wwwroot/js/*.test.js` (plus `js/__tests__/*.test.mjs`,
`deploy/*.test.js`, `scripts/__tests__/`). Pure-JS modules (geoMath,
geomPixels, markerLayer, popupHelpers, overzoomLayer, chartDownshift,
aisLayer, mapRotation, ...). node:test runner. `npm run lint` covers
`OnaPlotter/wwwroot/js`, `deploy` and `scripts`.

```bash
node --test "OnaPlotter/wwwroot/js/*.test.js" "deploy/*.test.js"
npm run lint
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
(Map.razor is a ~5000-line view plus ~2000 lines of partial
code-behind); target controllers and stateful sub-components instead.

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
  CRUD + routing; the `Map.RouteEditing` / `Map.PolygonEditing` /
  `Map.Editing` / `Map.FirstRun` partials split out the rest.
- `Components/Map/*` - HUD, controls, per-resource `*Section.razor` rows in
  LayersPanel.
- `Services/Api/*Api.cs` - one HTTP client per SignalK resource.
- `Services/Alarms/*AlarmRule.cs` - rule-per-file, DI-registered.
- `Services/Map/*` - diff-and-push `*Controller`s around JS interop.
- `Services/Signalk/*` - delta dispatcher, per-store handlers, REST seeders.
- `Utilities/*.cs` - pure math (Cpa, Colregs, Format, AisPalette,
  GeoMath, RouteProgress).
- `Models/*.cs` - value records (TrackPoint, TrackBbox, ...).
- `wwwroot/js/leafletInterop.js` + sibling modules - renderer;
  `mapRotation.js` course-up; `historyMapLayer.js` History map.
- `scripts/` - Release build helpers (bundle, CACHE_NAME stamp, precompress).

## Branching + commits

`master` is the deploy branch and the only long-lived branch. Commit
directly to `master`. Topic branches under `.claude/worktrees/` are
fine for in-flight work; push them straight to `master` via
`git push origin <branch>:master` (fast-forward only) when ready -
don't open a long-lived integration branch in between. When the local
`master` worktree is locked (worktree dir under `.claude/worktrees/`),
direct push is the only way; don't checkout master from the main
worktree.

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
is mandatory for SignalK resource-API interop, even when near-empty.
Notes are the exception: bare `position: { latitude, longitude }`.

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

**Chart overlays vs base charts.** `SignalkChart.IsOverlay` (built-in
`openseamap`, an id containing `seamark`, or a `/seamark/` tile path)
puts a chart in `chartOverlayPane` (z 260), above the weather radar
pane (z 250); base charts stay in `tilePane` (z 200). An overlay plugin
that matches none of these draws under the rain: extend `IsOverlay`
(and `SignalkChartOverlayTests`). Overzoom is per chart:
`ChartLayerController` resolves the upscale levels (helm setting x the
chart's `AllowUpscale`) and `overzoomLayer.js` (`withOverzoom`) applies
them; the built-in OSM / OpenSeaMap opt out.

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
