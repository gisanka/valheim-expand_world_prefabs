using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using Service;

namespace ExpandWorld.Prefab;

// EWP-only, opt-in wall-clock profiler. Only the world-save/shutdown triggers use Harmony; no per-call logging.
// All game state is read-only. Aggregate locks also cover calls from other threads.
internal static class EwpProfiler
{
  private static ConfigEntry<bool> Enable = null!;
  private static ConfigEntry<bool> OnJoin = null!;
  private static ConfigEntry<bool> OnStartup = null!;
  private static ConfigEntry<int> StartupDuration = null!;
  private static ConfigEntry<int> Duration = null!;
  private static ConfigEntry<bool> Continuous = null!;
  private static ConfigEntry<bool> OnWorldSave = null!;
  private static ConfigEntry<bool> Detailed = null!;
  private static ConfigEntry<float> SlowMs = null!;
  private static ConfigEntry<int> TopN = null!;
  private static ConfigEntry<bool> Searches = null!;
  private static ConfigEntry<bool> Chains = null!;
  private static ConfigEntry<bool> Allocations = null!;
  private static ConfigEntry<bool> SpawnProfiling = null!;
  private static ConfigEntry<bool> AllSpawnEvents = null!;
  private static ConfigEntry<string> RuleDetailFilter = null!;
  private static ConfigEntry<bool> CaptureSlowRootTrees = null!;
  private static ConfigEntry<float> SlowRootThreshold = null!;
  private static readonly object Sync = new();
  private static readonly Dictionary<string, Totals> Stats = new(StringComparer.Ordinal);
  private static readonly Dictionary<string, DirectRuleTotals> DirectStats = new(StringComparer.Ordinal);
  private static readonly Dictionary<string, SearchTotals> SearchStats = new(StringComparer.Ordinal);
  private static readonly Dictionary<long, ChainTotals> ChainStats = new();
  private static readonly SortedDictionary<long, Timeline> Seconds = new();
  private static readonly SortedDictionary<long, RuntimeRow> RuntimeSeconds = new();
  private static readonly List<SlowRootTrace> SlowRoots = new();
  private static readonly List<ProfilerMarker> Markers = new();
  private const int MaxMarkers = 100;
  private static int DroppedMarkers;
  private const int MaxSlowRoots = 120;
  private const int MaxRootSpans = 128;
  private static int DroppedSlowRoots;
  private static long LastUpdateStart;
  private static long LastGcSecond;
  private static int PreviousGc0, PreviousGc1, PreviousGc2;
  [ThreadStatic] private static List<RootSpan>? RootScratch;
  private static readonly List<SlowEvent> Slow = new();
  // Connection lifecycle markers are kept only for the current capture segment.
  private static readonly List<PlayerMarker> PlayerMarkers = new();
  // Updated only when EWP successfully rebuilds YAML rules, never on the rule hot path.
  private static readonly Dictionary<string, RuleSource> LoadedRuleSources = new(StringComparer.Ordinal);
  // Each successfully loaded source snapshot is assigned a process-local revision.
  // Reports reference immutable, separately stored catalogs instead of duplicating
  // tens of thousands of rule-to-YAML mappings at every report rotation.
  private static long SourceRevision;
  private static readonly List<RuleSourceSnapshot> SegmentSourceSnapshots = new();
  private static readonly Dictionary<string, string> SourceCatalogFiles = new(StringComparer.Ordinal);
  private static int RuleSourceReloads;
  // Test fixture isolation. Production catalogs live beside EwpProfiler segments.
  internal static string? SourceCatalogRootOverride;
  private static readonly Dictionary<string, SpawnTotals> SpawnStats = new(StringComparer.Ordinal);
  private static readonly List<SpawnEvent> SpawnEvents = new();
  private const int MaxSpawnEvents = 6000;
  private static int DroppedSpawnEvents;
  private static int SpawnProfilerErrors;
  // Session-local connection sequence persists across report rotation (not world state).
  private static readonly Dictionary<string, int> ConnectedPlayerJoins = new(StringComparer.Ordinal);
  private static int NextJoinOrdinal;
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
  private static long NextId;
  private static int CaptureGeneration;
  private static int PendingWorldSave;
  private static bool AnyPlayerJoined;
  private static string EndReason = "";
  private static bool StartupCaptureUsed;
  private static int ScopeOrderErrors;
  private static long CaptureStart;
  private static long CaptureStop;
  private static DateTime StartUtc;
  private static string StartReason = "";
  private static string OutputDirectory = "";
  private static int DroppedChains;
  private static int DroppedSlow;
  private static long SavedDataIdleChecks, SavedDataCooldownChecks, SavedDataAttempts, SavedDataSuccesses;
  private static long SavedDataLastEntries, SavedDataLastBytes, SavedDataTotalBytes;
  private const int MaxChains = 50000;
  private const int MaxSlow = 3000;
  private static volatile bool Running;
  [ThreadStatic] private static Frame? Current;

  internal static bool Active => Running;
  internal static bool ShouldCaptureJoin => Enable != null && Enable.Value && OnJoin.Value;
  internal static bool ShouldCaptureStartup => Enable != null && Enable.Value && OnStartup.Value;
  internal static bool ShouldContinuous => Enable != null && Enable.Value && Continuous.Value;
  internal static bool SearchActive => Running && Searches.Value;
  internal static bool ChainActive => Running && Chains.Value;
  internal static bool SpawnActive => Running && SpawnProfiling != null && SpawnProfiling.Value;
  internal static bool ShouldProfileRuleDetails(string key) =>
    RuleDetailFilter != null && !string.IsNullOrWhiteSpace(RuleDetailFilter.Value) &&
    key.IndexOf(RuleDetailFilter.Value.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
  internal static Scope BeginRuleDetail(Rule rule, string step) =>
    Running && rule.ProfileDetails ? Begin("rule-detail", step, rule.ProfilerKey) : default;

  // No-op return paths are counted without taking the profiler aggregate lock.
  internal static void SavedDataSkipped(bool cooldown)
  {
    if (!Running) return;
    if (cooldown) Interlocked.Increment(ref SavedDataCooldownChecks);
    else Interlocked.Increment(ref SavedDataIdleChecks);
  }
  internal static void SavedDataWriteStarted(int entries)
  {
    if (!Running) return;
    Interlocked.Increment(ref SavedDataAttempts);
    Interlocked.Exchange(ref SavedDataLastEntries, entries);
  }
  internal static void SavedDataWriteCompleted(int bytes)
  {
    if (!Running) return;
    Interlocked.Increment(ref SavedDataSuccesses);
    Interlocked.Exchange(ref SavedDataLastBytes, bytes);
    Interlocked.Add(ref SavedDataTotalBytes, bytes);
  }

  internal sealed class RuleSource(
    string ruleIdentity, string sourceFile, int ruleEntry, int yamlItem,
    int startLine, int endLine, string contentHash)
  {
    internal string RuleIdentity { get; } = ruleIdentity;
    internal string SourceFile { get; } = sourceFile;
    internal int RuleEntry { get; } = ruleEntry;
    internal int YamlItem { get; } = yamlItem;
    internal int StartLine { get; } = startLine;
    internal int EndLine { get; } = endLine;
    internal string ContentHash { get; } = contentHash;
    internal string VersionKey => RuleIdentity + "\u001f" + ContentHash + "\u001f" +
      YamlItem + "\u001f" + StartLine + "\u001f" + EndLine + "\u001f" + SourceFile;
  }

  private sealed class RuleSourceSnapshot(long revision, double firstObservedSeconds, RuleSource[] sources)
  {
    internal long Revision { get; } = revision;
    internal double FirstObservedSeconds { get; } = firstObservedSeconds;
    internal RuleSource[] Sources { get; } = sources;
  }

  internal static void SetLoadedRuleSources(IEnumerable<RuleSource> sources)
  {
    var next = new Dictionary<string, RuleSource>(StringComparer.Ordinal);
    foreach (var source in sources) next[source.RuleIdentity] = source;
    lock (Sync)
    {
      bool changed = LoadedRuleSources.Count != next.Count ||
        LoadedRuleSources.Any(pair => !next.TryGetValue(pair.Key, out var value) ||
          pair.Value.VersionKey != value.VersionKey);
      if (!changed) return;
      if (Running && LoadedRuleSources.Count > 0) RuleSourceReloads++;
      SourceRevision++;
      LoadedRuleSources.Clear();
      foreach (var pair in next) LoadedRuleSources.Add(pair.Key, pair.Value);
      // A source change is captured once, not on every rule execution.
      // A segment crossing a reload can therefore reference both versions.
      if (Running && next.Count > 0)
      {
        var elapsed = Math.Max(0, (Stopwatch.GetTimestamp() - CaptureStart) / (double)Stopwatch.Frequency);
        SegmentSourceSnapshots.Add(new RuleSourceSnapshot(SourceRevision, elapsed, next.Values.ToArray()));
      }
    }
  }

  internal static void Initialize(ConfigFile config)
  {
    const string section = "EWP Profiler";
    Enable = config.Bind(section, "EnableProfiler", false, "Enable automatic captures and optional continuous rotation. Manual start works regardless.");
    OnJoin = config.Bind(section, "CaptureOnPlayerJoin", true, "Start a gameplay capture when none is running; end startup phase at first join. While continuously profiling, all joins are marked without restarting the ongoing segment.");
    OnStartup = config.Bind(section, "CaptureOnServerStart", true, "Separate server startup capture; starts in plugin Awake on a headless dedicated server, or at first server detection.");
    StartupDuration = config.Bind(section, "CaptureStartupSeconds", 180, "Maximum startup capture duration; an earlier player join ends this phase if join capture is enabled.");
    Duration = config.Bind(section, "CaptureSeconds", 180, "Gameplay/continuous report interval in seconds. With continuous profiling disabled, capture ends after this interval.");
    Continuous = config.Bind(section, "EnableContinuousProfiling", true, "While EnableProfiler is true, rotate reports every CaptureSeconds and continue profiling until shutdown; startup remains a separate phase.");
    OnWorldSave = config.Bind(section, "ReportOnWorldSave", true, "Also rotate and write a report after ZNet.SaveWorld initiates a world save; writing is deferred to EWP LateUpdate.");
    Detailed = config.Bind(section, "EnableDetailedEvents", false, "Include more individual trace events (threshold / 4); slow calls over SlowCallThresholdMs are ALWAYS recorded in a bounded list.");
    SlowMs = config.Bind(section, "SlowCallThresholdMs", 25f, "Minimum inclusive wall-clock duration of a detailed event.");
    TopN = config.Bind(section, "TopN", 30, "Number of rows shown in text summary; TSVs contain all aggregated groups.");
    Searches = config.Bind(section, "EnableZdoSearchProfiling", true, "Count ZDO visits and filter passes in GetNearby/HasNearby/HasNotNearby/GetCounts.");
    Chains = config.Bind(section, "EnableCallChainProfiling", true, "Carry transient trace context across deferred pokes.");
    Allocations = config.Bind(section, "EnableAllocationDiagnostics", false, "Sample global GC counts and approximate heap size at capture boundaries only; NOT per-rule allocation attribution.");
    SpawnProfiling = config.Bind(section, "EnableSpawnProfiling", true, "Track EWP script-created ZDOs with source rule, trace ID and creation phases.");
    AllSpawnEvents = config.Bind(section, "RecordAllSpawnEvents", false, "Record individual events for all EWP spawns instead of only failed attempts. All EWP spawns are always aggregated.");
    RuleDetailFilter = config.Bind(section, "RuleDetailFilter", "", "Case-insensitive substring of RuleIdentity for selective condition, saved-key and action-stage timings. Empty disables additional rule scopes. Applies on YAML rebuild.");
    CaptureSlowRootTrees = config.Bind(section, "CaptureSlowRootTrees", false, "Temporarily buffer up to 128 child spans per root and retain up to 120 roots exceeding SlowRootThresholdMs per segment. Diagnostic mode only.");
    SlowRootThreshold = config.Bind(section, "SlowRootThresholdMs", 50f, "Minimum inclusive root milliseconds for a bounded synchronous span tree.");
  }

  private static int InitialGc0, InitialGc1, InitialGc2;
  private static long InitialHeap;
  private static int FinalGc0, FinalGc1, FinalGc2;
  private static long FinalHeap;
  // Must run before YAML initialization in headless mode. A non-headless server
  // can start only on first detected server tick; earlier work cannot be captured.
  internal static void ServerStarting(string trigger)
  {
    if (StartupCaptureUsed) return;
    StartupCaptureUsed = true;
    if (ShouldCaptureStartup) Start("startup-" + trigger);
    else if (ShouldContinuous) Start("continuous-" + trigger);
  }

  // Invoked once per actual connection by PeerManager, just before the State/join rules.
  // Later connections never rotate an active gameplay segment: their markers let us
  // compare the periods before and after joins without changing capture boundaries.
  internal static void PlayerJoined(string playerZdo)
  {
    int ordinal;
    lock (Sync)
    {
      ordinal = ++NextJoinOrdinal;
      ConnectedPlayerJoins[playerZdo] = ordinal;
      AnyPlayerJoined = true;
    }
    if (ShouldCaptureJoin)
    {
      if (Running && IsStartupCapture)
      {
        Stop("player-join-transition");
        Start("first-player-join");
      }
      else if (!Running)
      {
        Start(ordinal == 1 ? "first-player-join" : "player-join-" + ordinal.ToString("D4", Invariant));
      }
    }
    MarkPlayerEvent("join", ordinal, playerZdo);
  }

  // Called before State/leave processing, even if the character ZDO is already gone.
  internal static void PlayerLeft(string playerZdo)
  {
    int ordinal;
    lock (Sync)
    {
      if (!ConnectedPlayerJoins.TryGetValue(playerZdo, out ordinal)) ordinal = 0;
      ConnectedPlayerJoins.Remove(playerZdo);
    }
    MarkPlayerEvent("leave", ordinal, playerZdo);
  }

  private static void MarkPlayerEvent(string kind, int ordinal, string playerZdo)
  {
    if (!Running) return;
    var at = Stopwatch.GetTimestamp();
    var utc = DateTime.UtcNow;
    lock (Sync)
    {
      if (!Running) return;
      var sinceStart = Math.Max(0, at - CaptureStart);
      var second = sinceStart / Stopwatch.Frequency;
      PlayerMarkers.Add(new PlayerMarker(utc, sinceStart, second, kind, ordinal, playerZdo));
      if (!Seconds.TryGetValue(second, out var row)) Seconds[second] = row = new Timeline();
      if (kind == "join") row.PlayerJoins++;
      else row.PlayerLeaves++;
    }
  }

  private static bool IsStartupCapture => StartReason.StartsWith("startup-", StringComparison.Ordinal);

  internal static bool Start(string reason)
  {
    lock (Sync)
    {
      if (Running) return false;
      Stats.Clear(); DirectStats.Clear(); SearchStats.Clear(); ChainStats.Clear(); Seconds.Clear(); Slow.Clear(); PlayerMarkers.Clear();
      RuntimeSeconds.Clear(); SlowRoots.Clear(); DroppedSlowRoots = 0;
      Markers.Clear(); DroppedMarkers = 0;
      LastUpdateStart = 0; LastGcSecond = -1;
      PreviousGc0 = GC.CollectionCount(0); PreviousGc1 = GC.CollectionCount(1); PreviousGc2 = GC.CollectionCount(2);
      SpawnStats.Clear(); SpawnEvents.Clear(); DroppedSpawnEvents = 0; SpawnProfilerErrors = 0;
      SegmentSourceSnapshots.Clear();
      if (LoadedRuleSources.Count > 0)
        SegmentSourceSnapshots.Add(new RuleSourceSnapshot(SourceRevision, 0, LoadedRuleSources.Values.ToArray()));
      RuleSourceReloads = 0;
      DroppedChains = 0; DroppedSlow = 0; ScopeOrderErrors = 0;
      SavedDataIdleChecks = SavedDataCooldownChecks = SavedDataAttempts = SavedDataSuccesses = 0;
      SavedDataLastEntries = SavedDataLastBytes = SavedDataTotalBytes = 0;
      CaptureGeneration++;
      StartUtc = DateTime.UtcNow;
      StartReason = reason;
      EndReason = "";
      // The host can be configured for UTC; archive names should use German
      // civil time for straightforward comparison with local modpack logs.
      var stamp = GermanTime(StartUtc).ToString("yyyyMMdd_HHmmss_fff", Invariant);
      OutputDirectory = Path.Combine(FileLoading.BaseDirectory, "logs", "EwpProfiler", stamp + "_" + reason);
      CaptureStart = Stopwatch.GetTimestamp();
      var seconds = IsStartupCapture ? StartupDuration.Value : Duration.Value;
      CaptureStop = CaptureStart + (long)(Stopwatch.Frequency * Math.Max(1, seconds));
      Interlocked.Exchange(ref PendingWorldSave, 0);
      if (Allocations.Value)
      {
        InitialGc0 = GC.CollectionCount(0); InitialGc1 = GC.CollectionCount(1); InitialGc2 = GC.CollectionCount(2);
        InitialHeap = GC.GetTotalMemory(false);
      }
      Running = true;
    }
    Log.Info("EWP Profiler started: " + reason + ", segment up to " +
      Math.Max(1, IsStartupCapture ? StartupDuration.Value : Duration.Value) + " s; reports: " + OutputDirectory);
    return true;
  }

  // Runs only from the Harmony postfix of ZNet.SaveWorld. No report I/O on the
  // save call path, and no work on Valheim's asynchronous world writer thread.
  internal static void WorldSaveRequested()
  {
    if (Running && OnWorldSave.Value) Interlocked.Exchange(ref PendingWorldSave, 1);
  }

  internal static void Tick()
  {
    if (!Running) return;
    if (Interlocked.Exchange(ref PendingWorldSave, 0) != 0 && OnWorldSave.Value)
    {
      Rotate("world-save");
      return;
    }
    if (Stopwatch.GetTimestamp() >= CaptureStop)
    {
      if (ShouldContinuous) Rotate("interval");
      else Stop("duration");
    }
  }

  private static void Rotate(string reason)
  {
    // A report interval is a hard trace boundary. Old deferred timer tokens
    // intentionally become unattributed in the new generation.
    var nextReason = AnyPlayerJoined ? "continuous-gameplay" : "continuous-server-idle";
    // Even a failed report must not silently disable continuous profiling.
    Stop(reason);
    if (ShouldContinuous) Start(nextReason);
  }

  internal static bool Snapshot()
  {
    if (!Running) return false;
    var nextReason = AnyPlayerJoined ? "continuous-gameplay" : "continuous-server-idle";
    var written = Stop("manual-snapshot");
    Start(nextReason);
    return written;
  }

  internal static bool Stop() => Stop("manual-or-shutdown");

  internal static bool Stop(string reason)
  {
    string path;
    bool written = false;
    lock (Sync)
    {
      if (!Running) return false;
      Running = false;
      EndReason = reason;
      Interlocked.Exchange(ref PendingWorldSave, 0);
      if (Allocations.Value)
      {
        FinalGc0 = GC.CollectionCount(0); FinalGc1 = GC.CollectionCount(1); FinalGc2 = GC.CollectionCount(2);
        FinalHeap = GC.GetTotalMemory(false);
      }
      path = OutputDirectory;
      try { WriteReports(path); written = true; }
      catch (Exception e) { Log.Warning("EWP Profiler report FAILED at " + path + ": " + e); }
    }
    if (written) Log.Info("EWP Profiler report written (" + reason + "): " + path);
    return written;
  }

  private static DateTimeOffset GermanTime(DateTime utc)
  {
    // Linux dedicated servers use Europe/Berlin; Windows uses the Windows ID.
    // Fall back to the server timezone rather than assuming CET is always UTC+1.
    TimeZoneInfo timeZone;
    try { timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
    catch (TimeZoneNotFoundException)
    {
      try { timeZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
      catch (TimeZoneNotFoundException) { return new DateTimeOffset(utc.ToLocalTime()); }
    }
    var offset = timeZone.GetUtcOffset(utc);
    return new DateTimeOffset(DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified), offset);
  }

  internal readonly struct TraceToken
  {
    internal readonly long Trace;
    internal readonly long ParentSpan;
    internal readonly string Origin;
    internal readonly string Rule;
    internal readonly int Generation;
    internal TraceToken(long trace, long parent, string origin, string rule, int generation) { Trace = trace; ParentSpan = parent; Origin = origin; Rule = rule; Generation = generation; }
  }

  internal sealed class Frame
  {
    internal Frame? Parent;
    internal string Kind = "";
    internal string Name = "";
    internal string Rule = "";
    internal long Trace;
    internal long Span;
    internal long ParentSpan;
    internal string Origin = "";
    internal long Started;
    internal int Generation;
    internal long ChildTicks;
    internal bool Root;
    internal List<RootSpan>? RootSpans;
  }

  // Reused per thread; no allocation per root after its first recorded scope.
  private static List<RootSpan>? PrepareRootSpans(Frame? parent)
  {
    if (parent != null) return parent.RootSpans;
    if (CaptureSlowRootTrees == null || !CaptureSlowRootTrees.Value) return null;
    var spans = RootScratch ??= new List<RootSpan>(MaxRootSpans);
    spans.Clear();
    return spans;
  }

  internal readonly struct Scope : IDisposable
  {
    private readonly Frame? Frame;
    internal Scope(Frame? f) { Frame = f; }
    public void Dispose() { if (Frame != null) Finish(Frame); }
  }

  internal static Scope BeginEvent(ActionType type, string[] args, string prefab)
  {
    if (!Running) return default;
    var parameter = args.Length == 0 ? "" : args[0];
    return Begin("event", type + " / " + prefab + " / " + parameter);
  }

  internal static Scope BeginRule(Rule rule)
  {
    if (!Running) return default;
    return Begin("rule", rule.ProfilerKey, rule.ProfilerKey);
  }

  internal static Scope Begin(string kind, string name, string rule = "")
  {
    if (!Running) return default;
    // Never inherit a frame from a previous capture (e.g. player joins mid-event).
    var parent = Current;
    if (parent != null && parent.Generation != CaptureGeneration) parent = null;
    var id = Interlocked.Increment(ref NextId);
    var trace = Chains.Value ? (parent?.Trace ?? 0) : 0;
    var origin = parent?.Origin ?? "";
    // A non-poke event may execute multiple independent rules. Each selected
    // root rule starts its OWN causal chain; nested rules keep the parent's.
    if (Chains.Value && trace == 0 && kind == "rule")
    {
      trace = id;
      origin = rule;
    }
    var f = new Frame {
      Parent = parent, Kind = kind, Name = name, Rule = rule.Length > 0 ? rule : parent?.Rule ?? "",
      Trace = trace, Span = id, ParentSpan = parent?.Span ?? 0,
      Origin = origin, Started = Stopwatch.GetTimestamp(), Root = parent == null,
      Generation = CaptureGeneration, RootSpans = PrepareRootSpans(parent)
    };
    Current = f;
    return new Scope(f);
  }

  internal static TraceToken CaptureTrace()
  {
    if (!ChainActive || Current == null || Current.Generation != CaptureGeneration || Current.Trace == 0) return default;
    return new TraceToken(Current.Trace, Current.Span, Current.Origin, Current.Rule, CaptureGeneration);
  }

  internal static bool CanRestoreTrace(TraceToken token, int activeGeneration) => token.Trace != 0 && token.Generation == activeGeneration;

  internal static Scope BeginDeferred(TraceToken token, string name) => BeginDeferredAction(token, name, "deferred-poke");
  internal static Scope BeginDeferredSpawn(TraceToken token, string name) => BeginDeferredAction(token, name, "deferred-spawn");
  private static Scope BeginDeferredAction(TraceToken token, string name, string kind)
  {
    if (!Running) return default;
    var parent = Current;
    if (parent != null && parent.Generation != CaptureGeneration) parent = null;
    // A persisted timer, or one queued before this capture began, has no
    // attributable origin. Give it an independent trace with an empty origin.
    var linked = Chains.Value && CanRestoreTrace(token, CaptureGeneration);
    var id = Interlocked.Increment(ref NextId);
    var f = new Frame {
      Parent = parent, Kind = kind, Name = name,
      Rule = linked ? token.Rule : (parent?.Rule ?? ""), Started = Stopwatch.GetTimestamp(), Root = parent == null,
      Span = id, Generation = CaptureGeneration,
      Trace = Chains.Value ? (linked ? token.Trace : id) : 0,
      ParentSpan = linked ? token.ParentSpan : (parent?.Span ?? 0),
      Origin = linked ? token.Origin : "", RootSpans = PrepareRootSpans(parent)
    };
    Current = f;
    return new Scope(f);
  }

  // Do not log per spawn. All rows are buffered and written at segment rotation.
  // This records only EWP-created ZDOs; vanilla/native ZDO creation is not attributed.
  internal static void RecordSpawn(string phase, int prefabHash, ZDO? zdo, string sourceZdo,
    double delay, bool? triggerRules)
  {
    if (!SpawnActive) return;
    try
    {
      var frame = Current;
      var generation = CaptureGeneration;
      var obj = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
      var prefab = obj != null ? obj.name : "#" + prefabHash.ToString(Invariant);
      var utc = DateTime.UtcNow;
      var at = Stopwatch.GetTimestamp();
      var rule = frame != null && frame.Generation == generation ? frame.Rule : "";
      var origin = frame != null && frame.Generation == generation ? frame.Origin : "";
      var trace = frame != null && frame.Generation == generation ? frame.Trace : 0;
      var span = frame != null && frame.Generation == generation ? frame.Span : 0;
      var parent = frame != null && frame.Generation == generation ? frame.ParentSpan : 0;
      var uid = zdo != null ? zdo.m_uid.ToString() : "";
      var position = zdo != null ? zdo.m_position.ToString("F2") : "";
      var revision = zdo != null ? zdo.DataRevision.ToString(Invariant) : "";
      var owner = zdo != null ? zdo.GetOwner().ToString(Invariant) : "";
      lock (Sync)
      {
        if (!Running || generation != CaptureGeneration) return;
        var key = prefab + "\t" + phase + "\t" + rule;
        if (!SpawnStats.TryGetValue(key, out var totals)) SpawnStats[key] = totals = new SpawnTotals();
        totals.Count++;
        var second = Math.Max(0, (at - CaptureStart) / Stopwatch.Frequency);
        if (!Seconds.TryGetValue(second, out var timeline)) Seconds[second] = timeline = new Timeline();
        if (phase == "zdo-created") timeline.SpawnCreated++;
        if (AllSpawnEvents.Value || phase == "failed" || phase == "create-exception-outcome-unknown")
        {
          if (SpawnEvents.Count < MaxSpawnEvents)
            SpawnEvents.Add(new SpawnEvent(utc, at - CaptureStart, phase, prefab, prefabHash,
              uid, sourceZdo, rule, origin, trace, span, parent, delay, triggerRules, position, revision, owner));
          else DroppedSpawnEvents++;
        }
      }
    }
    catch (Exception)
    {
      // A diagnostic is never allowed to prevent or change a world spawn.
      Interlocked.Increment(ref SpawnProfilerErrors);
    }
  }

  internal sealed class SearchContext : IDisposable
  {
    internal readonly Scope Span;
    internal readonly string Key;
    internal readonly string Mode;
    internal readonly double Radius;
    internal long Candidates, Matched;
    internal int Returned;
    internal long Started;
    internal SearchContext(Scope span, string key, string mode, double radius)
    { Span = span; Key = key; Mode = mode; Radius = radius; Started = Stopwatch.GetTimestamp(); }
    public void Dispose()
    {
      // Count the actual enumeration, then close its scope. A segment can
      // rotate while this thread is waiting for the aggregate lock.
      if (Running && Current != null && Current.Generation == CaptureGeneration)
      {
        var now = Stopwatch.GetTimestamp();
        var ticks = now - Started;
        lock (Sync)
        {
          if (Running && Current != null && Current.Generation == CaptureGeneration)
          {
            if (!SearchStats.TryGetValue(Key, out var t)) SearchStats[Key] = t = new SearchTotals(Mode, Radius);
            t.Add(ticks, Candidates, Matched, Returned, Radius);
            var second = Math.Max(0, (now - CaptureStart) / Stopwatch.Frequency);
            if (!Seconds.TryGetValue(second, out var ts)) Seconds[second] = ts = new Timeline();
            ts.SearchCalls++; ts.SearchTicks += ticks;
            ts.Candidates += Candidates; ts.Matches += Matched;
            if (ChainActive && Current.Trace != 0)
            {
              var c = GetChain(Current.Trace, Current.Origin);
              if (c != null) { c.Searches++; c.Candidates += Candidates; c.Matches += Matched; }
            }
          }
        }
      }
      Span.Dispose();
    }
  }

  internal static SearchContext? BeginSearch(string operation, string filter, double radius, bool global)
  {
    if (!SearchActive) return null;
    var contextRule = Current != null && Current.Generation == CaptureGeneration && Current.Rule.Length > 0 ? Current.Rule : "(selection-or-external)";
    var mode = global ? "global" : "sectors";
    // Group by static filter/rule, NOT by resolved position or dynamically varying radius.
    var name = operation + " / " + mode + " / " + filter;
    var key = contextRule + "\t" + name;
    return new SearchContext(Begin("search", name), key, mode, radius);
  }

  internal static bool Inspect(SearchContext? search, bool isMatch)
  {
    if (search != null) { search.Candidates++; if (isMatch) search.Matched++; }
    return isMatch;
  }

  internal static bool SearchResult(SearchContext? search, bool result)
  {
    if (search != null) search.Returned = result ? 1 : 0;
    return result;
  }

  internal static void Inspected(SearchContext? search, bool matched)
  {
    if (search == null) return;
    search.Candidates++;
    if (matched) search.Matched++;
  }

  internal static void Queued(int count)
  {
    if (!ChainActive || Current == null || Current.Trace == 0) return;
    lock (Sync)
    {
      if (!Running || Current == null || Current.Generation != CaptureGeneration) return;
      var chain = GetChain(Current.Trace, Current.Origin);
      if (chain != null) chain.Queued += count;
    }
  }

  private static ChainTotals? GetChain(long id, string origin)
  {
    if (ChainStats.TryGetValue(id, out var stats)) return stats;
    if (ChainStats.Count >= MaxChains) { DroppedChains++; return null; }
    ChainStats[id] = stats = new ChainTotals(id, origin);
    return stats;
  }

  private static void Finish(Frame f)
  {
    var end = Stopwatch.GetTimestamp();
    // Never unwind a different frame on a mistaken/out-of-order Dispose.
    if (Current != f)
    {
      if (Running && f.Generation == CaptureGeneration) Interlocked.Increment(ref ScopeOrderErrors);
      return;
    }
    Current = f.Parent;
    if (f.Parent != null && f.Parent.Generation == f.Generation)
      f.Parent.ChildTicks += Math.Max(0, end - f.Started);
    // Captures may switch during a live event. Its old frame belongs to the
    // old capture and must NOT corrupt the new aggregates.
    if (!Running || f.Generation != CaptureGeneration) return;
    var elapsed = Math.Max(0, end - f.Started);
    var exclusive = ExclusiveTicks(elapsed, f.ChildTicks);
    lock (Sync)
    {
      // The generation may have changed while waiting for Sync.
      if (!Running || f.Generation != CaptureGeneration) return;
      var key = f.Kind + "\t" + f.Name;
      if (!Stats.TryGetValue(key, out var s)) Stats[key] = s = new Totals();
      s.Add(elapsed, exclusive);
      // Direct work is assigned to the ACTUAL rule context. Inclusive cost
      // would count nested rule calls more than once; sum exclusive slices.
      if (f.Rule.Length > 0)
      {
        if (!DirectStats.TryGetValue(f.Rule, out var direct)) DirectStats[f.Rule] = direct = new DirectRuleTotals();
        direct.WorkTicks += exclusive;
        direct.Slices++;
        if (f.Kind == "rule") { direct.Executions++; direct.RuleInclusiveTicks += elapsed; }
      }
      if (Chains.Value && f.Trace != 0)
      {
        var chain = GetChain(f.Trace, f.Origin);
        if (chain != null)
        {
          chain.Spans++;
          chain.WorkTicks += exclusive;
          if (elapsed > chain.MaxTicks) chain.MaxTicks = elapsed;
          if (f.Kind == "rule") chain.Rules++;
          if (f.Kind == "deferred-poke") chain.Deferred++;
        }
      }
      // Whole EWP update phases include their nested rule/search work. Keep
      // completion-second totals separately; never add them to that work again.
      if (f.Kind == "tick")
      {
        var second = Math.Max(0, (end - CaptureStart) / Stopwatch.Frequency);
        if (!Seconds.TryGetValue(second, out var t)) Seconds[second] = t = new Timeline();
        if (!t.Phases.TryGetValue(f.Name, out var phase)) t.Phases[f.Name] = phase = new PhaseTotals();
        phase.Count++; phase.Total += elapsed; phase.Maximum = Math.Max(phase.Maximum, elapsed);
      }
      // Only root event/tick/deferred scopes contribute to the root timeline.
      if (f.Root)
      {
        var second = Math.Max(0, (end - CaptureStart) / Stopwatch.Frequency);
        if (!Seconds.TryGetValue(second, out var t)) Seconds[second] = t = new Timeline();
        t.RootTicks += elapsed; t.RootCalls++;
        if (elapsed > t.MaxTicks) t.MaxTicks = elapsed;
      }
      // Keep child spans only while their root is running; retain them only for slow roots.
      if (f.RootSpans != null)
      {
        if (!f.Root && f.RootSpans.Count < MaxRootSpans)
          f.RootSpans.Add(new RootSpan(f.Span, f.ParentSpan, f.Started - CaptureStart,
            f.Kind, f.Name, f.Rule, elapsed, exclusive));
        if (f.Root && elapsed >= MsToTicks(SlowRootThreshold.Value))
        {
          if (SlowRoots.Count < MaxSlowRoots)
            SlowRoots.Add(new SlowRootTrace(f.Span, f.Started - CaptureStart, f.Kind, f.Name,
              f.Rule, elapsed, exclusive, f.RootSpans.Count == MaxRootSpans, f.RootSpans.ToArray()));
          else DroppedSlowRoots++;
        }
      }
      if (elapsed >= MsToTicks(Detailed.Value ? SlowMs.Value / 4d : SlowMs.Value))
      {
        if (Slow.Count < MaxSlow)
          Slow.Add(new SlowEvent(end - CaptureStart, f.Kind, f.Name, f.Rule, f.Trace, f.Span, f.ParentSpan, elapsed, exclusive));
        else DroppedSlow++;
      }
    }
  }

  // Pure accounting helper used by the unit test with synthetic nested frames.
  internal static long ExclusiveTicks(long elapsed, long completedChildTicks) => Math.Max(0, elapsed - completedChildTicks);

  private static long MsToTicks(double milliseconds) => (long)(Math.Max(0, milliseconds) * Stopwatch.Frequency / 1000d);
  private static double Ms(long ticks) => ticks * (1000d / Stopwatch.Frequency);
  private static string Num(double value) => value.ToString("0.###", Invariant);
  private static string Clean(string? value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
  private static void Write(string path, IEnumerable<string> lines) => File.WriteAllLines(Path.Combine(OutputDirectory, path), lines, new UTF8Encoding(false));

  private sealed class Totals
  {
    internal long Count, Inclusive, Exclusive, Max;
    internal readonly long[] Buckets = new long[40];
    internal void Add(long incl, long excl)
    {
      Count++; Inclusive += incl; Exclusive += excl; if (incl > Max) Max = incl;
      var micro = Math.Max(0, incl * 1000000d / Stopwatch.Frequency);
      int index = 0;
      double threshold = 2;
      while (index < Buckets.Length - 1 && micro >= threshold) { index++; threshold *= 2; }
      Buckets[index]++;
    }
    internal string Percentile(double quantile)
    {
      if (Count == 0) return "0";
      var threshold = (long)Math.Ceiling(Count * quantile);
      long seen = 0;
      for (var i = 0; i < Buckets.Length; i++)
      {
        seen += Buckets[i];
        if (seen >= threshold) return Num(Math.Pow(2, i) / 1000d);
      }
      return Num(Ms(Max));
    }
  }

  private sealed class DirectRuleTotals
  {
    internal long WorkTicks, RuleInclusiveTicks, Executions, Slices;
  }
  private sealed class SearchTotals
  {
    internal readonly string Mode;
    internal readonly double FirstRadius;
    internal double MinRadius = double.MaxValue, MaxRadius;
    internal long Count, Time, MaxTicks, Candidates, Matched, Returned, ZeroReturned, ZeroMatches;
    internal SearchTotals(string mode, double radius) { Mode = mode; FirstRadius = radius; }
    internal void Add(long ticks, long visited, long matched, long result, double radius)
    {
      Count++; Time += ticks; if (ticks > MaxTicks) MaxTicks = ticks;
      Candidates += visited; Matched += matched; Returned += result;
      if (result == 0) ZeroReturned++;
      if (matched == 0) ZeroMatches++;
      MinRadius = Math.Min(MinRadius, radius); MaxRadius = Math.Max(MaxRadius, radius);
    }
  }
  private sealed class ChainTotals
  {
    internal readonly long Id;
    internal readonly string Origin;
    internal long Spans, WorkTicks, MaxTicks, Rules, Deferred, Searches, Candidates, Matches, Queued;
    internal ChainTotals(long id, string origin) { Id = id; Origin = origin; }
  }
  private sealed class SpawnTotals { internal long Count; }
  private sealed class SpawnEvent
  {
    internal readonly DateTime Utc;
    internal readonly long At, Trace, Span, Parent;
    internal readonly string Phase, Prefab, Uid, Source, Rule, Origin, Position, Revision, Owner;
    internal readonly int PrefabHash;
    internal readonly double Delay;
    internal readonly bool? TriggerRules;
    internal SpawnEvent(DateTime utc, long at, string phase, string prefab, int prefabHash,
      string uid, string source, string rule, string origin, long trace, long span, long parent,
      double delay, bool? triggerRules, string position, string revision, string owner)
    { Utc = utc; At = at; Phase = phase; Prefab = prefab; PrefabHash = prefabHash;
      Uid = uid; Source = source; Rule = rule; Origin = origin; Trace = trace; Span = span;
      Parent = parent; Delay = delay; TriggerRules = triggerRules; Position = position; Revision = revision; Owner = owner; }
  }
  internal readonly struct RootSpan(long id, long parent, long started, string kind,
    string name, string rule, long inclusive, long exclusive)
  {
    internal long Id { get; } = id;
    internal long Parent { get; } = parent;
    internal long Started { get; } = started;
    internal string Kind { get; } = kind;
    internal string Name { get; } = name;
    internal string Rule { get; } = rule;
    internal long Inclusive { get; } = inclusive;
    internal long Exclusive { get; } = exclusive;
  }
  private sealed class SlowRootTrace(long id, long started, string kind, string name,
    string rule, long inclusive, long exclusive, bool truncated, RootSpan[] children)
  {
    internal long Id { get; } = id;
    internal long Started { get; } = started;
    internal string Kind { get; } = kind;
    internal string Name { get; } = name;
    internal string Rule { get; } = rule;
    internal long Inclusive { get; } = inclusive;
    internal long Exclusive { get; } = exclusive;
    internal bool Truncated { get; } = truncated;
    internal RootSpan[] Children { get; } = children;
  }
  private readonly struct ProfilerMarker(DateTime utc, long elapsedTicks, string label)
  {
    internal DateTime Utc { get; } = utc;
    internal long ElapsedTicks { get; } = elapsedTicks;
    internal string Label { get; } = label;
  }

  internal static bool Mark(string label)
  {
    if (!Running || string.IsNullOrWhiteSpace(label)) return false;
    var stamp = DateTime.UtcNow;
    var tick = Stopwatch.GetTimestamp();
    lock (Sync)
    {
      if (!Running) return false;
      if (Markers.Count == MaxMarkers) { DroppedMarkers++; return false; }
      Markers.Add(new ProfilerMarker(stamp, tick - CaptureStart,
        label.Trim().Substring(0, Math.Min(120, label.Trim().Length))));
      return true;
    }
  }

  private sealed class RuntimeRow
  {
    internal readonly Totals Updates = new();
    internal readonly Totals PokeDelay = new();
    internal long MaxUpdateInterval, PokeCount, RestoredPokes;
    internal long Gc0, Gc1, Gc2;
    internal int Peers = -1, QueueDepth = -1;
  }
  private static RuntimeRow RuntimeAt(long at)
  {
    var second = Math.Max(0, (at - CaptureStart) / Stopwatch.Frequency);
    if (!RuntimeSeconds.TryGetValue(second, out var row)) RuntimeSeconds[second] = row = new RuntimeRow();
    return row;
  }

  // EWP LateUpdate duration and call-start interval, not whole Unity frame time.
  internal static void RecordUpdate(long started, long ended)
  {
    if (!Running || started < CaptureStart) return;
    lock (Sync)
    {
      if (!Running || started < CaptureStart) return;
      var row = RuntimeAt(ended);
      row.Updates.Add(Math.Max(0, ended - started), 0);
      if (LastUpdateStart > 0)
        row.MaxUpdateInterval = Math.Max(row.MaxUpdateInterval, Math.Max(0, started - LastUpdateStart));
      LastUpdateStart = started;
      var second = Math.Max(0, (ended - CaptureStart) / Stopwatch.Frequency);
      if (second != LastGcSecond)
      {
        int a = GC.CollectionCount(0), b = GC.CollectionCount(1), c = GC.CollectionCount(2);
        row.Gc0 += Math.Max(0, a - PreviousGc0);
        row.Gc1 += Math.Max(0, b - PreviousGc1);
        row.Gc2 += Math.Max(0, c - PreviousGc2);
        PreviousGc0 = a; PreviousGc1 = b; PreviousGc2 = c;
        LastGcSecond = second;
        try { row.Peers = ZNet.instance != null && ZNet.instance.IsServer() ? ZNet.instance.GetPeers().Count : -1; }
        catch { row.Peers = -1; }
        row.QueueDepth = PokeTimers.PendingCount;
      }
    }
  }
  internal static void RecordPokeDispatch(double lateness, bool restored)
  {
    if (!Running) return;
    lock (Sync)
    {
      if (!Running) return;
      var row = RuntimeAt(Stopwatch.GetTimestamp());
      row.PokeCount++;
      if (restored) row.RestoredPokes++;
      row.PokeDelay.Add(MsToTicks(Math.Max(0, lateness) * 1000d), 0);
    }
  }

  private sealed class PhaseTotals { internal long Count, Total, Maximum; }
  private sealed class Timeline
  {
    internal long RootTicks, RootCalls, MaxTicks, SearchTicks, SearchCalls, Candidates, Matches, PlayerJoins, PlayerLeaves, SpawnCreated;
    internal readonly Dictionary<string, PhaseTotals> Phases = new(StringComparer.Ordinal);
  }
  private sealed class PlayerMarker
  {
    internal readonly DateTime Utc;
    internal readonly long SinceStartTicks, Second;
    internal readonly string Kind, PlayerZdo;
    internal readonly int Ordinal;
    internal PlayerMarker(DateTime utc, long sinceStartTicks, long second, string kind, int ordinal, string playerZdo)
    { Utc = utc; SinceStartTicks = sinceStartTicks; Second = second; Kind = kind; Ordinal = ordinal; PlayerZdo = playerZdo; }
  }
  private sealed class SlowEvent
  {
    internal readonly long At, Trace, Span, Parent, Total, Exclusive;
    internal readonly string Kind, Name, Rule;
    internal SlowEvent(long at, string kind, string name, string rule, long trace, long span, long parent, long total, long exclusive)
    { At = at; Kind = kind; Name = name; Rule = rule; Trace = trace; Span = span; Parent = parent; Total = total; Exclusive = exclusive; }
  }

  private static List<string> WriteSourceCatalogReferences()
  {
    const string catalogFile = "ProfilerRuleSources.tsv";
    var references = new List<string> {
      "FirstObservedSeconds\tSourceRevision\tRuleCount\tCatalogPath"
    };
    var root = SourceCatalogRootOverride ??
      Path.Combine(FileLoading.BaseDirectory, "logs", "EwpProfilerSources");
    foreach (var snapshot in SegmentSourceSnapshots)
    {
      // The path is unique to this server process and successfully loaded rule
      // revision. An unchanged revision reuses its catalog for every rotation.
      var cacheKey = root + "\u001f" + snapshot.Revision.ToString(Invariant);
      if (!SourceCatalogFiles.TryGetValue(cacheKey, out var fullFile) || !File.Exists(fullFile))
      {
        Directory.CreateDirectory(root);
        var stamp = GermanTime(DateTime.UtcNow).ToString("yyyyMMdd_HHmmss_fff", Invariant);
        var suffix = 0;
        string folder;
        do
        {
          folder = stamp + "_sources_" + snapshot.Revision.ToString("D6", Invariant) +
            (suffix == 0 ? "" : "_" + suffix.ToString("D2", Invariant));
          suffix++;
        } while (Directory.Exists(Path.Combine(root, folder)));
        var catalogDirectory = Path.Combine(root, folder);
        Directory.CreateDirectory(catalogDirectory);
        var rows = new List<string>(snapshot.Sources.Length + 1) {
          "RuleIdentity\tSourceFile\tRuleEntry\tYamlItem\tStartLine\tEndLine\tContentHash"
        };
        foreach (var source in snapshot.Sources.OrderBy(s => s.RuleIdentity, StringComparer.Ordinal)
          .ThenBy(s => s.ContentHash, StringComparer.Ordinal).ThenBy(s => s.StartLine))
          rows.Add(Clean(source.RuleIdentity) + "\t" + Clean(source.SourceFile) + "\t" +
            source.RuleEntry + "\t" + source.YamlItem + "\t" + source.StartLine + "\t" +
            source.EndLine + "\t" + Clean(source.ContentHash));
        // Only a complete file can ever be referenced. A failed write leaves
        // the temporary file unreferenced and Stop() reports the failure.
        var temporary = Path.Combine(catalogDirectory, catalogFile + ".tmp");
        fullFile = Path.Combine(catalogDirectory, catalogFile);
        try
        {
          File.WriteAllLines(temporary, rows, new UTF8Encoding(false));
          File.Move(temporary, fullFile);
        }
        finally
        {
          if (File.Exists(temporary)) File.Delete(temporary);
        }
        SourceCatalogFiles[cacheKey] = fullFile;
      }
      var directoryName = Path.GetFileName(Path.GetDirectoryName(fullFile)!);
      // Relative to expand_world/logs, not to the prunable segment directory.
      var relative = "EwpProfilerSources/" + directoryName + "/" + catalogFile;
      references.Add(Num(snapshot.FirstObservedSeconds) + "\t" +
        snapshot.Revision + "\t" + snapshot.Sources.Length + "\t" + relative);
    }
    Write("ProfilerSourceCatalogs.tsv", references);
    return references;
  }

  private static void WriteReports(string path)
  {
    Directory.CreateDirectory(path);
    var catalogReferences = WriteSourceCatalogReferences();
    var stopUtc = DateTime.UtcNow;
    var top = Math.Max(1, TopN.Value);
    var stats = Stats.ToArray();
    var directStats = DirectStats.ToArray();
    var searches = SearchStats.ToArray();
    var chains = ChainStats.Values.ToArray();
    var lines = new List<string> {
      "EWP internal profiler 1.14 — instrumented wall-clock, NOT actual thread CPU time",
      "EWP source version: " + EWP.VERSION,
      "DevAssistant export interface: contract=1, capabilities=1, thread-safe session epoch (role compatibility protocol 176 not asserted)",
      "UTC start: " + StartUtc.ToString("O", Invariant),
      "Server local start: " + StartUtc.ToLocalTime().ToString("O", Invariant),
      "Germany start: " + GermanTime(StartUtc).ToString("O", Invariant),
      "UTC stop: " + stopUtc.ToString("O", Invariant),
      "Server local stop: " + stopUtc.ToLocalTime().ToString("O", Invariant),
      "Germany stop: " + GermanTime(stopUtc).ToString("O", Invariant),
      "Report reason: " + EndReason,
      "Continuous profiling enabled: " + ShouldContinuous,
      "Write report at world save: " + OnWorldSave.Value,
      "Trigger: " + StartReason,
      "Capture configured seconds: " + (IsStartupCapture ? StartupDuration.Value : Duration.Value),
      "Actual capture seconds: " + Num((Stopwatch.GetTimestamp() - CaptureStart) / (double)Stopwatch.Frequency),
      "Output directory: " + path,
      "Measured scopes: " + stats.Sum(s => s.Value.Count),
      "Search groups: " + searches.Length + "; search calls: " + searches.Sum(s => s.Value.Count),
      "Saved-data: idle checks=" + SavedDataIdleChecks + "; cooldown checks=" + SavedDataCooldownChecks +
        "; write attempts=" + SavedDataAttempts + "; completed=" + SavedDataSuccesses +
        "; last database entries=" + SavedDataLastEntries + "; last UTF-8 bytes=" + SavedDataLastBytes +
        "; total written UTF-8 bytes=" + SavedDataTotalBytes,
      "Trace chains: " + chains.Length + "; dropped chain-record attempts: " + DroppedChains,
      "Slow events retained: " + Slow.Count + "; dropped: " + DroppedSlow,
      "Slow roots retained: " + SlowRoots.Count + "; dropped by segment limit: " + DroppedSlowRoots +
        "; max child spans per root: " + MaxRootSpans,
      "Manual marks retained: " + Markers.Count + "; dropped: " + DroppedMarkers,
      "Spawn groups: " + SpawnStats.Count + "; retained individual events: " + SpawnEvents.Count + "; dropped: " + DroppedSpawnEvents + "; profiler errors: " + SpawnProfilerErrors,
      "Individual spawn events: failed attempts by default, or all EWP spawns when RecordAllSpawnEvents is enabled. Vanilla spawns are not attributed.",
      "Player lifecycle markers: " + PlayerMarkers.Count + " (joins: " + PlayerMarkers.Count(m => m.Kind == "join") + ", leaves: " + PlayerMarkers.Count(m => m.Kind == "leave") + ")",
      "Player marker numbers are server-session connection ordinals, not persistent character IDs; ZDO identities allow matching join/leave within the same session.",
      "Successful rule-source changes during this segment: " + RuleSourceReloads +
        (RuleSourceReloads > 0 ? "; repeated RuleIdentity values may combine measurements from different loaded YAML versions." : ""),
      "Rule source catalogs referenced: " + (catalogReferences.Count - 1) +
        "; paths in ProfilerSourceCatalogs.tsv are relative to expand_world/logs/EwpProfilerSources parent.",
      "Slow-event threshold ms: " + Num(Detailed.Value ? SlowMs.Value / 4d : SlowMs.Value) + (Detailed.Value ? " (detailed mode)" : " (baseline mode)"),
      "Out-of-order profiler scope disposals: " + ScopeOrderErrors,
      "Timing: inclusive/exclusive are for *instrumented* nested scopes; exclusive includes uninstrumented children and is NOT CPU time.",
      "Timeline: root scope wall time and all search completions per second; long calls are not split across seconds.",
      "ProfilerPhases.tsv: whole EWP update phases by completion second; inclusive wall time includes nested rule/search work, not queue waiting time. Do not add phase totals to rule/search totals.",
      "Direct rule costs: exclusive time of all instrumented slices for the actual rule (incl. selection object checks and searches).",
      "Caused costs: sum of exclusive instrumented slices across direct and deferred actions of the same trace; attribution uses only a known capture-local origin.",
      "Search candidates: evaluated candidate visits, not necessarily distinct ZDOs; limit/sorting can affect iteration.",
      "Search matched: filter passes before limits; predicates return boolean 0/1 rather than object count. ZeroMatches is comparable across all search APIs.",
      "Delayed trace parentage is in-memory only. Timers restored after world reload have no originating trace.",
      "GC/allocation metrics are whole-process approximations; no per-rule allocation attribution.",
      "",
      "Top instrumented rule scopes by inclusive total wall-clock ms:"
    };
    foreach (var kv in stats.Where(x => x.Key.StartsWith("rule\t", StringComparison.Ordinal)).OrderByDescending(x => x.Value.Inclusive).Take(top))
      lines.Add(Num(Ms(kv.Value.Inclusive)) + " ms / " + kv.Value.Count + " calls / max " + Num(Ms(kv.Value.Max)) + " ms : " + kv.Key.Substring(5));
    lines.Add(""); lines.Add("Top direct rule work (exclusive sum across rule-context scopes):");
    foreach (var kv in directStats.OrderByDescending(x => x.Value.WorkTicks).Take(top))
      lines.Add(Num(Ms(kv.Value.WorkTicks)) + " ms / " + kv.Value.Executions + " executions / " + kv.Value.Slices + " slices: " + kv.Key);
    lines.Add(""); lines.Add("Top original rules by CAUSED work (trace spans incl. delayed descendants):");
    foreach (var g in chains.Where(c => c.Origin.Length > 0).GroupBy(c => c.Origin)
        .Select(g => new { Origin = g.Key, Time = g.Sum(c => c.WorkTicks), Chains = g.Count() })
        .OrderByDescending(g => g.Time).Take(top))
      lines.Add(Num(Ms(g.Time)) + " ms / " + g.Chains + " chains: " + g.Origin);
    lines.Add(""); lines.Add("Top ZDO searches by wall-clock ms:");
    foreach (var kv in searches.OrderByDescending(x => x.Value.Time).Take(top))
      lines.Add(Num(Ms(kv.Value.Time)) + " ms / " + kv.Value.Count + " calls / " + kv.Value.Candidates + " candidate visits / " + kv.Value.Returned + " results: " + kv.Key.Replace('\t', ' '));
    lines.Add(""); lines.Add("Top ZDO searches by candidate visits:");
    foreach (var kv in searches.OrderByDescending(x => x.Value.Candidates).Take(top))
      lines.Add(kv.Value.Candidates + " visits / " + kv.Value.Count + " calls / " + Num(Ms(kv.Value.Time)) + " ms: " + kv.Key.Replace('\t', ' '));
    lines.Add(""); lines.Add("Top ZDO searches by single-call maximum time:");
    foreach (var kv in searches.OrderByDescending(x => x.Value.MaxTicks).Take(top))
      lines.Add(Num(Ms(kv.Value.MaxTicks)) + " ms max / " + kv.Value.Count + " calls: " + kv.Key.Replace('\t', ' '));
    lines.Add(""); lines.Add("Top ZDO searches by invocation count:");
    foreach (var kv in searches.OrderByDescending(x => x.Value.Count).Take(top))
      lines.Add(kv.Value.Count + " calls / " + Num(Ms(kv.Value.Time)) + " ms total: " + kv.Key.Replace('\t', ' '));
    lines.Add(""); lines.Add("Top ZDO searches by zero-filter-match count:");
    foreach (var kv in searches.OrderByDescending(x => x.Value.ZeroMatches).Take(top))
      lines.Add(kv.Value.ZeroMatches + " / " + kv.Value.Count + " zero filter passes: " + kv.Key.Replace('\t', ' '));
    lines.Add(""); lines.Add("Top timeline seconds by candidate visits per second:");
    foreach (var kv in Seconds.OrderByDescending(x => x.Value.Candidates).Take(top))
      lines.Add("second " + kv.Key + ": " + kv.Value.Candidates + " visits / " + kv.Value.SearchCalls + " searches / " + Num(Ms(kv.Value.SearchTicks)) + " ms search wall time");
    lines.Add(""); lines.Add("Top timeline seconds by root instrumented wall time:");
    foreach (var kv in Seconds.OrderByDescending(x => x.Value.RootTicks).Take(top))
      lines.Add("second " + kv.Key + ": " + Num(Ms(kv.Value.RootTicks)) + " ms, " + kv.Value.RootCalls + " root calls, peak " + Num(Ms(kv.Value.MaxTicks)) + " ms");
    lines.Add(""); lines.Add("Top rule scopes by single-call max:");
    foreach (var kv in stats.Where(x => x.Key.StartsWith("rule\t", StringComparison.Ordinal)).OrderByDescending(x => x.Value.Max).Take(top))
      lines.Add(Num(Ms(kv.Value.Max)) + " ms max / " + kv.Value.Count + " calls : " + kv.Key.Substring(5));
    lines.Add(""); lines.Add("Top rule scopes by invocation count:");
    foreach (var kv in stats.Where(x => x.Key.StartsWith("rule\t", StringComparison.Ordinal)).OrderByDescending(x => x.Value.Count).Take(top))
      lines.Add(kv.Value.Count + " calls / " + Num(Ms(kv.Value.Inclusive)) + " ms total : " + kv.Key.Substring(5));
    if (Allocations.Value)
      lines.Add("GC delta gen0/gen1/gen2: " + (FinalGc0 - InitialGc0) + "/" + (FinalGc1 - InitialGc1) + "/" + (FinalGc2 - InitialGc2) + "; heap delta (bytes): " + (FinalHeap - InitialHeap));
    else
      lines.Add("GC boundary samples: disabled");
    Write("ProfilerSummary.txt", lines);

    var general = new List<string> { "Kind\tIdentity\tCalls\tInclusiveMs\tExclusiveMs\tAverageMs\tMaxMs\tP50BucketLowerMs\tP95BucketLowerMs\tP99BucketLowerMs" };
    foreach (var kv in stats.OrderByDescending(x => x.Value.Inclusive))
    {
      var s = kv.Value; var sep = kv.Key.IndexOf('\t');
      general.Add(Clean(kv.Key.Substring(0, sep)) + "\t" + Clean(kv.Key.Substring(sep + 1)) + "\t" + s.Count + "\t" + Num(Ms(s.Inclusive)) + "\t" + Num(Ms(s.Exclusive)) + "\t" + Num(Ms(s.Inclusive) / s.Count) + "\t" + Num(Ms(s.Max)) + "\t" + s.Percentile(.5) + "\t" + s.Percentile(.95) + "\t" + s.Percentile(.99));
    }
    Write("ProfilerRules.tsv", general);
    var directRows = new List<string> { "RuleIdentity\tDirectWorkMs\tRuleExecutions\tRuleInclusiveMs\tAttributedExclusiveSlices" };
    foreach (var kv in directStats.OrderByDescending(x => x.Value.WorkTicks))
      directRows.Add(Clean(kv.Key) + "\t" + Num(Ms(kv.Value.WorkTicks)) + "\t" + kv.Value.Executions + "\t" + Num(Ms(kv.Value.RuleInclusiveTicks)) + "\t" + kv.Value.Slices);
    Write("ProfilerDirectRules.tsv", directRows);
    var causedRows = new List<string> { "OriginRule\tCausedWorkMs\tChains\tRuleExecutions\tSearchCalls\tCandidateVisits\tDeferredExecutions\tQueuedTargets" };
    foreach (var g in chains.Where(c => c.Origin.Length > 0).GroupBy(c => c.Origin)
        .Select(g => new { Origin = g.Key, Time = g.Sum(c => c.WorkTicks), Chains = g.Count(), Rules = g.Sum(c => c.Rules),
          Searches = g.Sum(c => c.Searches), Candidates = g.Sum(c => c.Candidates), Deferred = g.Sum(c => c.Deferred), Queued = g.Sum(c => c.Queued) })
        .OrderByDescending(g => g.Time))
      causedRows.Add(Clean(g.Origin) + "\t" + Num(Ms(g.Time)) + "\t" + g.Chains + "\t" + g.Rules + "\t" + g.Searches + "\t" + g.Candidates + "\t" + g.Deferred + "\t" + g.Queued);
    Write("ProfilerCausedRules.tsv", causedRows);
    var searchRows = new List<string> { "RuleContext\tSearchOperation_Mode_Filter\tMode\tCalls\tTotalMs\tMaxMs\tCandidateVisits\tFilterPasses\tReturned\tZeroResultCalls\tZeroMatchCalls\tCandidateVisitsPerReturned\tCandidateVisitsPerFilterPass\tRadiusMin\tRadiusMax" };
    foreach (var kv in searches.OrderByDescending(x => x.Value.Time))
    {
      var s = kv.Value; var sep = kv.Key.IndexOf('\t');
      var predicate = kv.Key.IndexOf("\tHas", StringComparison.Ordinal) >= 0;
      searchRows.Add(Clean(kv.Key.Substring(0, sep)) + "\t" + Clean(kv.Key.Substring(sep + 1)) + "\t" + s.Mode + "\t" + s.Count + "\t" + Num(Ms(s.Time)) + "\t" + Num(Ms(s.MaxTicks)) + "\t" + s.Candidates + "\t" + s.Matched + "\t" + s.Returned + "\t" + s.ZeroReturned + "\t" + s.ZeroMatches + "\t" + (!predicate && s.Returned > 0 ? Num(s.Candidates / (double)s.Returned) : "NA") + "\t" + (s.Matched > 0 ? Num(s.Candidates / (double)s.Matched) : "NA") + "\t" + Num(s.MinRadius) + "\t" + Num(s.MaxRadius));
    }
    Write("ProfilerSearches.tsv", searchRows);
    var chainRows = new List<string> { "TraceID\tOriginRule\tInstrumentedExclusiveMs\tSpans\tRuleExecutions\tDeferredExecutions\tSearchCalls\tCandidates\tFilterPasses\tQueuedTargets\tMaxSpanMs" };
    foreach (var c in chains.OrderByDescending(x => x.WorkTicks))
      chainRows.Add(c.Id + "\t" + Clean(c.Origin) + "\t" + Num(Ms(c.WorkTicks)) + "\t" + c.Spans + "\t" + c.Rules + "\t" + c.Deferred + "\t" + c.Searches + "\t" + c.Candidates + "\t" + c.Matches + "\t" + c.Queued + "\t" + Num(Ms(c.MaxTicks)));
    Write("ProfilerChains.tsv", chainRows);
    var slowRows = new List<string> { "AtSeconds\tKind\tIdentity\tRuleContext\tTraceID\tSpanID\tParentSpanID\tInclusiveMs\tExclusiveMs" };
    foreach (var e in Slow.OrderByDescending(x => x.Total))
      slowRows.Add(Num(e.At / (double)Stopwatch.Frequency) + "\t" + Clean(e.Kind) + "\t" + Clean(e.Name) + "\t" + Clean(e.Rule) + "\t" + e.Trace + "\t" + e.Span + "\t" + e.Parent + "\t" + Num(Ms(e.Total)) + "\t" + Num(Ms(e.Exclusive)));
    Write("ProfilerSlowEvents.tsv", slowRows);
    var roots = new List<string> { "RootSpanID\tSpanID\tParentSpanID\tStartSeconds\tKind\tIdentity\tRuleContext\tInclusiveMs\tExclusiveMs\tTruncated" };
    foreach (var root in SlowRoots)
    {
      roots.Add(root.Id + "\t" + root.Id + "\t0\t" + Num(root.Started / (double)Stopwatch.Frequency) +
        "\t" + Clean(root.Kind) + "\t" + Clean(root.Name) + "\t" + Clean(root.Rule) +
        "\t" + Num(Ms(root.Inclusive)) + "\t" + Num(Ms(root.Exclusive)) + "\t" + (root.Truncated ? "1" : "0"));
      foreach (var child in root.Children.OrderBy(x => x.Started))
        roots.Add(root.Id + "\t" + child.Id + "\t" + child.Parent + "\t" +
          Num(child.Started / (double)Stopwatch.Frequency) + "\t" + Clean(child.Kind) + "\t" +
          Clean(child.Name) + "\t" + Clean(child.Rule) + "\t" +
          Num(Ms(child.Inclusive)) + "\t" + Num(Ms(child.Exclusive)) + "\t0");
    }
    Write("ProfilerSlowRoots.tsv", roots);
    var runtimeRows = new List<string> { "SecondSinceStart\tUpdates\tEwpLateUpdateTotalMs\tEwpLateUpdateAverageMs\tEwpLateUpdateMaxMs\tEwpLateUpdateP95BucketLowerMs\tEwpLateUpdateP99BucketLowerMs\tMaxUpdateStartIntervalMs\tGC0Delta\tGC1Delta\tGC2Delta\tConnectedPeers\tPokeQueueDepth\tPokeDispatches\tRestoredPokeDispatches\tPokeLatenessMaxMs\tPokeLatenessP95BucketLowerMs" };
    foreach (var pair in RuntimeSeconds)
    {
      var row = pair.Value;
      runtimeRows.Add(pair.Key + "\t" + row.Updates.Count + "\t" + Num(Ms(row.Updates.Inclusive)) +
        "\t" + Num(row.Updates.Count == 0 ? 0 : Ms(row.Updates.Inclusive) / row.Updates.Count) +
        "\t" + Num(Ms(row.Updates.Max)) + "\t" + row.Updates.Percentile(.95) +
        "\t" + row.Updates.Percentile(.99) + "\t" + Num(Ms(row.MaxUpdateInterval)) +
        "\t" + row.Gc0 + "\t" + row.Gc1 + "\t" + row.Gc2 + "\t" +
        row.Peers + "\t" + row.QueueDepth + "\t" + row.PokeCount + "\t" +
        row.RestoredPokes + "\t" + Num(Ms(row.PokeDelay.Max)) + "\t" + row.PokeDelay.Percentile(.95));
    }
    Write("ProfilerRuntime.tsv", runtimeRows);
    var manualMarks = new List<string> { "SecondSinceStart\tUtcTime\tGermanyTime\tLabel" };
    foreach (var mark in Markers)
      manualMarks.Add(Num(mark.ElapsedTicks / (double)Stopwatch.Frequency) + "\t" +
        mark.Utc.ToString("O", Invariant) + "\t" + GermanTime(mark.Utc).ToString("O", Invariant) + "\t" + Clean(mark.Label));
    Write("ProfilerMarkers.tsv", manualMarks);
    var markerRows = new List<string> { "Event\tJoinOrdinal\tPlayerZdo\tElapsedMs\tSecondSinceStart\tUtcTime\tGermanyTime" };
    foreach (var m in PlayerMarkers)
      markerRows.Add(m.Kind + "\t" + m.Ordinal + "\t" + Clean(m.PlayerZdo) + "\t" + Num(Ms(m.SinceStartTicks)) + "\t" + m.Second + "\t" + m.Utc.ToString("O", Invariant) + "\t" + GermanTime(m.Utc).ToString("O", Invariant));
    Write("ProfilerPlayerEvents.tsv", markerRows);
    var spawnSummaryRows = new List<string> { "Prefab\tPhase\tRuleContext\tOccurrences" };
    foreach (var kv in SpawnStats.OrderByDescending(x => x.Value.Count))
      spawnSummaryRows.Add(kv.Key + "\t" + kv.Value.Count);
    Write("ProfilerSpawnSummary.tsv", spawnSummaryRows);
    var spawnRows = new List<string> { "UtcTime\tGermanyTime\tElapsedMs\tPhase\tPrefab\tPrefabHash\tZdoId\tSourceZdoId\tRuleContext\tOriginRule\tTraceId\tSpanId\tParentSpanId\tPlannedDelaySeconds\tTriggerRules\tPosition\tDataRevision\tOwner" };
    foreach (var e in SpawnEvents)
      spawnRows.Add(e.Utc.ToString("O", Invariant) + "\t" + GermanTime(e.Utc).ToString("O", Invariant) + "\t" + Num(Ms(e.At)) + "\t" +
        Clean(e.Phase) + "\t" + Clean(e.Prefab) + "\t" + e.PrefabHash + "\t" + Clean(e.Uid) + "\t" + Clean(e.Source) + "\t" +
        Clean(e.Rule) + "\t" + Clean(e.Origin) + "\t" + e.Trace + "\t" + e.Span + "\t" + e.Parent + "\t" +
        (e.Delay < 0 ? "NA" : Num(e.Delay)) + "\t" + (e.TriggerRules.HasValue ? e.TriggerRules.Value.ToString() : "NA") + "\t" + Clean(e.Position) + "\t" + Clean(e.Revision) + "\t" + Clean(e.Owner));
    Write("ProfilerSpawns.tsv", spawnRows);
    var timelineRows = new List<string> { "SecondSinceStart\tRootWallMs\tRootCalls\tMaxRootCallMs\tSearchCalls\tSearchWallMs\tCandidateVisits\tFilterPasses\tPlayerJoins\tPlayerLeaves\tSpawnCreated" };
    foreach (var kv in Seconds)
      timelineRows.Add(kv.Key + "\t" + Num(Ms(kv.Value.RootTicks)) + "\t" + kv.Value.RootCalls + "\t" + Num(Ms(kv.Value.MaxTicks)) + "\t" + kv.Value.SearchCalls + "\t" + Num(Ms(kv.Value.SearchTicks)) + "\t" + kv.Value.Candidates + "\t" + kv.Value.Matches + "\t" + kv.Value.PlayerJoins + "\t" + kv.Value.PlayerLeaves + "\t" + kv.Value.SpawnCreated);
    Write("ProfilerTimeline.tsv", timelineRows);
    var phaseRows = new List<string> { "SecondSinceStart\tPhase\tCalls\tTotalWallMs\tMaxCallMs" };
    foreach (var second in Seconds)
      foreach (var phase in second.Value.Phases.OrderBy(p => p.Key, StringComparer.Ordinal))
        phaseRows.Add(second.Key + "\t" + Clean(phase.Key) + "\t" + phase.Value.Count + "\t" + Num(Ms(phase.Value.Total)) + "\t" + Num(Ms(phase.Value.Maximum)));
    Write("ProfilerPhases.tsv", phaseRows);
  }
}
