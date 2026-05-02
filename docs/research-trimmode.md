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

All known reflection-based JSON serialise sites in the application code are now migrated to source-gen. Recent migrations:

```
OnaPlotter/Services/SignalkClient.cs                -- SignalkSubscribeRequest / SignalkUnsubscribeRequest
OnaPlotter/Utilities/ResourceExporter.cs            -- 5 GeoJSON Feature shapes (Route / Waypoint / Note / Trip / Region)
OnaPlotter/Components/Pages/Map.razor.cs            -- 2 share-feature blobs (Waypoint / Note)
OnaPlotter/Components/Pages/Resources.razor         -- 2 share-feature blobs (Waypoint / Note)
OnaPlotter/Components/Pages/History.razor           -- 6 JS-interop literal embeds (string, double[], double[][], List<SegmentPayload>)
OnaPlotter/Models/RadarDtos.cs                      -- 1 primitive SerializeToElement
```

GeoJSON exports use a dedicated `OnaGeoJsonContext` (source-gen with `WriteIndented = true`); the wire-protocol shapes stay in `OnaJsonContext` (compact). Per-record `[JsonIgnore(Condition = WhenWritingNull)]` reproduces the previous `DefaultIgnoreCondition` behaviour where it was used (share blobs only).

**Status**: the application-side surface is source-gen-clean. Trim-warning audit on a `TrimMode=full` publish should now produce zero IL2xxx warnings on `OnaPlotter.dll`.

### 2. AlarmManager Activator-based test ctor (resolved)

The original audit flagged a comment in `AlarmManager.cs` referencing an "Activator-based ctor pattern" that supposedly needed a `[DynamicDependency]` hint. Verified during the F2 sweep: **no production code calls `Activator.CreateInstance` against `AlarmManager`, and every test fixture uses direct `new AlarmManager(...)`**. The comment was historical dead text; cleaned up alongside this doc.

### 3. AlarmRuleMetadataTests reflection (test-only -- not a blocker)

`AlarmRuleMetadataTests.DiscoverRuleTypes` reflects over the production assembly to find every `IAlarmRule` impl. This is test-only and doesn't ship to WASM, so not a blocker. Mention it because it shows up on a naive grep.

## Step-by-step migration plan

1. ~~**Source-gen JsonContext**: add `OnaPlotter/Services/Json/OnaJsonContext.cs` with `[JsonSerializable(typeof(T))]` for each DTO.~~ ✅ Landed in F2/3.
2. ~~**Migrate hot path first**: switch `SignalkClient.Deserialize<SignalkDelta>(json)` to use the context.~~ ✅ Landed in F2/3.
3. ~~**Migrate the anon-type Serialize sites**.~~ ✅ Landed in PR #208. Records live in `OnaPlotter/Models/GeoJsonDtos.cs` + `OnaPlotter/Models/SignalkSubscriptionDtos.cs`; the helm-facing GeoJSON shapes use `OnaGeoJsonContext` (indented), the wire-protocol shapes use `OnaJsonContext` (compact).
4. ~~**Audit Activator usage** in `AlarmManager`.~~ ✅ Verified: no production callers, no test reflection. Stale comment removed.
5. **Flip `<TrimMode>full</TrimMode>`** in csproj. **Probed and reverted** -- see "Probe results" below. Application code is trim-clean; remaining work is the lazy-XML interaction.
6. **Publish + smoke test**: `dotnet publish -c Release` and load the bundle on the helm. Watch DevTools console for any "type/member was not preserved" runtime errors. If the helm hits a screen with a missing type, add `[DynamicDependency]` to the broken site and re-publish.
7. **Measure**: compare brotli-compressed bundle size pre/post via `du -sb wwwroot/_framework/*.br` (or `dotnet publish` output).

## Probe results (2026-05-02)

Ran a `dotnet publish -c Release` with `<TrimMode>full</TrimMode>` flipped on top of the F2/3 source-gen + lazy-XML setup. Findings:

- **Zero IL trim warnings.** The application code itself is trim-clean under full mode -- the source-gen JsonContext + careful typing did the job. No IL2xxx warnings, no errors from our assemblies.
- **`BLAZORSDK1001` failure on lazy-load + full-trim.** Without rooting, the trimmer drops `System.Private.Xml.wasm` and `System.Private.Xml.Linq.wasm` entirely (it doesn't follow `<BlazorWebAssemblyLazyLoad>` references when computing the static call graph), then the SDK can't find the files to lazy-package and aborts with `Unable to find ... to be lazy loaded later`.
- **`<TrimmerRootAssembly>` workaround bloats the lazy assemblies.** Adding `<TrimmerRootAssembly Include="System.Private.Xml" />` (and Linq) lets the publish complete, but the rooted assemblies preserve everything (no member-level dead-code elimination) -- they balloon from ~97 KB brotli (partial-trim + lazy) to ~541 KB brotli combined (505 KB Xml + 36 KB Xml.Linq). Net: the lazy XML download is **larger** under full trim, which inverts the lazy-load win.
- **The right next step is `<TrimmerRootDescriptor>`**, an XML descriptor file that pins specific types and members rather than the whole assembly. This requires auditing the public surface ResourceImporter / ResourceExporter actually touch (XDocument.Parse, XElement, XAttribute, XNamespace, XmlException, plus their reachable graph). Estimated 3-4 hours plus a manual smoke test on the helm to catch any missed members at runtime.

**Verdict**: TrimMode=full **delivers** on the application-code side (the source-gen migration was the prerequisite, and it landed clean). The remaining blocker is the trim/lazy-load interaction for the XML stack, which needs a `<TrimmerRootDescriptor>` to keep the lazy assemblies small. Until that work lands, partial-trim + lazy-XML stays the smaller boot bundle. The csproj keeps `TrimMode=partial` with a comment pointing here.

## Other bundle-size opportunities (in priority order)

The v1 backlog also mentioned:
- ~~**Lazy-load `System.Private.Xml` via Blazor LazyAssemblies**~~ ✅ Landed in F2/2. ~97 KB brotli excluded from the eager boot bundle; the GPX flows in Resources / History pull the assemblies via `XmlAssemblyLoader.EnsureLoadedAsync()` before the first call.
- **`<UseInterpreter>` for cold paths**: AOT all the hot stuff, interpreter-only the cold paths to claw back AOT bloat. The current `<RunAOTCompilation>true</RunAOTCompilation>` AOTs everything; selective AOT is more nuanced and may not be worth it once full-trim lands.

## Verdict

`TrimMode=full` is realistic on the application-code side after F2/3 (source-gen JsonContext landed), but the lazy-XML interaction needs `<TrimmerRootDescriptor>` work before the flip is a net-positive on bundle size. The csproj stays at `TrimMode=partial` with a comment cross-referencing this doc so the next contributor finds the context.

The anonymous-type `Serialize(new {...})` sweep (step 3) is independent and the next concrete step toward closing this out.
