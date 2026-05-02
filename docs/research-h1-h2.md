# Research findings: H1 (timer observable) and H2 (RadarLegend in C#)

Two deferred-research items from the backlog. Helm asked to research first, implement only if perf / maintainability / testability earns it. Both were investigated; both came back **NOT WORTH SHIPPING**. Documented here so a future contributor doesn't redo the analysis.

## H1 -- Anchor / measure / route-edit JS timers as a single C# observable

### Hypothesis

The backlog item proposed unifying the rolling-window timers in `anchorLayer.js`, `measureLayer.js`, and `routeEditLayer.js` behind a single C# observable. Premise: one observable would tighten lifecycle management vs. three independent `setInterval` calls.

### Findings

`grep -n "setInterval\|setTimeout\|requestAnimationFrame" anchorLayer.js measureLayer.js routeEditLayer.js` returns **zero hits**. The proposed JS timers don't exist; the periodic work that the backlog item was thinking about all lives in C# already:

- `Map.RouteEditing.razor.cs` -- `routeStatsTimer: System.Threading.Timer` polls `getEditRouteCoords` on a 500 ms cadence while the helm is in route-edit mode.
- `Map.razor` -- `aisTimer`, `resourcePollTimer` -- both `System.Threading.Timer`s with their own cadences (3 s for AIS push, 60 s for resource refresh).
- `Map.razor` -- `_loadCts: CancellationTokenSource` for the History fetch (manual cancel + 30 s `CancelAfter`, no timer).

Each C# timer has a different cadence and a single, narrow consumer. Unifying them behind one observable would:

- Lose per-cadence tuning -- the AIS push at 3 s and the resource refresh at 60 s would either share a tick rate (wasting 19/20 ticks for resources) or carry per-subscription cadence tracking inside the observable, which reinvents the per-timer pattern with extra indirection.
- Add a dependency-injection seam (`ITickerService`) for what is currently three `new System.Threading.Timer(_ => ..., null, X, Y)` lines. The seam is bigger than the thing it abstracts.
- Provide no testability win that the existing `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` doesn't already give us when a test wants deterministic time.

### Verdict

**Skip.** The proposed observable solves a problem that doesn't exist (no JS timers to consolidate) and would add indirection to the three independent C# timers that DO exist. The existing per-feature timer pattern is the right granularity.

If a future contributor wants to revisit this, the question to answer first is: "what concrete bug or developer-velocity issue does the unified observable fix?" If the answer is just "feels cleaner", that's not enough.

---

## H2 -- Extract a RadarLegend C# model with byte-table builder over interop

### Hypothesis

The backlog item proposed building the radar's 256-entry byte → RGBA palette in C# instead of JS, shipping a precomputed `Uint8ClampedArray` over interop. Premise: legend logic belongs alongside `RadarApi` parsing tests; JS gets a precomputed table.

### Findings

The `_setLegend` build path in `radarLayer.js:212` runs ONCE per legend update. Legend updates happen on:

1. Radar device added to the map (typically once at session start).
2. Provider plugin pushes a new legend (rare; legends are static for a given radar firmware).

The hot path is the per-spoke READ of `byteToRgba`, not the build. The build itself is 256 iterations × 4 byte writes; even in the worst case (a helm cycling radar layers in and out) it costs microseconds.

So the perf win is ~zero. What about testability?

The pure-logic helpers from the legend (parsing hex colours, deciding whether a byte index suppresses, the `mediumReturn` cutoff) ALREADY landed in `OnaPlotter/Utilities/RadarSpokeMath.cs` during batch D1, with `RadarSpokeMathTests` covering the parse + decision branches. The byte-table BUILD is just iteration over that decision logic; if the per-byte rule is right, the table is right by construction.

What would be added by porting the build itself to C#?

- A `RadarLegendBuilder.Build(LegendDto) -> byte[1024]` method whose only job is to call `RadarSpokeMath.ParseHexRgba` 256 times. Tests would assert the same thing the existing `RadarSpokeMath` tests assert, just at a different layer.
- An interop call to ship the 1024-byte array into JS once per legend change. Same lifecycle as today, just C#-built instead of JS-built.

### Verdict

**Skip.** The decision logic is already in C# + tested via `RadarSpokeMath`. Moving the build itself adds an interop hop + a one-off byte-array transfer for negligible perf and zero new test coverage (it would re-test what `RadarSpokeMath` already tests, at a different abstraction level).

If a future contributor wants to extract a `RadarLegend` model: the right reason would be "we need to surface legend metadata in C# UI" (e.g. a Settings page where the helm picks which intensity bands count as sea clutter). Build the model when there's a consumer; don't build it speculatively.
