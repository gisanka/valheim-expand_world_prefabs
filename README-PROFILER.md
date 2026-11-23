# EWP Internal Profiler — diagnostics-only source variant

This branch is upstream/main at dec72f326a8a08d63ca93562a270d230dde7bbe2 (EWP 1.67) plus profiler instrumentation. It preserves aggregate timing, rule provenance, object-search counters, deferred trace correlation, spawn diagnostics, player lifecycle markers and the bounded Enigma path profiler.

PR #5 teleport synchronization, gameplay session gates, export contracts, config migration, terrain changes and ownership experiments are absent. Upstream controls gameplay, queues, RPC delivery, ownership, saves and YAML reloads.

Validation: the mod and test project compile in Release; all 354 tests pass. No in-game runtime validation has been performed. The NuGet vulnerability-data request returned NU1900, so no successful vulnerability audit is claimed.

## Main insertion points

- `PrefabLoading.cs` assigns a stable provenance key to each instantiated `Rule` during loading: **full YAML file path + 1-based script-entry index + action type + prefab + all parsed action parameters**. Not just `S0002` or the first poke token. Multiple `types` expansions remain individually identifiable.
- `PrefabManager.cs` profiles global/local event entry, selection, individual rule execution, data write, poke building.
- `InfoSelector.cs` attributes `objects` and `bannedObjects` predicate search work to the **specific candidate rule**, even when a rule ultimately fails selection.
- `DelayedPoke.cs` measures global/local poke building and synchronous dispatch, without changing chance, order, targets, or delays.
- `PokeTimers.cs` stores **in-memory only** trace context on scheduled entries and reuses it for dispatch. The persisted format and server-side data payload are unchanged (format remains `2`); restored timers start new trace chains.
- `ObjectsFiltering.cs` profiles `GetNearby` (both overloads), `HasNearby`, `HasNotNearby`, and `GetCounts`. Candidate counts are incremented *during the existing iteration*, with no extra enumeration.
- `ExpandWorldPrefabs.cs` measures plugin Awake and selected LateUpdate queue processing and controls capture lifetime; `PeerManager.cs` marks every join and observed disconnect. A lightweight Harmony postfix on `ZNet.SaveWorld` queues a report checkpoint, and a shutdown prefix flushes the final segment.

## How to build

The upstream project targets **net4.8** with language `preview` and uses `Directory.Build.props.user` for local reference paths. The optional `EwpLibsDirectory` property selects isolated profiler build references; `EwpSkipCopyDll=true` suppresses the upstream copy target.

A Windows convenience script is included. It discovers DLL references from a local Valheim installation and the specified Gale profile, then performs an incremental build without deployment:

```powershell
.\build-profiler.bat
# or specify explicit local paths:
.\build-profiler.ps1 -ValheimPath "C:\Path\to\Valheim" -ProfileName Enigma_FiWi
```

**Compile-time assembly publicization (v1.3):** EWP accesses many nonpublic Valheim fields (such as `ZDO.m_position`, `ZDOExtraData.s_floats`, and `ZDOMan.m_objectsByID`), so raw game references cause hundreds of CS1061/CS0117 errors. The project now references `BepInEx.AssemblyPublicizer.MSBuild` **0.4.3** with `Publicize="true"` on game assemblies. Its MSBuild task makes *temporary compile-time copies* available to the compiler and generates access-check compatibility handling; the original Valheim DLLs are never changed. Initial NuGet restore needs network access.

The build script now uses the project-local **`.ewp-profiler-libs/`** reference folder by default rather than a shared `..\Libs` folder. It copies original game/BepInEx dependencies into that private folder, compares SHA-256 to refresh stale files following a game update, and avoids touching unchanged references. The original project `CopyDLL` target is skipped for scripted profiler builds, and `dotnet build` now uses incremental compilation instead of an unconditional Rebuild. `-LibsDirectory` optionally overrides this destination, so avoid pointing it at any shared reference folder.

`YamlDotNet.dll` and other game/BepInEx dependencies must actually be installed. Windows .NET Framework 4.8 targeting pack and a recent .NET SDK may be needed. **The publicizer only changes visibility; it cannot restore methods removed or renamed in Valheim 1.0.** Remaining errors after this change should be addressed against the actual current game assemblies, one API difference at a time. Do not deploy over a live modded server or assume binary parity with the server's customized EWP.

## Configuration

Config section `[EWP Profiler]` in the standard EWP BepInEx config (exact key spellings as shown):

| Key | Default | Behavior |
|---|---:|---|
| `EnableProfiler` | `true` | Enables automatic server-start and first-join capture. Manually starting by console is allowed regardless of this value. |
| `CaptureOnPlayerJoin` | `true` | End the startup capture on the first join; if profiling is inactive, a join starts a capture. All joins are recorded as markers while an active capture continues. |
| `CaptureOnServerStart` | `true` | Start an independent startup phase in headless plugin Awake, otherwise on first server tick. |
| `CaptureStartupSeconds` | `180` | Startup-phase maximum duration; ends early if first player joins during this phase. |
| `CaptureSeconds` | `180` | Segment length: automatic report and continuation when continuous profiling is enabled; otherwise automatic stop. |
| `EnableContinuousProfiling` | `true` | Keep profiling after startup and gameplay segments; create a fresh segment after each report until server shutdown. Requires `EnableProfiler=true`. |
| `ReportOnWorldSave` | `true` | Flush the current segment when `ZNet.SaveWorld` initiates a server world save, deferring report I/O until the next `EWP.LateUpdate`. |
| `EnableDetailedEvents` | `true` | Retain extra individual trace events at threshold / 4; threshold-qualified slow calls are always recorded even when this is false. |
| `SlowCallThresholdMs` | `25` | Slow-event inclusive wall-clock threshold. |
| `TopN` | `30` | Summary rows (TSV files retain every aggregate group). |
| `EnableZdoSearchProfiling` | `true` | Count search candidates/matches and time calls. |
| `EnableCallChainProfiling` | `true` | Group linked synchronous/deferred calls by trace ID. |
| `EnableAllocationDiagnostics` | `true` | Sample **global** GC counts/heap size at capture start/stop only. |
| `RecordAllSpawnEvents` | `true` | Record individual EWP-created spawn events within the configured retention limit. |
| `RuleDetailFilter` | `*` | Time all instrumented per-rule phases; use substring to target particular rules. |
| `CaptureSlowRootTrees` | `true` | Preserve bounded synchronized slow-root hierarchy for long calls. |
| `SlowRootThresholdMs` | `100` | Minimum time for retaining a slow root. |

Commands (run on server where Terminal commands are available):

- `ewp_profiler_start` — starts/restarts a fresh capture when not already running.
- `ewp_profiler_snapshot` — writes current reports immediately and starts a fresh segment.
- `ewp_profiler_stop` — immediately stops and writes reports (no automatic restart after this manual stop).

With `EnableProfiler = true`, the dedicated-server startup phase starts from the EWP plugin `Awake` in headless mode. On a non-headless server, the fallback starts at the first `LateUpdate` detecting a server (earlier initialization is necessarily missed). The first relevant player join starts the separate gameplay capture; if it occurs while the startup capture is still active, that startup capture ends early and its report is written before gameplay capture begins. Subsequent joins are marked in the currently running segment without rotating or restarting it. If profiling is inactive and `CaptureOnPlayerJoin=true`, a later join starts a new capture. With `EnableContinuousProfiling=true`, the first startup and first-join captures remain separately identified, then regular `CaptureSeconds` segments continue until shutdown. A server world save also closes the current segment and triggers a new one on the next `LateUpdate`; the world save Harmony postfix only signals this and performs no disk I/O. With continuous profiling disabled, the older one-shot stop behavior is preserved. Manual commands allow captures on the same server session.

## Output

Files are created as one timestamped run directory:

`<expand_world base directory>/logs/EwpProfiler/YYYYMMDD_HHMMSS_fff_<phase>/` (Europe/Berlin civil time, including CET/CEST DST switching; independent of server timezone)

1. `ProfilerSummary.txt` — capture UTC/server/Germany start/end, segment end reason, trigger, source version, totals, top expensive rule scopes and searches, caveats, optional whole-process GC deltas.
2. `ProfilerRules.tsv` — **all scoped phases** (not only rules). Filter `Kind=rule` for successfully selected rule execution; `Kind=rule-condition-objects` / `rule-condition-excluded` for expensive checks of candidate rules *even when their conditions did not pass*. Columns: count, inclusive/exclusive, average, max, approximate p50/p95/p99.
3. `ProfilerSearches.tsv` — search owner context, operation, global/sector selection, calls, aggregate/max wall time, evaluated candidate visits, filter passes, result IDs or predicate outcome, zero-match calls, candidate-visits-per-result/filter-pass (NA if denominator zero), observed radius range. Predicate methods return a boolean (not number of objects).
4. `ProfilerChains.tsv` — trace ID, origin rule if known, sum of instrumented **exclusive** wall-clock work, span/rule/deferred/search counts, candidate visits, queued targets and max span. This is *not* elapsed real time between asynchronous schedule and execution.
5. `ProfilerDirectRules.tsv` — per actual rule-context sum of exclusive slices, including search work during candidate-rule conditions.
6. `ProfilerCausedRules.tsv` — aggregated causal cost for each known original rule across its chains, including delayed descendants; unidentified timer origins are excluded.
7. `ProfilerSlowEvents.tsv` — bounded individual scopes over threshold (or threshold / 4 in detailed mode), with IDs and parent ID, relative timestamp. Captures threshold-qualified slow calls even when detailed mode is off.
8. `ProfilerTimeline.tsv` — elapsed-second bucket, sum of top-level instrumented scope wall times and longest top-level scope, plus search wall-time, visits and filter passes per second. Added `PlayerJoins` and `PlayerLeaves` marker counts.
9. `ProfilerPlayerEvents.tsv` — all actual player joins and disconnects occurring during this segment: join ordinal (server-session connection number), Player-ZDO identity, elapsed time in milliseconds and seconds, UTC and Europe/Berlin clock time. No player names or addresses are stored. A long scope is counted at its **completion** second (not apportioned among seconds).
10. `ProfilerPhases.tsv` — complete EWP update phase durations, grouped by completion second: `SecondSinceStart`, `Phase`, `Calls`, `TotalWallMs`, `MaxCallMs`. Phases are `created`, `changed`, `spawn`, `remove`, `pokes`, `exec`, `rpc`, `terrain`, `owner` and `saved-data`. They wrap HandleCreated, HandleChanged, the respective delayed queues, and DataStorage.SaveSavedData. All executed calls, including empty queue checks, are counted while the profiler is active. Whole-capture phase aggregates also appear in `ProfilerRules.tsv` under `Kind=tick`. These inclusive durations contain nested rule/search work and must not be added to those costs; they measure processing, not configured delays or queue waiting. Phase values include all scripts, not just Enigma paths. `TerrainSnapshot.Execute` remains outside these ten phase scopes.

**YAML source lookup (Profiler 1.14):** `ProfilerRuleSources.tsv` maps the
unchanged `RuleIdentity` to `SourceFile` (relative to BepInEx/config),
1-based `RuleEntry` (script-only), 1-based `YamlItem` (full mixed YAML list),
and the original `StartLine`/`EndLine` before shorthand normalization.
`EndLine` is inclusive and includes any comments or blank lines preceding
the next top-level item. `ContentHash` is the existing SHA-256 of the
successfully parsed text snapshot, encoded in Base64. One YAML item expanded
by `types` yields multiple rows with the same physical source location.
No YAML source information is looked up during rule execution.

The full `ProfilerRuleSources.tsv` is stored **outside prunable segment directories**:

`<expand_world base directory>/logs/EwpProfilerSources/YYYYMMDD_HHMMSS_fff_sources_<revision>/ProfilerRuleSources.tsv`

The directory uses the Europe/Berlin timestamp, as do regular profiler segments.
Every segment writes only a small `ProfilerSourceCatalogs.tsv` with
`FirstObservedSeconds`, `SourceRevision`, `RuleCount` and `CatalogPath`.
`CatalogPath` is relative to `<expand_world base directory>/logs/`.
An unchanged loaded source revision references exactly the same directory on
every subsequent segment. On the first new revision (including after a server
restart), the profiler compares its complete canonical TSV rows with **only
the most recent** timestamped source catalog directory. If identical, it
reuses that existing file across runs, without creating a duplicate.
Otherwise it creates a new timestamped catalog. It deliberately does not
search older catalogs: an A → B → A sequence creates three directories.
If the newest catalog file is missing or unreadable, a new catalog is
created instead of referencing an older one. Comparison occurs only when
the in-memory revision cache misses at report write, never on the rule
hot path. Deleting old `EwpProfiler` segment directories does not delete
the sibling `EwpProfilerSources` directory.

If successfully loaded prefab YAML changes, a new versioned source
catalog is created at the next report write. A segment that spans a
successful reload references both source versions, with their first
observed offsets. `ProfilerSummary.txt` reports the number of
rule-source changes. Existing metric columns and identities are unchanged:
measurements sharing a `RuleIdentity` may include work from both versions,
and must not be interpreted as version-separated.

Each segment is written when it ends: on the interval, on the next `LateUpdate` after world-save initiation, on first-join phase transition, by manual command, or on `ZNet.Shutdown`/plugin destruction. Reports are *never* written for every event. TSVs use UTF-8 (no BOM); tabs and newlines in text fields are sanitized. Interleaved server/client logs are not needed.

## Interpretation & limits

- All timings are **Stopwatch monotonic elapsed wall time**, not true CPU cycles or thread-CPU time. A blocked thread will appear slow. A profiler capture itself adds overhead; compare similar captures, not raw timings against an uninstrumented build.
- Inclusive includes instrumented children. Exclusive is inclusive minus direct instrumented child duration. Summing all inclusive totals double counts; sum exclusive for non-overlapping measured frames/chain attribution.
- Candidate counts are **evaluations/visits**, not guaranteed unique ZDO IDs. `HasAllObjects` can examine a ZDO multiple times for multiple object filters, and early-out predicates may never examine the full collection. `GetNearby` counts predicate visits as its actual LINQ query runs; it does **not** enumerate the collection again. `GetCounts` reports unique prefab names returned, rather than target IDs.
- `RuleContext` includes a full file path. Search phases during global selection that have no specific candidate rule may show a generic event context. Calls not executed as a selected rule still appear under filter-condition keys where available.
- Chain provenance is transient: scheduled in-memory pokes inherit parent trace/span; after a save/reload, old pending timers have **no persistent parent ID**. Trace IDs are unique only within a process, not across server restarts or profiler runs. Timer entries linked in a previous profiler capture also become unattributed in a new capture. Independent rules in one event now receive separate trace IDs; nested rule calls inherit the original causal trace.
- Detailed slow scopes are bounded to 3,000 records; chain aggregates are bounded to 50,000 IDs (drops reported in summary). If the limit is reached, traces can be incomplete. A scope is not guaranteed to have a logged parent unless the parent also exceeded the detailed-event threshold.
- GC samples are whole-process, not reliable per-rule allocations. The allocation feature does not call unavailable newer .NET APIs.
- Only instrumented EWP paths are covered. Other Unity engine and mod CPU activity is out of scope. Some non-search EWP phases remain uninstrumented; results are a prioritized first-pass profiler, **not an exhaustive CPU accounting**.
- Script `Type` and parameter information is recorded as parsed by upstream EWP. YAML entries that contain *only data* are not runtime rule entries; index refers to `Yaml.ReadMixedFile().ScriptEntries`, not a physical YAML text line number.

## Recommended test

1. Compare test server's existing EWP DLL with the public source behavior; back up the world and mods first.
2. Build the diagnostic DLL with current server/game assemblies, deploy on **test server only**.
3. Set `EnableProfiler = true`, `CaptureOnPlayerJoin = true`, `CaptureSeconds = 180`, `EnableDetailedEvents = false` for a lower-overhead baseline.
4. Join, idle for 20s, explore for 30s, approach player base/refuge, test map dot/encounter activity, idle again.
5. Send all nine output files for comparison against **MK CLXX**, not pre-CLXX trace readings.

No caches, filter-order modifications, delay changes, target reshuffling, ZDO writes, revision handling, or YAML behaviors were introduced intentionally. Build verification and behavioral validation on the actual Valheim 1.0 test server are still required.

## Direct vs. caused costs

- `ProfilerDirectRules.tsv`: exclusive *instrumented slices* attributed to the rule actually responsible for that work, including candidate-rule filter evaluations. Does not count all uninstrumented engine work.
- `ProfilerCausedRules.tsv`: traces that start from an executed YAML rule, aggregated by origin. Deferred follow-up rules remain charged to the original rule for **causal** attribution. Unknown/restored/pre-capture timers create stand-alone traces with empty origin; they are deliberately excluded from per-origin sums.
- A trace's `InstrumentedExclusiveMs` is the sum of disjoint measured slices and may include a small amount of EWP event/dispatch overhead attributed only to the cause. Do **not** sum inclusive times across scopes to obtain a CPU total.
- Search timing and candidate metrics in `ProfilerTimeline.tsv` use the completion-second bucket (not distributed across running seconds). `SearchWallMs` is a subset of measured scope work, not additive to `RootWallMs`.
- `EnableDetailedEvents=false` still captures slow scopes above 25 ms (max 3,000 in-memory records); detail mode lowers the stored-event threshold to 1/4 for a focused second run. There is no per-call disk logging.
- Stopping a startup capture and writing reports at player-join introduces a short diagnostic I/O interruption. Interpret the first gameplay-second bucket with that in mind. For a completely uninterrupted baseline, capture startup and gameplay in separate test runs.

Startup-specific `startup-awake`, `yaml-reload` and `yaml-rebuild` scopes help identify whether the initialization burden has moved into the rule-loading stage. These measurements do not cover all Valheim world-generation code outside EWP.

The added `ProfilerAccountingTests.cs` includes a synthetic 4-frame nested exclusivity test (sum of exclusive parts equals root inclusive), negative-time guarding, and a `using`/exception unwinding check. They have **not** been executed here because this runtime lacks `dotnet` and Valheim DLL references.

## Continuous mode and save checkpoints (v1.4)

For a continuously running backed-up diagnostic server:

```ini
[EWP Profiler]
EnableProfiler = true
CaptureOnServerStart = true
CaptureStartupSeconds = 180
CaptureOnPlayerJoin = true
CaptureSeconds = 180
EnableContinuousProfiling = true
ReportOnWorldSave = true
EnableDetailedEvents = false
```

Reports are **rotating segments**, not a cumulative lifetime log: each segment begins with empty counters and a new capture generation, so tables do not grow indefinitely. `ProfilerSummary.txt` contains `Report reason` (`interval`, `world-save`, `server-shutdown`, etc.). A delayed poke scheduled before a rotation may execute after it; the old trace ID is deliberately *not* inherited in the new segment. Causal chains can therefore span segment boundaries but will not be falsely joined.

The `ZNet.SaveWorld` postfix runs after the synchronous save initiation, not necessarily after Valheim's asynchronous writer completes. It only sets an atomic flag. EWP checks it at the start of a subsequent `LateUpdate` and writes the nine report files there. This separates profiling I/O from Valheim's save code, but writing multiple MB of TSV files can still briefly occupy the Unity main thread; run on a test server and monitor spikes. The 180-second rotation ensures progress reports even if the save hook is absent or no world save happens.

A shutdown `ZNet.Shutdown` prefix flushes the last incomplete segment before queues are cleared. `EWP.OnDestroy` is an additional fallback, not a guarantee against crashes or hard process termination. Console output is intentionally short: a continuous rotation emits **one**
line, e.g. `Profiler: new continuous-gameplay segment #20261123_163000_466 (interval)`
or `Profiler: new continuous-gameplay segment #20261123_163000_466 (world-save)`.
The displayed timestamp belongs to the **new** batch and the parenthesized
reason belongs to the **completed** previous batch. Initial standalone
starts and final stops remain separately logged; report write failures
still produce a warning. Manual snapshots and first-player transitions
also use a single new-segment message. The batch identifier is the Europe/Berlin timestamp prefix of the full report directory, which remains in `ProfilerSummary.txt`. Write failures use the same batch identifier and include the exception type and message without repeating the absolute output directory. BepInEx retains its upstream metadata (`Loading [Expand World Prefabs 1.67]`), preserving dependency-version compatibility. `EwpProfiler.Initialize()` writes `Profiler 1.16 initialized` once during plugin startup instead. If the server uses UTC (as in DatHost), the summaries show server-local UTC and an explicit German local time field.

`ewp_profiler_snapshot` is available to force a report without stopping continuous measurement. `ewp_profiler_stop` ends the current segment; when `CaptureOnPlayerJoin=true`, any later real join may start a new segment if none is running. This does not change active continuous segments.

## Join/leave markers in continuous captures (v1.6)

Each actual player connection accepted by `PeerManager.HandlePlayerCreatedState` produces a lightweight `join` marker **before** EWP executes the `State/join` rules. The corresponding `ZNet.Disconnect` hook records a `leave` marker (if the connection was tracked), before `State/leave` processing. Respawns handled by the same peer are not counted as new joins; reconnects generate a new session-local join ordinal.

**No active gameplay segment is rotated on player join.** The first join still ends the separate startup segment, if one exists and `CaptureOnPlayerJoin=true`, and starts a gameplay segment; later joins just add marker rows. Markers therefore leave the continuous metrics and delayed-poke trace ancestry untouched. Reports remain rotated by time, world save and shutdown, exactly as before.

Join ordinals increase during one server process, even across rotated report segments. `ProfilerPlayerEvents.tsv` has precise marker timestamps; `ProfilerTimeline.tsv` adds `PlayerJoins`/`PlayerLeaves` to the 1-second rows. To compare join effects, combine consecutive segments when a join occurs near an interval or world-save boundary. An event marker does not assert that all nearby EWP work was *caused* by that connection; it only establishes temporal correlation. A disconnect marker can be absent if Valheim has terminated the process or called a different disconnect route before the hook can observe it.


## Spawn diagnostics

EWP-side ZDO creation tracing captures source rules, prefab identities, creation
phases and timestamps. It does not capture native spawns initiated outside EWP
or client-side rendering.

`EnableSpawnProfiling = true` records aggregate spawn counts while a profiler
capture is running. `RecordAllSpawnEvents = false` retains individual failed
attempts only; set it to `true` to retain individual events for every EWP-created
spawn. Events remain buffered until normal report rotation, world save, manual
snapshot or shutdown. No per-spawn disk logging occurs. The individual-event
buffer holds up to 6,000 rows per segment and reports dropped events.

Output files in each segment:

- `ProfilerSpawns.tsv`: UTC/German time, stage, prefab, ZDO ID, source ZDO ID
  (when known), current rule, chain origin, trace/span IDs, planned delay,
  position, data revision and owner.
- `ProfilerSpawnSummary.tsv`: total stages grouped by prefab, stage and rule.
- `ProfilerTimeline.tsv`: per-second `SpawnCreated` counter.

Stages: `scheduled` / `due` track delay timing; `immediate` denotes direct
execution; `zdo-created` is **before** rule-supplied data has been written;
`data-written` is immediately **after** this write; `created-ready` follows
creation and initialization; `delayed-completed` follows delayed creation;
`create-exception-outcome-unknown` warns that the native create might already
have succeeded before an exception was raised. These stages are **not** visual
client-rendering events. Failed or missing prefabs can have no ZDO ID.

A delayed spawn retains a transient trace token only if it was scheduled
inside the current capture generation. On capture rotation the token expires;
unknown origin is left blank rather than invented. The delay queue, save data,
ZDO writes, and gameplay behavior are unchanged.

## Recording and path observations

The former bounded path probe, its console commands, diagnostic lifecycle and dedicated rule-log reserve have been removed. General phase timings now belong to EwpProfiler and use the normal capture lifecycle, independent of Enigma mode and rule logging. The rule logger uses upstream behavior.

ValheimLogExporter EWP Recording can complement these reports with timestamped rule admissions and source mapping from observed reloads. It does not record resolved actor/route/row/attempt identity or prove action completion. There is no replacement row-acceptance journal, resolved-poke-delay trace or Enigma receipt validation in this branch. Spawn profiling continues to record prefab, source rule and creation phases through EwpProfiler. Existing report columns remain unchanged; ProfilerPhases.tsv is additive. The added outer tick scopes increase root timeline coverage and reduce duplicate nested root calls for those phases, so older RootWallMs/RootCalls values are not directly comparable.

## Additional profiling of slow roots and server updates

The profiler now records `saved-data-detail` scopes for the rare actual data
file save (directory, serialization, backup and write). `ProfilerSummary.txt`
reports idle and cooldown calls, attempts, successful writes, database keys
and UTF-8 output size. These counters are not a claim that every invocation
performed disk I/O.

In this private diagnostic branch, `RuleDetailFilter = *` is the default
and enables `rule-detail` scopes for **all rules**, including condition,
saved/banned-key tests, chance, queued exec/commands and local data-read.
The filter is precomputed on YAML rebuild. Set a case-insensitive substring
of `RuleIdentity` to limit work, or empty to disable. Changing the filter
requires a YAML rebuild/restart. Existing rule identities and metrics columns
remain unchanged.

`ProfilerSlowRoots.tsv` is **enabled by default** on this internal branch,
with `SlowRootThresholdMs = 100`. A bounded per-thread buffer keeps up to
128 instrumented child spans per root. If the root is longer than the
threshold, the buffer is exported; otherwise it is reused. Once full,
later expensive child spans may replace cheaper leaf spans, while required
synchronous ancestors remain in the tree. Exactly 128 observed spans is
not a truncation. The TSV now separates the actual synchronous
`ParentSpanID` from the deferred `CausalParentSpanID`; previous
`ProfilerSlowEvents.tsv` ancestry and existing causal accounting are
unchanged.

A segment keeps the **120 slowest roots**, not the first 120.
`ProfilerSlowRootWork.tsv` accompanies the bounded tree with an exact
per-root account of **all instrumented child spans**, including children not
retained in the 128-span tree. Rows marked `root-self` show exclusive work
not spent in instrumented children; `kind-total` rows group every child
by scope kind (an exact non-overlapping cost partition). Their
`TotalExclusiveMs` values plus the `root-self` row account for the
root's inclusive time within measurement precision. `detail` rows provide
the kind, operation identity, rule context, count, total exclusive time,
maximum individual inclusive time and its `MaxSpanID`. Detail keys are
bounded to 256 identities per live root; further identities contribute
to labeled `detail-overflow` rows grouped by kind. Kind totals remain
exact. Detail rows and kind totals are **alternative breakdowns**, not
values to sum together. Inclusive totals are nonadditive because child
scopes can be nested. Dynamic identity grouping may have more overhead
than the fixed tree alone.

`ProfilerSlowEvents.tsv` now keeps the **3,000 slowest qualifying events**
per segment rather than the first 3,000. A bounded min-heap avoids an
unbounded event list. The new `ProfilerSlowEventSummary.tsv` counts
**all threshold-qualified slow events**, whether or not their individual
rows survived retention. It has exact `kind-total` rows and up to 1,024
`detail` keys, then exact per-kind `detail-overflow` counts and costs.
The existing `ProfilerRules.tsv` continues to aggregate *all* profiled
scopes, including those faster than the slow-event threshold.
The original `ProfilerSlowEvents.tsv` columns are unchanged.
`ProfilerSummary.txt` reports recording enabled/disabled, threshold,
qualified roots, retained roots, excluded roots, retained truncated roots,
and omitted child spans in retained roots. The root rows add
`ObservedChildSpans` and `OmittedChildSpans`; `Truncated` means
at least one instrumented child span was not retained. Child rows retain
their real call-parent IDs. Slow-root data covers only instrumented work
within a synchronous root; deferred follow-up work in later ticks
remains a distinct root.

The private branch also defaults to `EnableProfiler = true`,
`EnableDetailedEvents = true`, `EnableAllocationDiagnostics = true`,
and `RecordAllSpawnEvents = true` for **new configurations**.
There is no configuration migration or automatic override: an existing
`CaptureSlowRootTrees = false` or any other explicitly saved setting
remains in effect. The obsolete `InternalDiagnosticDefaultsV115`
entry, if present in older config files, is no longer read or written
by the profiler.
Slow-event and spawn-event outputs remain capped; their drop counts
may rise in long or busy segments.
These defaults can increase CPU cost, allocation pressure and log size;
compare with reduced-diagnostics runs before using measured timings
to conclude that a gameplay path is expensive.

`ProfilerRuntime.tsv` reports per-second EWP `LateUpdate` wall time
(sum/average/max and histogram bucket P95/P99), maximum interval between
EWP update starts, periodic GC collection deltas, connected peer count,
outstanding poke timers, and dispatch latency for newly created/restored
poke timers. These are EWP update measurements, not whole Unity frame
times, CPU time or exact GC pause durations. Timer lateness is measured
in game-time seconds at dispatch, and may be affected by changes in the
game clock. Some buckets can contain poke dispatches but no completed
EWP update when profiling stops during a tick.

Use `ewp_profiler_mark <label>` on the server to record a timestamped
manual test marker. Up to 100 markers per segment are written to
`ProfilerMarkers.tsv` with UTC and German time. Marks do not modify
gameplay or segment boundaries.

Profiler report label 1.13 adds saved-data/rule details, slow roots, runtime and markers without changing the existing 1.12 rule-source mapping.
