# Research: Can we enable `TrimMode=full` on the WASM bundle?

Helm asked at the end of the v1 cleanup batch: "investigate TrimMode=full; now possible? Smth left we can do?"

**Short answer**: Not yet -- but the gap is well-defined and the migration is mechanical. Estimated ~half a day of focused work, gated on a manual Release publish + smoke test on the helm.

## What `TrimMode=full` would buy

`TrimMode=partial` (the Blazor WASM default) preserves all assemblies marked `IsTrimmable=false` and keeps reflection-callable members of types that aren't reachable from a `Main`. `TrimMode=full` aggressively removes everything not statically reachable.

Empirically on similar-shape Blazor WASM projects, the brotli-compressed size drop is **~100-200 KB**. On marine 4G that's ~2-4 seconds shaved off cold start. Bundle today is ~3 MB brotli per `getChartTileErrors`-style audits in the dev section; the relative win is ~3-7 %.

Stacking with what's already enabled (`InvariantGlobalization=true`, `WasmStripILAfterAOT=true`, `OptimizationPreference=Speed`) it's the last meaningful first-load win without restructuring app code.

## What blocks it today

Three reflection-flavoured surfaces would generate trim warnings or runtime "type was not preserved" failures the moment `TrimMode=full` ships:

### 1. JsonSerializer.Deserialize / Serialize (10 call sites)

```
OnaPlotter/Components/Pages/Map.razor.cs:234,465  -- Serialize(feature, ...)
OnaPlotter/Models/RadarDtos.cs:501                -- SerializeToElement(...)
OnaPlotter/Services/AlarmManager.cs:233,269       -- snooze persist round-trip
OnaPlotter/Services/Api/AuthApi.cs:74             -- LoginStatus deser
OnaPlotter/Services/RouteDraftStore.cs:27,70      -- RouteDraft round-trip
OnaPlotter/Services/SignalkClient.cs:704          -- SignalkDelta deser  *** hot path
```

Each call uses the reflection-based serializer. Migration: declare a `JsonSerializerContext`-derived class with `[JsonSerializable(typeof(SignalkDelta))]` etc. for every type, then replace each call site:

```csharp
// Before
JsonSerializer.Deserialize<SignalkDelta>(json);
// After
JsonSerializer.Deserialize(json, OnaJsonContext.Default.SignalkDelta);
```

The `SignalkDelta` deserialize is the hot path (every WS frame). Source-gen would also remove a ~50 KB chunk of `System.Text.Json.Reflection` from the bundle as a side benefit.

**Estimated effort**: 2-3 hours. Mechanical once the context is set up.

### 2. AlarmManager Activator-based test ctor

`AlarmManager.cs:197` has a `// Keeps the args: [rules, now] Activator.CreateInstance pattern` comment. The legacy test reflection path needs a `[DynamicDependency]` hint or the production trim will preserve the wrong members. Verify it's actually still used; if it's pure test scaffolding, mark the test-only entry with `[DynamicallyAccessedMembers(...)]`.

**Estimated effort**: 1 hour audit + tag.

### 3. AlarmRuleMetadataTests reflection (test-only -- not a blocker)

`AlarmRuleMetadataTests.DiscoverRuleTypes` reflects over the production assembly to find every `IAlarmRule` impl. This is test-only and doesn't ship to WASM, so not a blocker. Mention it because it shows up on a naive grep.

## Step-by-step migration plan

1. **Source-gen JsonContext**: add `OnaPlotter/Services/Json/OnaJsonContext.cs` with `[JsonSerializable(typeof(T))]` for each DTO. Verify build is clean (the analyzer surfaces missing types).
2. **Migrate hot path first**: switch `SignalkClient.Deserialize<SignalkDelta>(json)` to use the context. Run the suite + manually test against a live SK feed. If green, the contract is validated.
3. **Migrate the rest**: each of the 10 call sites in turn.
4. **Audit Activator usage** in `AlarmManager`. Add `[DynamicDependency]` if the pattern actually runs in production.
5. **Flip `<TrimMode>full</TrimMode>`** in csproj.
6. **Publish + smoke test**: `dotnet publish -c Release` and load the bundle on the helm. Watch DevTools console for any "type/member was not preserved" runtime errors. If the helm hits a screen with a missing type, add `[DynamicDependency]` to the broken site and re-publish.
7. **Measure**: compare brotli-compressed bundle size pre/post via `du -sb wwwroot/_framework/*.br` (or `dotnet publish` output).

## Other bundle-size opportunities (in priority order)

The v1 backlog also mentioned:
- **Lazy-load `System.Private.Xml` via Blazor LazyAssemblies** (-500 KB raw, -100 KB brotli; loaded only when GPX import/export runs). The XML usage is concentrated in `OnaPlotter/Services/GpxService.cs` -- the call site is gated behind a Razor handler that only runs on the Resources page. Wrap in `LazyAssembly` + an awaiting load before the first GpxService call. Lower risk than TrimMode=full and orthogonal to it.
- **`<UseInterpreter>` for cold paths**: AOT all the hot stuff, interpreter-only the cold paths to claw back AOT bloat. The current `<RunAOTCompilation>true</RunAOTCompilation>` AOTs everything; selective AOT is more nuanced and may not be worth it once full-trim lands.

## Verdict

`TrimMode=full` is realistic but blocked on a focused migration of the JsonSerializer call sites. Worth it for the cold-start win on marine networks. The csproj has been left at `TrimMode=partial` with a comment cross-referencing this doc so the next contributor finds the context.

The lazy-XML win is independent and probably easier to land first; recommend doing it as a standalone batch before the JsonContext sweep.
