// Service worker for OnaPlotter PWA.
// Caches the app shell for faster loads; network-first for API calls.

// v2 -> v3: chartOverzoom module extraction (shape change).
// v3 -> v4: chart-tile cache-as-you-view (new rule below).
// v4 -> v5: errorRelayBoot.js + themeApply.js + index.html shell update;
//           bundle precaches the new boot script so an iPad that
//           loaded an older version doesn't keep serving it offline.
//           Bump CACHE_NAME on EVERY shell-asset change going forward
//           (favicon, css, html, any APP_SHELL entry).
// v5 -> v6: harden tileCacheFirst against Cache API rejections so
//           Firefox no longer surfaces tile fetches as
//           "ServiceWorker intercepted ... unexpected error".
// v6 -> v7: PWA standalone scrollbar fix (sidebar 100dvh + body
//           overflow lock) + sidebar-collapse user toggle. CSS
//           changes only, but APP_SHELL precaches need a refresh.
// v7 -> v8: anchor-on-route-activate JS marker clear (race fix)
//           + in-place route edit forces active-route refetch.
//           Bundled C# changes won't affect SW behaviour but the
//           helm should see the fixed flow on the next reload, so
//           bump triggers an app shell refresh.
// v8 -> v9: sidebar-collapse moved into NavMenu foot; iOS fullbleed
//           map-container fills full viewport (no -3rem topbar gap)
//           so Safari's bottom toolbar no longer overlaps the HUD.
// v9 -> v10: anchor-raise visual feedback (dim marker until SK delta
//            confirms) replaces the optimistic clear that masked
//            errors and raced the next sync tick.
// v10 -> v11: wheel zoom + arrow-step + reverse + save-as-copy in
//             route edit; Stop Navigation now uses the same dim-and-
//             wait pattern as anchor raise instead of optimistically
//             clearing local state.
// v11 -> v12: route activation no longer auto-raises the anchor (only
//             the reverse direction stays); editing the active route
//             temporarily hides the active overlay to avoid double-
//             drawing, restores after Cancel/Save.
// v12 -> v13: tile cache cap 1000 -> 5000 + LRU promotion on hit
//             (delete+re-put moves entries to end of insertion order
//             so trim evicts least-recently-used). keepBuffer 4 -> 6
//             on tile layers for snappier route-planning pans.
// v13 -> v14: post-sail-readiness follow-ups - sidebar-collapse
//             driven by user setting only (no longer forced by
//             :fullscreen / .ios-fullbleed CSS so the chevron toggle
//             works in fullscreen); active-route hides the regular
//             polyline so the dashed-leg overlay isn't double-drawn;
//             route HUD shows passed/total nm; iPad follow centres
//             the boat above the geometric centre; CPA dismiss
//             cooldown 30s -> 15min; PNG PWA icons + apple-touch-icon
//             so iPad install shows the OnaPlotter logo; prev/next
//             waypoint buttons in route HUD; AIS popup autoPan off.
// v14 -> v15: real-browser :fullscreen now hides the topbar (matches
//             .ios-fullbleed) so the 100dvh map-container fits the
//             viewport without 3rem of bottom overflow on Firefox /
//             Chrome / Edge; Settings + other overflowing pages get
//             a main-as-scroll-container fallback under :fullscreen
//             so the helm can scroll Settings while in real
//             fullscreen.
// v15 -> v16: branded boot loading screen (favicon-derived SVG +
//             spinner + build stamp from js/version.g.js); phone
//             first-run default collapses the sidebar to the icon
//             rail; nav menu picks up a tighter @media trim under
//             600px so labels + icons fit without crowding.
// v16 -> v17: drop the body { overflow: hidden } chain we added under
//             :fullscreen for non-map pages. On Firefox the chain
//             ate Settings / History / Resources scroll instead of
//             routing it to <main>; reverting to the natural document
//             scroll restores it. Map page is still locked via the
//             body:has(.map-container) rule.
// v17 -> v18: stop hiding the topbar in :fullscreen / .ios-fullbleed
//             so zoom / night / liveness / exit-fullscreen affordances
//             stay visible while fullscreen. Map-container falls back
//             to its non-fullscreen calc(100dvh - 3rem) height. Also
//             explicit pointer-events: auto on the route prev/next
//             and autopilot buttons inside the non-corner HUDs (the
//             parent .hud / .hud-panel pointer-events: none was
//             swallowing taps on those new buttons).
// v18 -> v19: scoped main-as-scroll-container fix for Settings /
//             Dashboard / History / Resources in fullscreen. Just
//             constrain <main> itself (height 100dvh, overflow-y
//             auto); leaves html / body alone so the chain that
//             tripped Firefox in v17 isn't reintroduced.
// v19 -> v20: outer-try guard on tileCacheFirst: any unhandled
//             throw / sync-rejection from the cache-API path now
//             falls through to a 504 placeholder rather than
//             rejecting respondWith(). Helm reported recurring
//             "ServiceWorker intercepted ... unexpected error" on
//             Firefox; v6 hardened the documented failure modes,
//             v20 catches the residue (clone-throw on quirky
//             Response bodies, sync-throws from cache.put on low-
//             memory Firefox, ...). Also drops the LRU clone+put
//             promotion on cache hits - helm reported "no single
//             tile loads" which points at body-stream contention
//             between the served response and the background put.
//             We accept FIFO eviction and ship reliable tile
//             rendering; LRU was nice-to-have, tile delivery is
//             not negotiable.
// v20 -> v21: tile-cache audit follow-ups.
//             - /signalk/* + WS pass-through and app-shell cache
//               handlers now wrapped (passThroughOrPlaceholder,
//               appShellCacheFirst) so a fetch / Cache-API rejection
//               doesn't bubble into respondWith and trigger the
//               same Firefox SW-intercept error we squashed for
//               tiles in v20.
//             - tile-cache reference module-scoped (getTileCache);
//               eliminates per-request caches.open() spin-up.
//             - trimTileCache throttled (every 50 puts) + single-
//               flight + parallel-delete; was running a full
//               cache.keys() walk on every successful put.
//             - keepBuffer 6 -> 10 on the OSM / OpenSeaMap base
//               and per-chart layers (leafletInterop.js); larger
//               in-DOM tile retention so small route-planning pans
//               don't re-fetch.
// v21 -> v22: network-first for /js/*, /css/*, and the document root.
//             Stale-while-revalidate (the previous strategy) caches
//             a working JS module on first visit AND keeps serving
//             that exact byte-for-byte cached copy on every
//             subsequent visit until the cache is bumped manually.
//             Every deploy that adds a new C#-callable export to a
//             JS module (e.g. setGuardZoneWarningRingVisible in PR
//             #143) crashes the helm's NEXT page load with a
//             Blazor "value is not a function" because the new C#
//             code calls a function the cached old JS doesn't
//             have. The cache eventually refreshes in the
//             background and the helm's NEXT-NEXT load works - but
//             one crashed reload per deploy is one too many; e2e
//             CI surfaces it as a hard failure.
//             Network-first fetches fresh on every online load and
//             only falls back to cache when the network is gone.
//             The 304-revalidation path keeps the bytes-on-the-wire
//             cost low (Kestrel sends ETag, browser sends
//             If-None-Match, server replies 304 with no body when
//             unchanged). Offline launch still works because the
//             network failure surfaces the cached copy.
// v22 -> v23: radar range-ring labels + emphasised active-ring stroke.
//             css/app.css is in APP_SHELL so a stale precache would
//             ship the old (label-less) styles to a freshly-loaded
//             page even with the network-first JS rule overriding the
//             code path. Bump invalidates the shell precache so the
//             helm picks up the new label styling on next launch.
// v23 -> v24: route-total dashed divider scoped to big-screen mode,
//             closing border under the chart-filter Display section.
//             Same APP_SHELL invalidation reasoning as v23.
// v24 -> v25: chart-filter Reset button always visible (with
//             :disabled state styled when at defaults). CSS gained
//             a new :disabled rule, so the precache must refresh.
// v25 -> v26: topbar place-search SearchBox + search-pin overlay
//             (Phase 2 of the geocoder feature). New CSS rules
//             under .topbar-search* / .search-pin* land in the
//             APP_SHELL precache; bump invalidates the stale shell.
// v26 -> v27: Place-search Phase 3 - own-data separator + source
//             badge. Adds .topbar-search-separator + -badge CSS
//             under APP_SHELL.
// v27 -> v28: Place-search Phase 4 - Nominatim fallback + offline
//             empty-state hint. Adds .topbar-search-empty-title
//             / -hint CSS under APP_SHELL.
// v28 -> v29: Place-search dropdown switched to HUD-card tokens
//             (--hud-card-bg / -stroke-in / -stroke-out / -halo /
//             --hud-ink) so the dropdown matches HUD opacity in
//             night / dark themes instead of looking transparent.
// v29 -> v30: SearchBox.razor rewritten to use @bind + request-id
//             counter (was: value="@_query" + manual @oninput +
//             CTS chain). The previous controlled-input pattern
//             raced keystrokes against in-handler StateHasChanged
//             calls, dropping characters and (often) preventing
//             a search request from firing at all.
// v30 -> v31: SearchBox.razor rewritten AGAIN - fully uncontrolled
//             input (no value=, no @bind). The renderer never
//             writes the value attribute, so the helm's typing
//             can't be clobbered. Added searchBoxJs.js for the
//             programmatic clear / fill-on-pick paths.
// v31 -> v32: AnchorEditPanel + HudAnchorCard component shapes
//             changed (manual JS-only fallback removed; plugin
//             v2.0.0+ is the only supported source). The cached
//             v31 WASM still references the dropped CanRaise /
//             OnAutoSetRadius / Manual / ManualRadiusMeters
//             parameters, so a freshly-deployed shell loaded into
//             a stale-WASM browser throws InvalidOperationException
//             at render time. Bump invalidates the precache so the
//             helm pulls the new framework on next launch.
// v32 -> v33: SearchBox state-cleanup pass - explicit
//             StateHasChanged so the spinner shows during the
//             in-flight call; catch-all around SearchAsync so
//             any decorator regression degrades to empty list
//             instead of leaving the dropdown in a half-rendered
//             "stopped working" state.
// v33 -> v34: AnchorEditPanel SetRadius mode reverted to pick-then-
//             Set with on-map preview (was: live-commit, which
//             couldn't show "Auto-grows-as-boat-drifts" without
//             spamming PUTs). Panel gained OnPreviewRadius parameter
//             and an .anchor-edit-set button; lost the SetRadius
//             Close button. Cached v33 WASM passing the old shape
//             would render a panel without preview wiring.
// v34 -> v35: .top-row z-index 1 -> 1000 so the SearchBox typeahead
//             dropdown (which visually overflows from the topbar
//             into the map area) renders ABOVE the leaflet map
//             panes + HUD cards. The dropdown WAS rendering - the
//             helm could see the rows in the HTML inspector - but
//             the parent's stacking context capped its effective
//             z-index at 1, so the chart painted over it.
// v35 -> v36: History page no longer sends a viewport bbox to the
//             track API. Helm-visible: loading "yesterday" while
//             zoomed at home harbour now returns the full passage
//             instead of just the marina pixels. Framework files
//             go through cache-first, so without this bump a
//             helm with the v35 cache would keep loading the
//             old (bbox-sending) WASM after redeploy.
// v36 -> v37: Mobile layout pass.
//             - More menu trims Centre / Fit Track / Legend / Tips
//               on phone + scrolls if it still overflows.
//             - Depth HUD now expands on phone (rows were always
//               hidden by mobile CSS; now hidden only when collapsed).
//             - Topbar tightened (smaller icon buttons, narrower
//               SearchBox, smaller padding).
//             - Search input gets text-overflow: ellipsis on phone.
//             - Alarm-history button replaces text with a clipboard
//               icon + count badge (markup change: alarm-log-icon
//               span replaces alarm-log-label-short).
//             - Active-route HUD hides per-leg TTG / ETA + the
//               prev/next chevron buttons on phone (route-total row
//               carries the macro time).
//             CSS-only + small markup tweak; WASM is unchanged
//             except for the new History icon helper. Bump
//             invalidates the precache so helms pull the new CSS +
//             markup together.
// v37 -> v38: Route HUD spacing + ResourceHttp timeout handling.
//             - Bumped --panel-gap 9 -> 16 px so the bc-stack
//               (anchor + active route) has visible breathing room
//               above the bottom bar.
//             - On phone, lifted .hud-stack-bc by an extra 64 px so
//               it floats ABOVE the depth / heading corner cards
//               instead of sharing the bottom band (route card was
//               340 px wide on a 390-wide viewport, leaving no room
//               for the corners next to it).
//             - ResourceHttp.{Get,Post,Put,Delete,PostCreate} now
//               also catch OperationCanceledException when the
//               caller didn't cancel - that's the HttpClient
//               timeout path, which previously bubbled past the
//               helpers as TaskCanceledException and reached
//               Blazor's renderer error UI ("An unhandled error has
//               occurred"). Failures still surface as toasts; the
//               banner only fires for genuine bugs now.
// v38 -> v39: ResourceStore added: live route / waypoint / note /
//             region sync via SignalkClient.OnResourceDelta + REST
//             reconcile on reconnect. Adds new JS export
//             leafletInterop.updateRoute (in-place setLatLngs +
//             popup refresh + dot rebuild). Stale leafletInterop.js
//             without updateRoute throws "value is not a function"
//             on first remote-route delta; bump invalidates the
//             precache. Closes the multi-plotter route-edit
//             sync gap (plotter A edits, plotter B's polyline now
//             updates without reload).
// v39 -> v40: Mobile layout iteration 2.
//             - --panel-gap 16 -> 4 px and dropped the 64 px phone
//               lift on .hud-stack-bc. Helm: "too much padding".
//             - Expanded HUDs paint OVER the bottom menu + the
//               route/anchor cards via :has(.hud-panel-expanded)
//               -> z-index pop on the parent stack. Tap a corner
//               to expand; details overlay everything; tap again
//               to collapse. Same behaviour on every breakpoint;
//               desktop wasn't seeing the issue today but the
//               policy is consistent.
//             - Chart-quick-bar is no longer hidden on phone -
//               smaller chips (font 0.62rem, padding 2px 8px) so
//               the helm can flip charts without diving into the
//               full Layers panel.
//             - Topbar on phone: overflow-x:auto + scroll-bar-
//               hidden so a still-wide row (auth chip + update
//               chip + search + zoom + night) scrolls horizontally
//               instead of clipping off the right edge on FF
//               Android. Also shrunk topbar-icon-btn 1.75 -> 1.5
//               rem and the SearchBox cap 180 -> 160 px.
// v40 -> v41: .hud-value gets `white-space: nowrap` so the unit
//             suffix span never wraps onto its own line. Helm
//             reported a depth of "38.2 m" rendering as "38.2"
//             on one line, "m" on the next on a narrow BL card;
//             the inline-box boundary between the digit text
//             node and the .hud-unit <span> was a wrap
//             opportunity. The TR wind card already had its own
//             nowrap rule; lifting it onto the base class covers
//             depth, route metrics, and any other corner value
//             with the same shape.
// v41 -> v42: New Layers > Marine services section +
//             marinePoiLayer.js module (OSM Overpass overlay for
//             fuel docks, marinas, harbours, moorings, slipways,
//             piers, chandleries, drinking water, pump-outs).
//             LayersPanel gained ~9 toggle parameters + a new
//             child component; cached v41 WASM passing the old
//             shape into the v42 panel component would render
//             without the new section AND error on unknown
//             parameters during diff. Bump invalidates the
//             precache so the helm pulls the new framework on
//             next launch.
// v42 -> v43: Multi-plotter sync extended to waypoints / notes /
//             regions. Map.razor subscribes to ResourceStore's
//             per-type Changed/Removed events and pushes redraws
//             via MapResourceController.Redraw{Waypoint,Note,
//             Region}Async. ResourceStore.HandleRegionDelta now
//             populates OuterRings on delta-fed regions (mirrors
//             RegionApi.GetAllAsync) so the Leaflet polygon
//             actually renders. Resources page gains a manual
//             Refresh button that calls RefreshAllAsync.
// v43 -> v44: HTML-escape route name + note title before they reach
//             Leaflet's bindTooltip (leafletInterop.js addRoute /
//             updateRoute, noteLayer.js addNoteMarker). Without
//             this, a remote-edited resource name like
//             '<img src=x onerror=...>' executed on hover because
//             bindTooltip writes its string argument straight to
//             innerHTML. waypointLayer was already safe via
//             formatWaypointTooltip's existing esc() calls.
// v44 -> v45: Drop Map.razor's 60 s resourcePollTimer +
//             RefreshServerResourcesAsync. ResourceStore now keeps
//             waypoints / routes / notes / regions live via the
//             resources.* SK delta subscriptions (PR #238) and
//             reconciles via REST on every reconnect, so the
//             page-level poll was redundant work. The
//             ToggleLayersPanel-open path narrows to a radar-only
//             refresh (radar fields aren't published over the SK
//             delta stream). Per-delta handlers now flush the
//             filter cache + topbar-search index when an add /
//             remove changes the cached counts so the panel and
//             search pick up the change without waiting for a pan.
// v45 -> v46: ResourceStore snapshot caching + event-fire cleanup.
//             The Routes / Waypoints / Notes / Regions read
//             properties cache the IReadOnlyList and only rebuild
//             when the underlying dictionary mutates, so a HUD that
//             reads Count + then iterates no longer allocates a
//             fresh list per access. Event invocations route through
//             a shared SafeInvoke helper instead of ~30 inline
//             try/catch sites; Replace{Type} reuses one instance
//             HashSet for the upsert-membership lookup and only
//             allocates the stale-id list when there's actually one
//             to remove.
// v46 -> v47: Resource reconcile observability. RefreshAllAsync
//             gains a `cause` parameter ("startup", "page-mount",
//             "manual", "reconnect") that surfaces in the
//             reconcile-started + reconcile-complete structured
//             logs, so a helm reading the journal can tell why a
//             reconcile fired without correlating timestamps. The
//             reconcile-complete line now reports per-type added /
//             updated / removed counts (not just totals), so it's
//             obvious when "everything stayed the same" vs "5
//             routes deleted server-side". Connection-edge
//             transitions log explicitly so the journal shows when
//             deltas paused / resumed.
// v47 -> v48: ResourceStore extracted to a typed cache primitive
//             (ResourceTypeCache<T>). Per-type duplication -
//             dictionary, snapshot list, Changed/Removed events,
//             subscriber-throw guard, Replace logic - all collapse
//             onto one generic class; ResourceStore composes four
//             instances (one per resource type). The public API
//             (Routes / Waypoints / OnRouteChanged / ...) is
//             unchanged via custom event accessors that forward to
//             the inner cache; existing subscribers keep working
//             without any code change on their side.
// v48 -> v49: Mobile layout helm round.
//             - chart-quick chips: 10-char + ellipsis truncation in
//               C#; combined (pointer:coarse) AND (max-width:600)
//               override drops the touch-floor 44px chip height to
//               28px on phones so the strip doesn't crowd the
//               chart underneath.
//             - prev/next waypoint chevrons re-shown on phones
//               (previously display:none in the phone @media).
//               Per-leg TTG/ETA stays hidden so DTW/BRG/VMG fit
//               on one line.
//             - Route-total row drops the TTG span (variable-width,
//               sometimes wrapped onto a second line on phone);
//               keeps the fixed-width "ETA HH:MM". flex-wrap:
//               nowrap + white-space: nowrap enforce single line.
//             - --ctrl-bar-height bumped per config to match
//               actual rendered bar heights (mobile 48->58,
//               pointer:coarse new override 70). Earlier guesses
//               left the route HUD overlapping the bar; new values
//               place it exactly --panel-gap above the bar's top
//               edge across desktop / iPad / mobile / big-type.
//             - Free/Follow promoted from the More menu's phone-
//               overflow block to the always-visible bar so the
//               phone bar reads [More, Free/Follow, Layers]. The
//               More-menu row carries .ctrl-more-mobile-hide so
//               it's suppressed on phones to avoid duplicating
//               the bar version.
// v49 -> v50: Region popup + map upgrade + Resources active-route pin
//             + global prose dash style.
//             - leafletInterop.addRegion gained five params
//               (isHazard, areaSqM, centerLat, centerLon,
//               radiusMeters, createdAtIso). Cached v49 WASM
//               calls the old 4-arg shape and the new JS would
//               render the popup without metadata; bump invalidates
//               the precache so the helm picks up matched bundles.
//             - Hazard regions render in red with an always-on
//               warning glyph at the centroid; popup includes
//               area, coords, created, hazard chip, plus an Edit
//               button next to Delete.
//             - Resources page pins the active route at the top
//               with View-on-map / Stop actions.
//             - Codebase-wide prose dash style: " -- " (em-dash)
//               replaced with " - " (hyphen) in comments, README,
//               UI strings - helm-readability pass, no behavioural
//               impact.
// v50 -> v51: Range-scale chip now carries the zoom level inline
//             (e.g. "0.5 nm  | z14") so the helm reads distance and
//             zoom from the same chip; the standalone Leaflet
//             zoom-level badge (already display:none for a few
//             revisions) stays hidden. CSS + JS only; bump
//             invalidates the precache so the helm picks up the new
//             .ona-range-scale-zoom rule on next launch.
// v51 -> v52: Anchor Auto formula now factors swing + tide drop.
//             AutoRadiusPreview was "current distance + 5 m" (or
//             "armed-max + 5 m"). New shape is
//             swing + tide-drop + 5 m margin, where:
//               - swing = AnchorPeakRadius (worst-case observed
//                 since drop) -> AnchorCurrentRadius -> haversine,
//                 in priority order.
//               - tide-drop = max(0, TideHeightNow - TideHeightLow)
//                 from environment.tide.* deltas; reflects the
//                 extra effective scope the boat will swing on
//                 between now and the next predicted low water.
//               - 5 m margin: blanket safety pad.
//             AnchorEditPanel gains an AutoBreakdown parameter so
//             the chip's tooltip and the panel's eyebrow both read
//             the same helm-readable summary
//             ("swing 38 m + tide drop 4 m + 5 m margin"); the
//             eyebrow is now computed live (was a static string set
//             at panel-open) so it follows the boat as the swing
//             peak grows and tide ticks.
// v52 -> v53: Marine services master "Show" toggle + Layers panel
//             reorder + Resources Import/Refresh grouping.
//             - MarineServicesSection grows a header "Show" chip
//               that flips MarinePoiOverlayVisible (new persisted
//               setting, defaults true). When off, the controller
//               drops every marker AND skips Overpass fetches; per-
//               category picks survive a hide/show round-trip.
//               IMarinePoiSettings + AppSettingsService extended;
//               existing fakes updated.
//             - Misc section moved to the bottom of the Layers
//               panel (was above Marine services). Helm: chart-
//               related sections cluster at the top, the grab-bag
//               sits last.
//             - Resources page Import + Refresh wrapped in a single
//               flex group so they sit adjacent on the right;
//               previously space-between pushed Import to the
//               middle and Refresh to the far right.
// v53 -> v54: Hazard region warning glyph centring fix.
//             - .region-hazard-glyph picks up box-sizing: border-box
//               so the rendered disk is exactly 24x24 (was 28x28
//               under default content-box, putting the disk's
//               visual centre 2px below-right of the iconAnchor
//               coordinate).
//             - Inner glyph swapped from Unicode U+26A0 to an
//               inline SVG triangle + exclamation. The codepoint's
//               font-dependent baseline metrics rendered off-centre
//               on Safari / Firefox / Chrome each in their own way;
//               the SVG is pixel-deterministic.
// v54 -> v55: Quick-chip + circle-preview helm round.
//             - regionLayer.setCirclePreview switches from
//               colors.region (amber) to colors.current (violet)
//               so the in-progress circle reads as the same
//               "shape I'm drawing" family as route-edit and
//               polygon-edit. Finalised regions still amber.
//             - chart.Name no longer truncated in C# to 10 chars.
//               Truncation moves to a CSS clip (max-width:8em +
//               text-overflow:ellipsis) gated to the phone @media
//               so desktop / iPad get the full name and only a
//               390-wide phone reads "Bahamas No...".
//             - main:has(.big-type) .chart-quick-chip rule added:
//               font 0.92rem, padding 6/14, radius 16 -- ~2pt
//               taller than the default and noticeably bigger than
//               the touch (pointer:coarse) override on the helm
//               console where big-type lives.
// v55 -> v56: Alarms helm round.
//             - Per-rule disable: Settings -> Alarms now lists
//               every registered rule with a switch + a master
//               on/off; AlarmManager.Evaluate skips disabled rules
//               and drops their already-active banners.
//             - Default thresholds retuned for fewer false
//               positives: SHALLOW 3->2 m, ANCHOR TIDE 1->0.5 m,
//               WIND SHIFT 15->30 deg, lookback 5->10 min, min TWS
//               3->5 kn. Existing helms keep their stored values.
//             - APPROACH self-clears on auto-advance: the system's
//               auto-advance now dismisses the latched banner the
//               same way the manual Next-WP tap on the alarm does,
//               so the helm doesn't see a stale APPROACH for a
//               waypoint they just sailed past.
// v56 -> v57: Mobile alarm overlay round.
//             - Phone (<=600px): the floating alarm-banner stack
//               is hidden. Active alarms surface in the alarm-log
//               overlay (see next bullet) and the topbar history
//               button blinks a danger-tinted pulse so the helm's
//               eye still catches a fire.
//             - Alarm-log overlay grows an "Active" section above
//               the existing "Recent" history list - on every
//               layout, not just mobile. Each row carries the same
//               Snooze / Next-WP / Hold-to-dismiss affordances the
//               banner offered, so the overlay is now a complete
//               actionable view instead of read-only history.
//             - Phone overlay goes full-screen (top:0 + 100dvh)
//               instead of the previous 75vh bottom-sheet so the
//               Active rows + the Recent list both fit without a
//               separate scroll container.
//             - Topbar button visibility broadens to include
//               "active alarms exist" -- previously gated on
//               history / snoozed only, which left phones with no
//               affordance for live alarms.
// v57 -> v58: Coalesce ResourceStore.OnRouteChanged storms during
//             reconnect-edge reconcile. The cache fires Changed
//             for every upserted entry on the reconnect-edge
//             reconcile pass; on a 93-route boat that's 93
//             fire-and-forget tasks racing through the same JS-
//             interop path simultaneously, which on the Pi WASM
//             host blew the 5 MB stack ("RuntimeError: memory
//             access out of bounds" in the renderer trace) every
//             time the WS dropped + reconnected. Map.razor's
//             HandleRouteChangedFromStore now queues the id and
//             schedules a single drain via Task.Yield; subsequent
//             events fold into the pending set, the drain
//             serialises the per-id work. Routes were the
//             demonstrated culprit (~93); waypoints / notes /
//             regions stay on the per-id path until they show
//             the same symptom.
// v58 -> v59: Settings -> Alarms hides the empty-title row.
//             ServerNotificationsAlarmRule's Title is "" by
//             design (it's a multi-output bridge that emits
//             per-notification titles), which previously rendered
//             a label-less toggle in the per-rule list. The UI
//             loop now skips empty-title rules so the visible
//             list matches the description above it (server-side
//             notifications stay independently armed). All-off
//             master already short-circuited via the setter's
//             empty-key guard, so its behaviour is unchanged.
// v59 -> v60: Settings page restructured.
//             - Mode -> "HUD & input" header break so the chart
//               upscale / autopilot HUD / radar HUD / keep-screen
//               toggles read as a group instead of squatting
//               under the boat-class header.
//             - Alarms section: per-alarm cards each carry their
//               own Enabled toggle (new AlarmRuleEnableToggle
//               component); standalone "Enable client-side
//               alarms" card removed; master "All on/off" stays
//               at the top.
//             - Map elements (Own/AIS COG vector, radar range
//               rings) and Course (server-side approach,
//               waypoint arrival radius, auto-advance) split
//               out from Alarms into their own headers.
//             - Anchor section: ANCHOR DRAG + ANCHOR TIDE per-
//               rule cards plus the existing tide-alarm setup
//               checklist plus a NEW "Auto anchor radius" card
//               showing the swing+tide+margin formula and a
//               tunable safety-margin field (replaces the 5 m
//               const in Map.razor; new
//               AnchorAutoRadiusSafetyMargin setting persisted
//               under "anchorAutoRadiusSafetyMargin.v1").
//             - Advanced section collapsed by default
//               (advancedOpen field, in-memory only).
// v60 -> v61: Topbar + alarm overlay polish.
//             - .topbar-icon-btn + .alarm-log-btn drop their
//               fixed heights and pick up align-self: stretch so
//               every topbar button fills the full 3 rem row;
//               helm flagged the previous 2 rem-ish buttons as
//               under-using the band + awkward to tap.
//             - Alarm-log button icon switches by state: warning
//               triangle (Icons.Small.Warning, new) when active
//               alarms are firing, clipboard / note-block (the
//               existing History) when only the dismissed-history
//               is non-empty.
//             - Mobile overlay starts at top:3rem (below the
//               topbar) instead of top:0; topbar stays visible so
//               the connection chip + the now-blinking alarm
//               button are still glanceable while reviewing the
//               list. The grab-handle pill is gone -- the X on
//               the right is the single close affordance.
// v61 -> v62: Settings page tighter layout.
//             - Section reorder: Display first, Mode second, HUD
//               third (was Mode -> HUD & input -> ... -> Display
//               at the bottom). Helm asked the look-and-feel
//               picker to be the first thing the page shows.
//             - "HUD & input" renamed to "HUD". The input bits
//               (keep-screen-awake, show keyboard hints) moved
//               into Display alongside Theme + Night Mode + Big
//               screen mode; they're all global look-and-feel
//               knobs.
//             - The big Night Mode card split: Night Mode +
//               presets + Auto-flip in one card; Big screen
//               mode in its own card.
//             - Show tide + Expand all HUD panels moved out of
//               the night-mode card into the HUD section as
//               separate cards.
//             - Most col-12 content cards converted to col-sm-6
//               so two cards fit per row on >= sm breakpoints.
//               Auto anchor radius and Boat polars stay col-12
//               (the formula + the polar plot really need the
//               full width).
// v62 -> v63: Topbar button + dialog rename.
//             - "Alarm History" / "Active N" topbar label unified
//               as "Notifications N" (matches SK v2 vocabulary +
//               how helms talk about the banner stack). Icon +
//               badge already convey active vs history; the label
//               doesn't need to flick between two strings.
//             - Dialog title "Alarms" -> "Notifications". Empty-
//               state strings + the +N-more banner overflow
//               tooltip retitled to match.
// v63 -> v64: Two-layer hardening for the route-storm crash family.
//             (1) Source-side dedup in ResourceTypeCache.Replace:
//                 a content-equality function per resource type
//                 suppresses Changed for entries that didn't
//                 actually mutate during the disconnect window
//                 (steady cruise = zero diffs = zero events).
//             (2) Per-handler coalesce-and-drain extended to
//                 waypoints / notes / regions, mirroring the v58
//                 route fix; the region drain hoists the
//                 RegionStore.SetRegions call out of the per-id
//                 loop so HazardousRegionAlarmRule re-evaluates
//                 once against the final state, not N times.
//             Plus operator-fitness fixes:
//             - NotificationsApi catches per-call OperationCancel-
//               edException so MOB raise / alarm publish / ack /
//               clear surface stalls as ApiResult.Fail instead of
//               faulting the fire-and-forget Task. Was the silent-
//               death cause for the MOB retry loop.
//             - MobService.RunRaiseLoopAsync gets a top-level
//               try/catch + per-attempt retry catch so any
//               unexpected throw logs and continues rather than
//               killing the most life-critical retry path.
//             - AlarmPublisher republishes stable alarms after WS
//               reconnect; previously a SHALLOW that survived a
//               disconnect was invisible to other plotters until
//               something local mutated.
//             - SignalKNotificationAcknowledger logs ack failures
//               via Console.Error (relay -> SK server log) so
//               cross-plotter ack stalls are debuggable.
// v64 -> v65: Wire navigation.course.arrivalCircle (SK v2 Course
//             API) so the chart's arrival ring + the client APPROACH
//             alarm threshold + the route HUD all use the SAME
//             radius the server uses to fire arrivalCircleEntered.
//             Previously each leg's "you've arrived" boundary
//             differed between client (Settings.WaypointArrivalRadiusMeters,
//             default 50m) and server (per-route author's value), so
//             the helm could see APPROACH banner + chart ring in one
//             place while the SK course-provider considered them
//             outside the circle. Local setting becomes a fallback
//             for minimal SK installs that don't publish the path.
// v65 -> v66: Drop the local Settings.WaypointArrivalRadiusMeters
//             fallback. navigation.course.arrivalCircle is now the
//             ONLY source for the chart arrival ring + the client
//             APPROACH alarm threshold. Server silent = ring hidden,
//             client rule dormant. Eliminates the drift class where
//             the helm-set fallback could disagree with what the
//             course-provider plugin used to fire arrivalCircleEntered.
//             AppSettingsService loses the property + storage key
//             ("waypointArrivalRadiusMeters.v1" is intentionally not
//             migrated); IAlarmThresholds + Settings page input gone.
// v66 -> v67: Fix CPA banner trailing "- ," when COLREGS classifier
//             returns Indeterminate / None. Colregs.ShortLabel and
//             RoleLabel now return string? (null) for those cases
//             instead of empty string; CpaAlarmRule's existing
//             `is not null` check now correctly suppresses the
//             comma-separated suffix when there's no useful label.
// v67 -> v68: Three small adjustments.
//             - AisLabelsVisible setting (default ON): toggle vessel
//               name labels independent of harbor mode. Layers panel
//               gets a checkbox under Misc; the JS gate ANDs the
//               flag with !harborMode so harbor mode still hides
//               labels while it's on.
//             - Stop fanning out cache invalidation on every settings
//               change. SignalkClient, MarinePoiController, and
//               Map.HandleSettingsChanged now snapshot the inputs
//               they actually consume and skip the JS interop /
//               OnDataChanged broadcast / fetch reschedule when
//               nothing relevant changed. Previously a font-size
//               toggle re-pushed every JS state and re-scheduled
//               the marine-POI Overpass fetch.
//             - Night mode collapsed to a single binary toggle.
//               Dropped NightModePreset (dusk / soft / amber / red)
//               + the four-step cycle button + the .night-mode-amber
//               and .night-mode-red CSS variants. Helms wanting a
//               non-red dark intermediate use Theme = "dark"
//               instead.
// v68 -> v69: Settings > Anchor: "Auto anchor radius" card narrows
//             from col-12 to col-sm-6 so it pairs with the Tide
//             Alarm setup card on the same row. <pre> formula
//             gets pre-wrap + word-break so half-width on tablet
//             doesn't horizontal-scroll.
// v71 -> v72: Fix "Cannot read properties of undefined (reading
//             'appendChild')" Leaflet crash that emptied the chart
//             after enabling Radar HUD. Three-layer fix:
//             (1) leafletInterop.initMap calls a new
//                 tearDownAllRadarOverlays before map.remove() so
//                 stale radar records can't survive a navigate-away-
//                 and-back pointing at a destroyed map's wiped panes.
//             (2) radarLayer.setRangeRingsConfig defensively checks
//                 each record's map.getPanes().overlayPane and skips
//                 (with a one-shot destroy+log) any record whose map
//                 is dead. Belt-and-braces if a future code path
//                 leaks a record without going through initMap.
//             (3) MapControlsJs.InvokeSafe now also catches
//                 JSException, logs to Console.Error (relay -> SK
//                 server log), and returns. A future JS regression
//                 can no longer take Blazor's renderer down for the
//                 rest of the session.
// v72 -> v73: Radar spokes hot-loop perf:
//             (1) Uint32Array view over imageData.data + byteToRgba
//                 buffers; the paint loop now writes one 32-bit word
//                 per filled pixel instead of four bytes (~1.5x
//                 faster on busy chart conditions). Open-water early-
//                 out is unchanged.
//             (2) Spoke object pool in radarProtobuf - decodeSpoke
//                 resets fields in place on a pooled object instead
//                 of returning a fresh literal per spoke. Cuts
//                 ~1k allocs/sec on HALO 31 + keeps V8's hidden
//                 class monomorphic across frames.
//             (3) Hoisted lut/xLut/yLut + spoke.data refs to locals
//                 inside _paintSpoke so the JIT doesn't re-read
//                 them per iteration.
// v75 -> v76: General performance sweep. Three packages:
//             A) AIS push pipeline. AisPushService.BuildSnapshot
//                pools AisVesselPayload instances and writes fields
//                in place; the per-tick allocation drops from 200
//                anonymous-type objects to one array. JS aisLayer
//                reuses a [lat,lon] scratch tuple, skips setLatLng
//                when the position is unchanged, hoists getElement()
//                once per vessel, and rotateMarker caches the inner
//                <svg> + last-applied degree.
//             B) Boot path. Vendored Leaflet 1.9.4 (no longer
//                blocks first paint on cross-origin unpkg fetch;
//                works PWA-offline). atonLayer + marinePoiLayer
//                cache divIcons by category and skip setIcon when
//                the cached ref hasn't changed.
//             C) Per-vessel CPU. AisPalette.ShipTypeColor +
//                ShipTypeCategory + Colregs.FromAisShipType use
//                StringComparison.OrdinalIgnoreCase instead of
//                allocating a lowercased copy. AisSart.CategoryFromAny
//                fast-paths on "no mmsi:9 in context" before the
//                substring extract.
const CACHE_NAME = 'ona-plotter-v76';
const TILE_CACHE_NAME = 'ona-plotter-tiles-v1';
// Cap on the tile cache. Approx 5000 tiles * ~40 kB = 200 MB which
// is comfortable on iPad / desktop and fits one or two full route-
// planning sessions. Eviction is FIFO by insertion order
// (trimTileCache below deletes from the front of cache.keys()); the
// earlier LRU promotion on hits was dropped after helm reports of
// "no single tile loads" pointed at body-stream contention between
// the served response and the background re-put. With a 5000 entry
// cap, FIFO is plenty for typical coastal / rivers use - the home
// anchorage tiles fall out only after several sessions of heavy
// route-planning elsewhere. Bump this number rather than switching
// to a separate offline-tiles feature.
const TILE_CACHE_MAX_ENTRIES = 5000;
// Use relative URLs so the worker works both at root and under a subpath
// (SignalK webapp serves at /signalk-onaplotter/).
const SCOPE = self.registration ? self.registration.scope : self.location.href;
const APP_SHELL = [
    SCOPE,
    new URL('css/app.css', SCOPE).toString(),
    new URL('favicon.svg', SCOPE).toString(),
    new URL('manifest.json', SCOPE).toString(),
    // errorRelayBoot.js is loaded synchronously from index.html before
    // the Blazor runtime so the relay listeners are armed in time to
    // catch boot-time exceptions. Precaching it here means a stale
    // copy is invalidated cleanly when CACHE_NAME bumps; without it
    // the catch-all opportunistic cache could pin an old broken copy.
    new URL('js/errorRelayBoot.js', SCOPE).toString(),
    // PNG icons for the iPad / iOS home-screen install path. The SVG
    // is still listed as a manifest icon (Android handles SVG fine)
    // but Safari needs the rasterised versions for apple-touch-icon
    // and PWA splash; precache so the install flow works offline.
    new URL('apple-touch-icon-180.png', SCOPE).toString(),
    new URL('icon-192.png', SCOPE).toString(),
    new URL('icon-512.png', SCOPE).toString(),
    // Vendored Leaflet 1.9.4 (was loaded from unpkg.com synchronously
    // in index.html, blocking WASM start on slow links AND breaking
    // PWA offline mode). Precaching the JS + CSS + per-CSS image
    // refs means cold-start fetches one fewer cross-origin asset and
    // the helm can boot the chartplotter offline once the bundle's
    // installed. Bump CACHE_NAME to invalidate stale copies on
    // future Leaflet upgrades.
    new URL('lib/leaflet/dist/leaflet.js', SCOPE).toString(),
    new URL('lib/leaflet/dist/leaflet.css', SCOPE).toString(),
    new URL('lib/leaflet/dist/images/layers.png', SCOPE).toString(),
    new URL('lib/leaflet/dist/images/layers-2x.png', SCOPE).toString(),
    new URL('lib/leaflet/dist/images/marker-icon.png', SCOPE).toString(),
    new URL('lib/leaflet/dist/images/marker-icon-2x.png', SCOPE).toString(),
    new URL('lib/leaflet/dist/images/marker-shadow.png', SCOPE).toString()
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => cache.addAll(APP_SHELL))
    );
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) =>
            Promise.all(keys
                .filter((k) => k !== CACHE_NAME && k !== TILE_CACHE_NAME)
                .map((k) => caches.delete(k)))
        )
    );
    self.clients.claim();
});

// Cache-first for tile URLs. Wraps the inner cache-then-fetch logic
// in a top-level try/catch that always returns *some* Response, so
// the respondWith() promise never rejects. A bare rejection is what
// Firefox surfaces as "ServiceWorker intercepted the request and
// encountered an unexpected error" and renders as a broken tile in
// Leaflet - the cause is hard to pin (Cache-API quota, transient
// IndexedDB corruption, body-clone on a partial 206, ...) but the
// effect is uniform and so is the mitigation: any exception path,
// however unlikely, falls back to a 504 placeholder.
async function tileCacheFirst(request) {
    try {
        return await tileCacheFirstInner(request);
    } catch (_) {
        // Last-resort fallback: a 504 placeholder so Leaflet can
        // render its errorTileUrl rather than the helm seeing the
        // generic Firefox SW-intercept error in the console plus a
        // missing tile. This branch should be unreachable - the
        // inner function has its own per-step guards - but the
        // outer net is what guarantees respondWith() never rejects.
        return new Response('', { status: 504, statusText: 'Tile error' });
    }
}

// Module-scoped cache reference. caches.open() is fast on a hot disk
// but each call still spins up a transaction; on a Pi-class device
// the per-request open shows up in flame-graphs for tile-heavy
// workloads. Cache the resolved Cache object after first open and
// reuse for subsequent requests. The Cache object stays valid across
// the SW's lifetime; if it's ever invalidated (extremely unusual),
// the caller's try/catch falls through to the network path.
let tileCacheRef = null;
async function getTileCache() {
    if (tileCacheRef) return tileCacheRef;
    tileCacheRef = await caches.open(TILE_CACHE_NAME);
    return tileCacheRef;
}

// Cumulative-puts counter that throttles the trimTileCache scan.
// trimTileCache used to fire after every successful put, which meant
// a full cache.keys() walk (5000 entries on a busy passage) on every
// tile cached - the bookkeeping cost dwarfed the actual put. Now we
// only scan after every TRIM_CHECK_EVERY puts, so a single tile-load
// burst pays the scan once instead of N times. Choice of 50 keeps
// the high-water-mark slop bounded (we may exceed the cap by ~50
// entries before the next trim catches up; 5050 vs 5000 is fine).
const TRIM_CHECK_EVERY = 50;
let putsSinceLastTrim = 0;
let trimInFlight = false;

async function tileCacheFirstInner(request) {
    let cache = null;
    try {
        cache = await getTileCache();
        const cached = await cache.match(request);
        if (cached) {
            // Just serve the cached response. Earlier versions did an
            // LRU promotion via cache.put(request, cached.clone()) so
            // that trimTileCache evicted least-recently-used entries.
            // Helm reports of "no single tile loads" point at the
            // clone path: in some browser builds (Firefox in
            // particular) the clone's body stream and the served
            // response share underlying state in a way that the
            // background put can lock, breaking every subsequent
            // tile read. The pure-FIFO eviction we land on without
            // promotion is good enough - with a 5000-tile cap a
            // home anchorage stays in the cache for several
            // sessions of normal use, and the alternative
            // (every-tile failure) is not a trade we'd accept.
            return cached;
        }
    } catch (_) {
        // Cache layer unavailable; degrade to a pure-network path
        // below. cache stays null, the put attempt is skipped.
    }

    let response;
    try {
        response = await fetch(request);
    } catch (_) {
        // Offline + no cache entry. Return a harmless 504 so Leaflet
        // shows its errorTileUrl placeholder rather than hanging.
        return new Response('', { status: 504, statusText: 'Offline' });
    }

    // Best-effort cache write. cache.put rejects on partial-content
    // (206), no-store headers, quota exceeded, and a few other Response
    // shapes that the spec disallows. None of those should affect the
    // caller - swallow the rejection and just return the response.
    if (cache && response.ok) {
        try {
            const clone = response.clone();
            // Defensive: cache.put can throw synchronously on some
            // browser versions (Firefox under low-memory, Safari with
            // partial-storage quotas). The .catch chains the async
            // rejection; the try/catch traps the sync throw so the
            // outer respondWith path stays clean.
            try {
                cache.put(request, clone)
                    .then(() => maybeTrimTileCache(cache))
                    .catch(() => { /* put rejected; tile not cached, fine */ });
            } catch (_) { /* sync throw from cache.put */ }
        } catch (_) { /* clone() threw on a weird response body */ }
    }
    return response;
}

// Throttled + single-flight trim. Counts puts; only scans the cache
// every TRIM_CHECK_EVERY puts, and never runs more than one trim at
// a time. Prevents the cache.keys() walk from running on every put
// (was a measurable perf hit at 5000+ entries) while still keeping
// the cap honest within ~50 entries of slop.
async function maybeTrimTileCache(cache) {
    putsSinceLastTrim++;
    if (putsSinceLastTrim < TRIM_CHECK_EVERY) return;
    if (trimInFlight) return;
    putsSinceLastTrim = 0;
    trimInFlight = true;
    try {
        const keys = await cache.keys();
        if (keys.length <= TILE_CACHE_MAX_ENTRIES) return;
        // Delete oldest in parallel (FIFO by insertion order). Cache.keys()
        // returns in insertion order per the spec; sufficient for our
        // approximate eviction. Promise.all over the slice rather than
        // sequential awaits cuts trim time by ~10x on a Pi-class device
        // when many entries need eviction at once.
        const excess = keys.length - TILE_CACHE_MAX_ENTRIES;
        await Promise.all(keys.slice(0, excess).map((k) => cache.delete(k)));
    } catch (_) {
        // Trim is best-effort; cache cap may be temporarily exceeded
        // but the next put will trigger another attempt.
    } finally {
        trimInFlight = false;
    }
}

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Only handle GET requests for same-origin resources.
    // Cross-origin (OSM tiles, OpenSeaMap, unpkg.com, MarineTraffic links) must pass
    // through untouched: the browser sends proper Referer headers that some servers
    // (like OSM) require per their usage policy. Intercepting them breaks those requests.
    if (event.request.method !== 'GET' || url.origin !== self.location.origin) {
        return;
    }

    // Chart tiles served by signalk-charts-plugin. Cache-on-view so a
    // re-visit to a cove the user sailed through earlier works offline.
    // Separate cache from the app shell so it can be evicted
    // independently and capped by entry count. Cache-first: tiles
    // never invalidate on their own (charts don't change), so a
    // single freshness pass at install / cache-bump is enough. The
    // FIFO cap rolls in newer tiles to displace stale ones over time.
    if (url.pathname.startsWith('/signalk/chart-tiles/')) {
        event.respondWith(tileCacheFirst(event.request));
        return;
    }

    // Network-first for other SignalK API calls and WebSocket upgrades.
    // Match /signalk/ and /signalk/v* paths exactly (not webapp names like /signalk-onaplotter).
    // Wrap in try/catch + 504 fallback so a fetch rejection (offline,
    // CORS, abort) never bubbles into the respondWith promise. Without
    // this guard, Firefox surfaces every transient network blip on a
    // /signalk/* path as the same "ServiceWorker intercepted ...
    // unexpected error" message that the tile cache handler had
    // pre-v20.
    if (url.pathname === '/signalk' || url.pathname.startsWith('/signalk/')
        || event.request.mode === 'websocket') {
        event.respondWith(passThroughOrPlaceholder(event.request));
        return;
    }

    // Network-first for "evolves with C# bundle" assets (JS modules,
    // CSS, the SPA document root). C# code that calls a JS export
    // ships in a content-hashed wasm filename, so the new wasm is
    // always fetched fresh - but the JS module URLs are stable
    // (`./js/leafletInterop.js`, no hash), so a cache-first SW would
    // happily serve yesterday's leafletInterop.js (no new export) to
    // today's wasm (which calls it) and crash on the
    // not-a-function lookup. Network-first fixes that without losing
    // offline launch (cache is the fallback when fetch rejects).
    if (url.pathname.startsWith('/js/')
        || url.pathname.startsWith('/css/')
        || url.pathname === '/'
        || url.pathname === SCOPE.replace(self.location.origin, '')
        || url.pathname.endsWith('.html')) {
        event.respondWith(networkFirstWithCacheFallback(event.request));
        return;
    }

    // Cache-first for app shell (icons, fonts, version stamps), network
    // fallback otherwise. Wrapped for the same reason as above:
    // caches.match / caches.open / put can all reject; the outer try
    // guarantees respondWith resolves to a Response.
    event.respondWith(appShellCacheFirst(event.request));
});

/** Network-first with cache fallback. Always tries network first so
 *  a fresh deploy lands in the helm's browser on the next page load
 *  rather than the load-after-that. The browser-level HTTP cache +
 *  Kestrel's ETag/no-cache headers keep the wire cost down (304 with
 *  no body when unchanged). On network failure (offline / refused),
 *  serves the previously-cached copy so the app still launches.
 *  Caches every successful 2xx response so the offline fallback has
 *  something to fall back to. */
async function networkFirstWithCacheFallback(request) {
    try {
        const response = await fetch(request);
        if (response.ok) {
            try {
                const clone = response.clone();
                const cache = await caches.open(CACHE_NAME);
                cache.put(request, clone).catch(() => { /* best-effort */ });
            } catch (_) { /* clone / open / put threw */ }
        }
        return response;
    } catch (_) {
        // Offline / refused / aborted. Fall back to whatever the
        // cache has from a previous online visit.
        try {
            const cached = await caches.match(request);
            if (cached) return cached;
        } catch (_) { /* cache layer unavailable */ }
        return new Response('', { status: 504, statusText: 'Offline' });
    }
}

/** Plain pass-through fetch with a safety net. Used for /signalk/*
 *  and WebSocket upgrades - we don't cache or transform those, but
 *  we still own the respondWith promise and need to settle it. */
async function passThroughOrPlaceholder(request) {
    try {
        return await fetch(request);
    } catch (_) {
        // Offline / aborted / blocked. 504 lets the caller (the
        // SignalK API client or the Blazor circuit) handle it as a
        // server error rather than an undefined-response intercept.
        return new Response('', { status: 504, statusText: 'Offline' });
    }
}

/** Cache-first with stale-while-revalidate for the app shell.
 *  Returns the cached Response if present, then refreshes the cache
 *  in the background; on cache miss, awaits the network. Any
 *  exception in the cache layer falls through to a pure-network
 *  attempt; any exception in the network falls through to whatever
 *  cached entry we had (or a 504 placeholder). respondWith never
 *  rejects. */
async function appShellCacheFirst(request) {
    let cached;
    try {
        cached = await caches.match(request);
    } catch (_) { /* cache layer unavailable */ }

    // Background refresh on hit, awaited fetch on miss.
    const fetchPromise = (async () => {
        try {
            const response = await fetch(request);
            if (response.ok) {
                try {
                    const clone = response.clone();
                    const cache = await caches.open(CACHE_NAME);
                    cache.put(request, clone).catch(() => { /* best-effort */ });
                } catch (_) { /* clone / open / put threw */ }
            }
            return response;
        } catch (_) {
            // Network failed; return cached if we have it, else 504.
            return cached ?? new Response('', { status: 504, statusText: 'Offline' });
        }
    })();

    // Cache hit: serve cached + let the refresh run in the background.
    if (cached) {
        // Don't await fetchPromise - let it update the cache silently.
        // .catch keeps an unhandled rejection out of the SW's error
        // bus.
        fetchPromise.catch(() => { /* best-effort */ });
        return cached;
    }
    // Cache miss: await network (or its 504 fallback above).
    return fetchPromise;
}
