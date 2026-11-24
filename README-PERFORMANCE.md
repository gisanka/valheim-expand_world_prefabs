# Experimental EWP startup performance candidate

**Status: source candidate, NOT compiled or runtime-validated. Both optimizations default to disabled.**

Base: `runtime-analysis` at `5287b0dd7ee121e9bcf1e33c64f89ed433fd57e6`. Changes are confined to `perf/persistent-yaml-cache`.

## Baseline evidence (2026-11-24, Valheim 1.0.21, EWP 1.67, EWD 1.81)

The supplied `StartupSummary.txt` and `StartupMethods.tsv` are from startup profiler 1.0, not the 1.1 source baseline. The branch already has the shutdown fix. Elapsed 74,484.964 ms; exclusive EWP 56,129.540 ms; exclusive EWD 14,571.268 ms; outside measured roots 3,784.156 ms. The root totals sum to the elapsed wall time. Zero accounting errors, open scopes or dropped rows. The following are nested within EWP mixed parsing:

| Stage | Calls | Inclusive ms | Exclusive ms |
|---|---:|---:|---:|
| Mixed YAML parsing | 1,192 | 46,604.588 | 640.234 |
| Mixed YAML splitting | 1,192 | 1,055.303 | 1,055.303 |
| YAML shorthand normalization | 815 | 20,091.302 | 20,091.302 |
| YAML deserialization | 1,544 | 24,707.430 | 24,707.430 |

The three child timings sum to 45,854.035 ms; the remaining 110.319 ms are other measured nested work (including deserializer setup), besides the 640.234 ms exclusive parent. The baseline also has 2,076.099 ms file read/hash, 4,738.973 ms rule construction (45,627 calls, not necessarily the number of expanded runtime rules), 526.322 ms rule indexing (47,739 calls), 949.898 ms data entry construction (110,059 calls), and 96.027 ms inclusive value group resolution. The reported 80,508 value groups and 29,551 data entries are loaded counts, not method invocation counts. GC 113, managed heap 285,896,704 -> 2,188,967,936 bytes, both process-wide.

Warm P1 theoretical upper bound: remove 46,604.588 ms from 56,129.540 ms leaving 9,524.952 ms EWP before cache overhead. P2 theoretical upper bound: remove part (not necessarily all) of 20,091.302 ms when parsing is still necessary. These savings overlap. No new performance result or cache size has been measured.

## P2: streaming normalization

The original `FilterShorthand.Normalize`, `PreParse` and tolerant deserializer are retained. With P2 enabled, the mixed script section uses `NormalizingParser` over the original `Parser`. It maintains one frame per open mapping/sequence and at most two synthetic pending events. Only mapping-key scalars can rename `filter` / `bannedFilter`; eligible scalar and alias values are wrapped. Bare empty plain untagged scalars are still converted to plain `null` to match the reference converter input. New scalar events preserve `IsKey` and source marks. Collection/anchor events are not rewritten beyond inserted sequence wrappers.

If the streaming attempt fails, rerun the complete reference path, including strict then fresh tolerant parsing and its original error diagnostic. Explicit tags and multiline plain scalars intentionally use the reference path even when otherwise valid, because the emitter's restyling has not been proven equivalent. Such fallback files are not persistently cached in streaming mode in this candidate. This is conservative; it may reduce P1 hit coverage and P2 savings. All parser-style equivalence tests are still required before default enablement. The old emitter may change event flags or style; the event-level differential tests are intentionally strict and may expose further differences.

## P1: persistent per-file cache

`FileLoading.StoreChanges` consumes the existing `ChangedFile` text snapshot and SHA-256. Lookup happens before `Yaml.ParseMixed`; write happens only after successful strict parsing and `DataField.Hoist`, and before `YamlFileCache.Store/Apply`. Each read reconstructs a new DTO graph. `YamlFileCache.Apply`, source ordering, reverse prefab precedence, runtime Rule construction, ValueGroups and DataEntries are unchanged. The source file is still authoritative and must be read and hashed on a full reload. A changed source with a stale cache never hits. Existing automatic reload rollback remains intact.

Cache directory: `BepInEx/config/expand_world_yaml_cache` (relative to the active BepInEx config path); outside the watched YAML roots. Each mode/path has its own `.ewpc` file. Header: magic, explicit format/schema/parser versions, environment identity, existing SHA-256 content fingerprint, payload SHA-256 and payload length. Identity also includes full source path and the actual lowercase stable path hash used by inline hoisting, parser mode, EWP/YamlDotNet/game/BCL module MVIDs, runtime version and current culture/number format. A new assembly build deliberately invalidates earlier cache entries. Payload is bounded (64 MiB, 1M value visits, nesting 128); unsupported/cyclic values fall back. No persisted type name, arbitrary activator type, BinaryFormatter, Unity/game object, runtime rule or evaluated function. SHA-256 detects accidental corruption; it is not authentication against a user who can rewrite the cache and its checksum.

Writes use a unique temporary file in the same directory followed by `File.Move` or `File.Replace`. Failure is a cache miss/write-error counter, not an invalid YAML replacement. No per-file forced disk flush (a disposable cache with torn data is rejected); native Mono/filesystem atomicity and true multi-process testing remain outstanding. Orphaned own cache files and temporary files older than 30 days are pruned on full reload; active paths in either parser mode and unrelated files are retained. Disable P1 or remove the cache directory while all game processes are stopped to rebuild from YAML.

### DTO and serializer review

Inspected `yaml/{Rule,Config,Exec,Object,Poke,Spawn,Terrain}.cs`, `common/data/DataYaml.cs`, `FlexibleYaml.cs`, `RuleLogData.cs`, and `Yaml.MixedFileEntries/SourceSpan`. The current typed DTO graph has strings, bool/int/float, nullable bool/float, string arrays, nested DTO arrays, typed lists, `Dictionary<string,string>` and arrays thereof. No enum fields or Unity types are present in that graph. `PokeYaml` inherits `ObjectYaml`. Defaults (including empty string, null, false, zero, ItemYaml chance 1) are encoded explicitly; nullable is not conflated with default. `StringList`, `SpawnEntries`, `ObjectEntries`, `ExecEntries` have readonly array fields; use their constructors. `DataField` includes `Name` and `Inline`. `RuleLogData/RuleLogFiles` contain `object`; the codec permits only a fixed set of primitive/list/dictionary types and falls back for other objects/comparers. It preserves shared mutable graph references through numeric IDs and rejects cyclic graphs; string identities are not preserved (strings immutable). It preserves source item/span order and the original content hash. `DataField.Hoist` is a demonstrated path-sensitive DTO mutation. Inspection of `PrefabLoading`, `DataLoading`, `ValueGroups`, `DataEntry.Load` and `ConfigManager` finds runtime conversion reading DTOs and building runtime structures; the persisted snapshot is taken before those operations regardless.

The selected **candidate**, not a proven winner, is a closed binary DTO codec with a cached, sorted field table plus explicit wrapper constructors. Runtime type creation is restricted to the inspected mutable DTO and collection types. A layout check rejects unknown non-public/readonly fields and changed special wrappers; assembly MVID invalidates previous schemas. Reflection and reference tables have a cost; measure it rather than assume it is negligible. No added runtime dependency or runtime target change (net4.8).

Alternatives: MessagePack may perform well but needs formatter/contract and Unity/Mono/dependency validation, which has not been performed. DataContractSerializer with binary XML and preserved references is a built-in comparison candidate; readonly wrappers, source spans and arbitrary flexible values are potential incompatibilities. The explicit NUnit benchmark attempts a roundtrip and reports rejection if unequal. Newtonsoft JSON/BSON needs separate contract and dependency evaluation (BSON not supplied merely by Newtonsoft.Json); JSON does not by itself preserve this graph. Normalized YAML text still requires YAML deserialization. BinaryFormatter is excluded. A final choice requires actual benchmark and roundtrip evidence on the corpus.

### Known baseline conflict (not changed here)

`ReloadAll()` clears the in-memory cache before processing; the automatic reload path retains old entries on parse errors. This pre-existing difference is not fixed as part of performance work, so the broad requirement to preserve previous valid configuration on *every* invalid full reload remains unmet. The cache never treats an older fingerprint as a hit. Decide the desired manual-reload/world-boundary rollback contract separately before release. The existing detector acknowledges changed hashes at a coarse flush boundary even if a file's parse failed; its unchanged-hash count is not a guarantee of a valid DTO hit.

## Profiling

Startup profiler report 1.3 adds `StartupCounters.tsv`, mode flags and per-method cache lookup/read-validation/encode/decode/write/prune/fingerprint stages. Counters distinguish persistent hits, misses and reasons, fallbacks, skipped serialization/parser fallback, write errors, bytes, file counts, retained in-memory entries (no detected change), and unchanged detector hashes. YAML bytes are the UTF-8 text snapshot size, not the original disk encoding size. Streaming parser elapsed ticks include underlying parser + normalization inside `MoveNext`; they are part of the measured attempt/deserialization and **must not be added to exclusive totals**. Existing YAML deserialization hooks and EWD/GC/heap reporting remain; `PreParse` still measures legacy/fallback only. No Harmony hook per event and no profiling allocation per event; timestamps on measured parser calls do add overhead. Counts cover the startup session only. OS disk cache is separate from a persistent DTO hit; this profiler does not measure OS cache hits directly.

## Build and managed validation (Windows)

From the performance branch, use the existing `build-profiler.ps1` with the actual active profile and paths. Example from `pwsh.exe` (replace the profile if necessary):

```powershell
$libs = 'C:\tmp\EWP-Performance\libs'
pwsh.exe -NoProfile -File .\build-profiler.ps1 -LibsDirectory $libs
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$libArg = $libs.TrimEnd('\') + '\'
dotnet test .\ExpandWorldPrefabs.Tests\ExpandWorldPrefabs.Tests.csproj -c Release "-p:LibsDir=$libArg" '-p:AlwaysUseLibsDir=true' '-p:EwpSkipCopyDll=true'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
```

New test fixtures: `StreamingYamlTests`, `PersistentYamlCacheTests`, augmented `YamlParsingTests`/`FileLoadingTests`. They include event/DTO comparison (including alias relationships, defaults, spans, flexible structures, nested shorthand, tags, nulls, malformed YAML), reference fallback and strictness, codec roundtrips, content/path/mode/culture/version/checksum/truncation invalidation, unavailable directory, concurrent writers in one process, cleanup, and the same StoreChanges body used in production. Some special event tests may fail where the reference emitter adjusts syntax; investigate rather than weakening the equivalence gate. Runtime rules/indices/ValueGroups and actual cross-process tests require separate execution; successful helper tests alone do not establish equivalence.

The explicit managed benchmark requires an actual copy of the YAML corpus, not the profiler report. Use a folder containing the same selected YAML inputs (no backups or unrelated mods) and run only the explicit test:

```powershell
$env:EWP_BENCHMARK_ROOT = 'C:\tmp\EWP-Performance\yaml-corpus'
dotnet test .\ExpandWorldPrefabs.Tests\ExpandWorldPrefabs.Tests.csproj -c Release "-p:LibsDir=$libArg" '-p:AlwaysUseLibsDir=true' '-p:EwpSkipCopyDll=true' --filter 'FullyQualifiedName=ExpandWorldPrefabs.Tests.YamlPerformanceBenchmarks.CompareParserAndPersistentCache' --logger 'console;verbosity=detailed'
```

This compares five repeats of baseline, P2, P1 cold/warm, both cold/warm, and one/multiple changed snapshots. Inputs are already read/hashed for this managed-host measurement; it includes cache I/O but not world loading, source file discovery, fingerprint calculation or runtime conversion. Report equivalence, coverage, allocated bytes, disk cache size and durations; serializer comparison attempts binary XML on the same codec-eligible subset. Do not compare a rejected/non-equivalent serializer's timings as a valid alternative. OS cache is not flushed. For representative decisions use the complete modpack and run actual Valheim measurements.

## Runtime acceptance and A/B matrix

Use the same Valheim/modpack/world and preserve the original build outside scanned plugin folders. Keep the candidate DLL identical for all runs. Both settings are in `[Performance]`, `Persistent YAML cache` (P1) and `Streaming YAML normalization` (P2). Start with both `false`. For each cold run delete only the candidate cache folder while all processes are stopped. Capture at least three runs per scenario with a process restart, then compare medians and ranges. Modes have separate cache files; warm runs must follow the matching cold mode. Preserve cache data for the warm/partial cases. Keep `StartupSummary`, `StartupMethods`, `StartupFiles`, `StartupCounters`, `StartupCoverage` and BepInEx logs. Confirm zero missing required hooks/accounting errors; EWD timing remains independent.

| Scenario | P1 | P2 | State |
|---|---|---|---|
| Baseline | false | false | none |
| Parser only | false | true | none |
| Cache only cold | true | false | empty |
| Cache only warm | true | false | valid |
| Both cold | true | true | empty |
| Both warm | true | true | valid |
| Both single changed | true | true | edit one YAML, unchanged rest |
| Both multiple changed | true | true | edit several YAML files |

For each scenario compare loaded Data Entry/Value Group/rule/index counts and complete source identities. Compare DTOs with the managed differential test and run targeted gameplay/regression checks on actual rules, including rule priorities and source mapping for multiple action types. Compare `RuleSource` identity fields in the gameplay profiler export. Counts alone do not prove runtime equivalence. Exercise live reload, `ewp_reload`, world switches, rejoin within process, full restart, invalid YAML and repair, deleted/renamed files, corrupted/version-mismatched cache, write-protected/unavailable cache directory, and concurrent actual Valheim processes sharing the config/cache. Ensure tolerant diagnostics are preserved. In-memory reload and OS page cache are not warm persistent-cache results. Do not enable defaults until all relevant checks pass.

## Validation record for this delivery

- PASS: Git bundle verified as complete history; base commit exactly matches the previously verified remote branch; local branch created from it; no remote writes.
- PASS (source/metadata inspection only): actual DTO fields inventoried; namespaces/imports and SDK default source inclusion reviewed; no new project/package/runtime dependency. Uploaded YamlDotNet.dll CLI metadata confirms IParser `Current` and `MoveNext`, generic IParser deserialization, relevant event classes, public Scalar six/nine-argument constructors (including key flag), SequenceStart four-argument and SequenceEnd parameterless constructors, AnchorAlias.Value, TagName/AnchorName and YamlException string constructor. YamlDotNet MVID `cba6ec40-2546-4a46-9577-18d27987fd9e`, SHA256 `9172f0758ed76963e0ede3b5ca9a1fb5b82f64c7e7a92cb3a6de39efa933a668`; game assembly MVID `7ac23c58-e834-4776-b9c1-ae8a703440f1`.
- NOT RUN: full C# compilation/type and overload resolution, actual project reference closure, existing or new NUnit tests, representative serializer/cold/warm benchmark, Harmony hook installation and Unity/Mono startup/reload/concurrency/runtime acceptance. Environment has no dotnet/Mono/pwsh.exe or complete BepInEx/Unity/reference assembly closure. Actual YAML corpus was not included. No benchmark numbers, cache size, runtime parity or speedup are claimed. Both modes remain opt-in. Full manual-reload invalid-YAML rollback requirement remains open as described above.
