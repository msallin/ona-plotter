# Research: Can we enable `TrimMode=full` on the WASM bundle?

Helm asked at the end of the v1 cleanup batch: "investigate TrimMode=full; now possible? Smth left we can do?"

**Short answer**: Not yet -- but the gap is well-defined and the migration is mechanical. Estimated ~half a day of focused work, gated on a manual Release publish + smoke test on the helm.

## What `TrimMode=full` would buy

`TrimMode=partial` (the Blazor WASM default) preserves all assemblies marked `IsTrimmable=false` and keeps reflection-callable members of types that aren't reachable from a `Main`. `TrimMode=full` aggressively removes everything not statically reachable.

Empirically on similar-shape Blazor WASM projects, the brotli-compressed size drop is **~100-200 KB**. On marine 4G that's ~2-4 seconds shaved off cold start. Bundle today is ~3 MB brotli per `getChartTileErrors`-style audits in the dev section; the relative win is ~3-7 %.

Stacking with what's already enabled (`InvariantGlobalization=true`, `WasmStripILAfterAOT=true`, `OptimizationPreference=Speed`) it's the last meaningful first-load win without restructuring app code.

## What blocks it today

Three reflection-flavoured surfaces would generate trim warnings or runtime "type was not preserved" failures the moment `TrimMode=full` ships:

### 1. JsonSerializer.Deserialize / Serialize call sites

**Status (post F2/3)**: the four named-type deserialise sites are migrated to source-gen via `OnaPlotter/Services/Json/OnaJsonContext.cs`. The remaining sites are anonymous-type serialisations only -- they need a separate refactor to named records before the trim flip.

Migrated (source-gen now):

```
OnaPlotter/Services/SignalkClient.cs:766          -- SignalkDelta deser  *** hot path
OnaPlotter/Services/AlarmManager.cs:233,269       -- SnoozedTarget[] round-trip
OnaPlotter/Services/Api/AuthApi.cs:74             -- LoginStatus deser
OnaPlotter/Services/RouteDraftStore.cs:27,70      -- RouteDraft round-trip
```

Still reflection (anonymous-type serialise; not a blocker for partial-trim, blocks the full-trim flip):

```
OnaPlotter/Components/Pages/Map.razor.cs:234,465  -- share-feature Serialize(new {...})
OnaPlotter/Components/Pages/Resources.razor:682,773 -- share-feature Serialize(new {...})
OnaPlotter/Components/Pages/History.razor:1134,1145,1147,1167,1375,1477 -- JS-interop literal embedding
OnaPlotter/Models/RadarDtos.cs:501                -- SerializeToElement(...) of an anon shape
OnaPlotter/Services/SignalkClient.cs:1436,1453    -- WS subscribe / unsubscribe envelope
OnaPlotter/Utilities/ResourceExporter.cs:108,139,175,259,294 -- per-feature GPX-twin GeoJSON
```

To finish the trim-blocker work for these, replace each `new { ... }` with a named `record` (e.g. `SubscribeRequest(string Context, SubscribePath[] Paths)`) and add `[JsonSerializable(typeof(SubscribeRequest))]` to `OnaJsonContext`. Mechanical, but spread across many files; left out of F2 to keep that batch focused.

**Estimated effort for the anonymous-type sweep**: 2-3 hours.

### 2. AlarmManager Activator-based test ctor

`AlarmManager.cs:197` has a `// Keeps the args: [rules, now] Activator.CreateInstance pattern` comment. The legacy test reflection path needs a `[DynamicDependency]` hint or the production trim will preserve the wrong members. Verify it's actually still used; if it's pure test scaffolding, mark the test-only entry with `[DynamicallyAccessedMembers(...)]`.

**Estimated effort**: 1 hour audit + tag.

### 3. AlarmRuleMetadataTests reflection (test-only -- not a blocker)

`AlarmRuleMetadataTests.DiscoverRuleTypes` reflects over the production assembly to find every `IAlarmRule` impl. This is test-only and doesn't ship to WASM, so not a blocker. Mention it because it shows up on a naive grep.

## Step-by-step migration plan

1. ~~**Source-gen JsonContext**: add `OnaPlotter/Services/Json/OnaJsonContext.cs` with `[JsonSerializable(typeof(T))]` for each DTO.~~ ✅ Landed in F2/3 for the four named-type sites.
2. ~~**Migrate hot path first**: switch `SignalkClient.Deserialize<SignalkDelta>(json)` to use the context.~~ ✅ Landed in F2/3.
3. **Migrate the anon-type Serialize sites**: requires a small refactor (anon record -> named `record`) at each call site. See list above. Pending.
4. **Audit Activator usage** in `AlarmManager`. Add `[DynamicDependency]` if the pattern actually runs in production.
5. **Flip `<TrimMode>full</TrimMode>`** in csproj.
6. **Publish + smoke test**: `dotnet publish -c Release` and load the bundle on the helm. Watch DevTools console for any "type/member was not preserved" runtime errors. If the helm hits a screen with a missing type, add `[DynamicDependency]` to the broken site and re-publish.
7. **Measure**: compare brotli-compressed bundle size pre/post via `du -sb wwwroot/_framework/*.br` (or `dotnet publish` output).

## Other bundle-size opportunities (in priority order)

The v1 backlog also mentioned:
- ~~**Lazy-load `System.Private.Xml` via Blazor LazyAssemblies**~~ ✅ Landed in F2/2. ~95 KB brotli excluded from the eager boot bundle; the GPX flows in Resources / History pull the assemblies via `XmlAssemblyLoader.EnsureLoadedAsync()` before the first call.
- **`<UseInterpreter>` for cold paths**: AOT all the hot stuff, interpreter-only the cold paths to claw back AOT bloat. The current `<RunAOTCompilation>true</RunAOTCompilation>` AOTs everything; selective AOT is more nuanced and may not be worth it once full-trim lands.

## Verdict

`TrimMode=full` is realistic but blocked on a focused migration of the JsonSerializer call sites. Worth it for the cold-start win on marine networks. The csproj has been left at `TrimMode=partial` with a comment cross-referencing this doc so the next contributor finds the context.

The lazy-XML win is independent and probably easier to land first; recommend doing it as a standalone batch before the JsonContext sweep.
