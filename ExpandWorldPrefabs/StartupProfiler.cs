using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Service;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace ExpandWorld.Prefab;

// World-loading timings are independent of gameplay capture rotation and first-player joins.
internal static class StartupProfiler
{
  private const string PatchId = EWP.GUID + ".startup-profiler";
  private const int MaxFiles = 10000;
  private const int MaxRoots = 512;
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
  private static readonly Assembly EwpAssembly = typeof(EWP).Assembly;
  private static readonly Dictionary<MethodBase, Target> Targets = new();
  private static readonly List<string> Coverage = new();
  private static Harmony? Patches;
  private static Assembly? EwdAssembly;
  private static Session? Current;
  private static bool Ready;
  private static bool AccountingWarning;

  private sealed class Target(string module, string stage, MethodBase method)
  {
    internal readonly string Module = module;
    internal readonly string Stage = stage;
    internal readonly MethodBase Method = method;
  }

  private sealed class Totals
  {
    internal long Calls, Inclusive, Exclusive, Max, Exceptions;
    internal void Add(long elapsed, long children, bool failed)
    {
      Calls++;
      Inclusive += elapsed;
      Exclusive += Math.Max(0, elapsed - children);
      Max = Math.Max(Max, elapsed);
      if (failed) Exceptions++;
    }
  }

  private struct Frame(Target target, string module, long started, string? file)
  {
    internal Target Target = target;
    internal string Module = module;
    internal long Started = started;
    internal long Children = 0;
    internal string? File = file;
  }

  private sealed class Session
  {
    internal readonly DateTime Utc = DateTime.UtcNow;
    internal readonly long Started = Stopwatch.GetTimestamp();
    internal readonly int Thread = ThreadingId();
    internal readonly int[] Gc = GcCounts();
    internal readonly long Heap = GC.GetTotalMemory(false);
    internal readonly List<Frame> Stack = new(32);
    internal readonly Dictionary<(Target, string), Totals> Stats = new();
    internal readonly Dictionary<(string, string), Totals> Files = new();
    internal readonly List<(string Module, string Stage, long Started, long Elapsed)> Roots = new();
    internal long RootTicks, DroppedFileCalls, DroppedRoots, AccountingErrors;
    internal long ZoneAt, DungeonAt;
    internal bool Zone, Dungeon;
  }

  private readonly struct Scope(Session session, int depth)
  {
    internal readonly Session? Session = session;
    internal readonly int Depth = depth;
  }

  private static int ThreadingId() => Thread.CurrentThread.ManagedThreadId;
  private static int[] GcCounts() => Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
  private static double Ms(long ticks) => ticks * (1000d / Stopwatch.Frequency);
  private static string F(long ticks) => Ms(ticks).ToString("F3", Invariant);
  private static string Cell(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

  internal static void Initialize()
  {
    try { Install(); }
    catch (Exception ex)
    {
      Ready = false;
      Log.Warning("Startup profiler initialization failed: " + ex.Message);
    }
  }

  private static void Install()
  {
    if (!EwpProfiler.ShouldCaptureStartup || Patches != null) return;
    Patches = new Harmony(PatchId);
    Ready = PatchLifecycle(typeof(ZNet), "Awake", nameof(WorldPrefix), true);
    Ready &= PatchLifecycle(typeof(ZoneSystem), "Start", nameof(ZonePostfix), false);
    Ready &= PatchLifecycle(typeof(DungeonDB), "Start", nameof(DungeonPostfix), false);
    // Reuse the existing EWP Harmony hooks, which already resolve the actual game signatures.
    Coverage.Add("Lifecycle\tZNet.Shutdown\tdelegated to EWP CleanupOnShutdown.Prefix");
    Coverage.Add("Lifecycle\tZNet.ShutdownWithoutSave\tdelegated to EWP ProfilerShutdownWithoutSave.Prefix");
    if (!Ready)
    {
      Log.Warning("Startup profiler disabled: a required world lifecycle hook is unavailable.");
      return;
    }
    RegisterCommon(EwpAssembly, "EWP");
    Add(EwpAssembly, "EWP", "YAML reload", "Service.FileLoading", "ReloadAll", 0);
    Add(EwpAssembly, "EWP", "File discovery and hashing", "Service.YamlChangeDetector", "TakeAll", 0);
    Add(EwpAssembly, "EWP", "File discovery", "Service.YamlChangeDetector", "ListFiles", 0);
    Add(EwpAssembly, "EWP", "File read and hash", "Service.YamlChangeDetector", "TryRead", 4, nameof(FilePrefix));
    Add(EwpAssembly, "EWP", "Parse and cache files", "Service.FileLoading", "StoreChanges", 2);
    Add(EwpAssembly, "EWP", "Mixed YAML parsing", "Service.Yaml", "ParseMixed", 4, nameof(FilePrefix));
    Add(EwpAssembly, "EWP", "Mixed YAML splitting", "Service.Yaml", "SplitMixed", 1);
    Add(EwpAssembly, "EWP", "YAML shorthand normalization", "Service.Yaml", "PreParse", 1);
    Add(EwpAssembly, "EWP", "Apply parsed cache", "Service.YamlFileCache", "Apply", 2);
    Add(EwpAssembly, "EWP", "Data loading", "Data.DataLoading", "LoadFromFiles", 2);
    Add(EwpAssembly, "EWP", "Data rebuild", "Data.DataLoading", "RebuildFromCache", 2);
    Add(EwpAssembly, "EWP", "Rule loading", "ExpandWorld.Prefab.Loading", "RebuildFromCache", 3);
    Add(EwpAssembly, "EWP", "Rule construction", "ExpandWorld.Prefab.Loading", "FromData", 1);
    Add(EwpAssembly, "EWP", "Rule indexing", "ExpandWorld.Prefab.InfoManager", "Add", 1);
    Add(EwpAssembly, "EWP", "Rule patch setup", "ExpandWorld.Prefab.InfoManager", "Patch", 0);
    Add(EwpAssembly, "EWP", "Rule config loading", "ExpandWorld.Prefab.ConfigManager", "LoadFromFiles", 2);
    // Both mods use this non-generic parser entry. Attribute only DTOs from the
    // active mod assembly, never another consumer of the shared YAML library.
    Add(typeof(Deserializer).Assembly, "YAML", "YAML deserialization", typeof(Deserializer).FullName!, "Deserialize", 2,
      nameof(YamlPrefix), new[] { typeof(IParser), typeof(Type) });
  }

  internal static void AttachEwd()
  {
    if (!Ready || EwdAssembly != null) return;
    try
    {
      var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("ExpandWorldData.EWD", false) != null);
      if (assembly == null) return;
      EwdAssembly = assembly;
      const string m = "EWD";
      RegisterCommon(assembly, m);
      Add(assembly, m, "World data initialization", "ExpandWorldData.Data.InitializeWorld", "Postfix", 1);
      Add(assembly, m, "Zone content initialization", "ExpandWorldData.Data.InitializeContent", "Postfix", 0);
      Add(assembly, m, "Dungeon initialization", "ExpandWorldData.Data.InitializeRooms", "Postfix", 0);
      Add(assembly, m, "Data loading", "Data.DataLoading", "LoadEntries", 0);
      Add(assembly, m, "Data file discovery", "Data.DataLoading", "GetSourceFiles", 4);
      Add(assembly, m, "Manager YAML read", "ExpandWorldData.Data.DataManager", "Read", 1);
      Add(assembly, m, "Manager config reading", "ExpandWorldData.Data.YamlDataManager", "ReadConfigs", 0);
      Add(assembly, m, "Manager apply", "ExpandWorldData.Data.YamlDataManager", "Apply", 1);
      Add(assembly, m, "Pending synchronized data", "ExpandWorldData.Data.SyncedDataManager", "PostInitialize", 0);
      Add(assembly, m, "Migration: files", "ExpandWorldData.Migration.Migrations", "RunFiles", 0);
      Add(assembly, m, "Migration: vanilla", "ExpandWorldData.Migration.Migrations", "RunVanilla", 0);
      Add(assembly, m, "Migration: dungeons", "ExpandWorldData.Migration.Migrations", "RunDungeons", 1);
      Add(assembly, m, "No build initialization", "ExpandWorldData.Data.NoBuildManager", "Initialize", 0);
      Add(assembly, m, "No build apply", "ExpandWorldData.Data.NoBuildManager", "Apply", 1);
      Add(assembly, m, "No build index", "ExpandWorldData.Data.NoBuildManager", "CreateIndex", 1);
      Manager(assembly, "Vegetation.VegetationManager", true, true, true);
      Manager(assembly, "Locations.LocationManager", true, true, true);
      Manager(assembly, "AltBiomes.AltBiomeManager", true, true, false);
      Manager(assembly, "Dungeon.RoomManager", true, true, false);
      Manager(assembly, "Dungeon.DungeonManager", true, false, false);
      Manager(assembly, "Environment.EnvironmentManager", true, true, false);
      Manager(assembly, "Clutter.ClutterManager", true, false, false);
      Manager(assembly, "World.WorldManager", false, false, false);
      Manager(assembly, "Territory.TerritoryManager", false, false, false);
      Manager(assembly, "Spawn.SpawnManager", false, true, false);
      Manager(assembly, "PersistentEvents.PersistentEventManager", true, false, false);
      Add(assembly, m, "Persistent events load/apply", "ExpandWorldData.PersistentEvents.PersistentEventManager", "LoadAndApply", 2);
      Add(assembly, m, "Room conversion", "ExpandWorldData.Dungeon.RoomManager", "FromData", 2);
      Add(assembly, m, "Location hash index", "ExpandWorldData.Locations.LocationManager", "UpdateHashes", 0);
      Add(assembly, m, "Location instances", "ExpandWorldData.Locations.LocationManager", "UpdateInstances", 0);
      Add(assembly, m, "Alternative biome apply", "ExpandWorldData.AltBiomes.AltBiomeManager", "Apply", 2);
      Add(assembly, m, "Alternative biome placement preparation", "ExpandWorldData.AltBiomes.AltBiomePlacement", "PrepareWorldAltBiomes", 1);
      Add(assembly, m, "Alternative biome placement application", "ExpandWorldData.AltBiomes.AltBiomePlacement", "ApplyWorldAltBiomes", 0);
      Add(assembly, m, "Room theme initialization", "ExpandWorldData.Dungeon.RoomManager", "CreateNameToTheme", 0);
      Add(assembly, m, "Biome reading", "ExpandWorldData.Biome.BiomeManager", "ReadConfigs", 0);
      Add(assembly, m, "Biome defaults", "ExpandWorldData.Biome.BiomeManager", "CompleteDefaultConfig", 0);
      Add(assembly, m, "Biome environments", "ExpandWorldData.Biome.BiomeManager", "LoadEnvironments", 0);
      Log.Info("Startup profiler: EWD hooks installed (see report for coverage).");
    }
    catch (Exception ex) { Log.Warning("Startup profiler: EWD attachment incomplete: " + ex.Message); }
  }

  private static void RegisterCommon(Assembly assembly, string module)
  {
    Add(assembly, module, "Value group collection", "Data.ValueGroups", "Add", 2);
    Add(assembly, module, "Value group resolution", "Data.ValueGroups", "Resolve", 0);
    Add(assembly, module, "Prefab/component scan", "Data.ValueGroups", "LoadDefaults", 0);
    Add(assembly, module, "Nested group expansion", "Data.ValueGroups", "ResolveNested", 1);
    Add(assembly, module, "Data entry construction", "Data.DataLoading", "LoadEntry", 2);
    Add(assembly, module, "YAML deserializer setup", "Service.Yaml", "Deserializer", 0);
    Add(assembly, module, "YAML fallback setup", "Service.Yaml", "DeserializerUnSafe", 0);
  }

  private static void Manager(Assembly assembly, string type, bool initialize, bool loadApply, bool load)
  {
    var name = type.Substring(type.LastIndexOf('.') + 1);
    type = "ExpandWorldData." + type;
    if (initialize) Add(assembly, "EWD", name + " initialize", type, "Initialize", 0);
    Add(assembly, "EWD", name + " read", type, "ReadConfigs", 0);
    Add(assembly, "EWD", name + " set", type, "Set", 1);
    if (loadApply) Add(assembly, "EWD", name + " load/apply", type, "LoadAndApply", 1);
    if (load) Add(assembly, "EWD", name + " load", type, "Load", 2);
  }

  private static MethodInfo? Find(Type? type, string name, int count, Type[]? signature = null)
  {
    if (type == null) return null;
    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .Where(m => m.Name == name && !m.IsGenericMethod && !m.IsAbstract && m.GetParameters().Length == count);
    if (signature != null) methods = methods.Where(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(signature));
    var matches = methods.ToArray();
    return matches.Length == 1 ? matches[0] : null;
  }

  private static HarmonyMethod Hook(string method, int priority) => new HarmonyMethod(typeof(StartupProfiler), method) { priority = priority };

  private static bool PatchLifecycle(Type type, string method, string hook, bool prefix)
  {
    var name = type.FullName + "." + method;
    try
    {
      var target = Find(type, method, 0) ?? throw new MissingMethodException(name);
      if (prefix) Patches!.Patch(target, prefix: Hook(hook, Priority.First));
      else Patches!.Patch(target, postfix: Hook(hook, Priority.Last));
      Coverage.Add("Lifecycle\t" + name + "\tinstalled");
      return true;
    }
    catch (Exception ex)
    {
      Coverage.Add("Lifecycle\t" + name + "\t" + Cell(ex.Message));
      Log.Warning("Startup profiler: missing lifecycle hook " + name + ": " + ex.Message);
      return false;
    }
  }

  private static void Add(Assembly assembly, string module, string stage, string type, string method, int count,
    string prefix = nameof(MeasurePrefix), Type[]? signature = null)
  {
    var name = type + "." + method;
    MethodInfo? target = null;
    try
    {
      target = Find(assembly.GetType(type, false), method, count, signature) ?? throw new MissingMethodException(name);
      // Validate positional parameters before installing specialized hooks.
      if (prefix == nameof(FilePrefix) && target.GetParameters()[0].ParameterType != typeof(string))
        throw new InvalidOperationException("Expected string path argument.");
      Targets.Add(target, new Target(module, stage, target));
      Patches!.Patch(target, prefix: Hook(prefix, Priority.First), finalizer: Hook(nameof(MeasureFinalizer), Priority.Last));
      Coverage.Add(module + "\t" + name + "\tinstalled");
    }
    catch (Exception ex)
    {
      if (target != null) Targets.Remove(target);
      Coverage.Add(module + "\t" + name + "\t" + Cell(ex.GetType().Name + ": " + ex.Message));
      Log.Warning("Startup profiler: unavailable " + module + " " + name + ": " + ex.Message);
    }
  }

  private static void WorldPrefix()
  {
    if (!Ready || !EwpProfiler.ShouldCaptureStartup) return;
    try
    {
      if (Current != null) Finish("next-world-before-completion");
      AttachEwd();
      AccountingWarning = false;
      Current = new Session();
    }
    catch (Exception ex) { Log.Warning("Startup profiler could not begin: " + ex.Message); }
  }
  private static void ZonePostfix()
  {
    if (Current is { } s && s.Thread == ThreadingId() && !s.Zone)
    {
      s.Zone = true;
      s.ZoneAt = Stopwatch.GetTimestamp() - s.Started;
    }
  }
  private static void DungeonPostfix()
  {
    if (Current is { } s && s.Thread == ThreadingId() && !s.Dungeon)
    {
      s.Dungeon = true;
      s.DungeonAt = Stopwatch.GetTimestamp() - s.Started;
    }
  }
  internal static void WorldShutdown(string reason)
  {
    // A shutdown after both callbacks but before LateUpdate still completed loading.
    if (Current is { Zone: true, Dungeon: true }) Finish("zone-and-dungeon-completed");
    else Finish(reason);
  }

  internal static void Tick()
  {
    if (Current is not { } s || s.Thread != ThreadingId()) return;
    if (s.Zone && s.Dungeon) Finish("zone-and-dungeon-completed");
    else if (Ms(Stopwatch.GetTimestamp() - s.Started) >= 300000) Finish("startup-timeout");
  }

  internal static void Dispose()
  {
    Finish("plugin-destroy");
    try { Patches?.UnpatchSelf(); }
    catch (Exception ex) { Log.Warning("Startup profiler unpatch failed: " + ex.Message); }
    Ready = false;
  }

  private static void MeasurePrefix(MethodBase __originalMethod, out Scope __state) => __state = Enter(__originalMethod, null, null);
  private static void FilePrefix(MethodBase __originalMethod, string __0, out Scope __state) => __state = Enter(__originalMethod, __0, null);
  private static void YamlPrefix(MethodBase __originalMethod, Type __1, out Scope __state) => __state = Enter(__originalMethod, null, __1);

  private static bool OwnsDto(Type type, Assembly assembly)
  {
    if (type.Assembly == assembly) return true;
    if (type.IsArray) return OwnsDto(type.GetElementType()!, assembly);
    return type.IsGenericType && type.GetGenericArguments().Any(t => OwnsDto(t, assembly));
  }

  private static Scope Enter(MethodBase method, string? file, Type? yamlType)
  {
    var s = Current;
    if (s == null || s.Thread != ThreadingId()) return default;
    try
    {
      if (!Targets.TryGetValue(method, out var target)) return default;
      var module = target.Module;
      if (yamlType != null)
      {
        if (s.Stack.Count == 0) return default;
        module = s.Stack[s.Stack.Count - 1].Module;
        var assembly = module == "EWP" ? EwpAssembly : module == "EWD" ? EwdAssembly : null;
        if (assembly == null || !OwnsDto(yamlType, assembly)) return default;
      }
      s.Stack.Add(new Frame(target, module, Stopwatch.GetTimestamp(), file));
      return new Scope(s, s.Stack.Count);
    }
    catch (Exception ex) { AccountingFailed(s, ex.Message); return default; }
  }

  // Always propagate the original exception unchanged, and close failed calls too.
  private static Exception? MeasureFinalizer(Exception? __exception, Scope __state)
  {
    var s = __state.Session;
    if (s == null || s != Current) return __exception;
    try
    {
      var end = Stopwatch.GetTimestamp();
      if (s.Stack.Count != __state.Depth) { AccountingFailed(s, "scope order mismatch"); return __exception; }
      var index = s.Stack.Count - 1;
      var f = s.Stack[index];
      s.Stack.RemoveAt(index);
      var elapsed = Math.Max(0, end - f.Started);
      if (index == 0)
      {
        s.RootTicks += elapsed;
        if (s.Roots.Count < MaxRoots) s.Roots.Add((f.Module, f.Target.Stage, f.Started - s.Started, elapsed));
        else s.DroppedRoots++;
      }
      else
      {
        var parent = s.Stack[index - 1];
        parent.Children += elapsed;
        s.Stack[index - 1] = parent;
      }
      var key = (f.Target, f.Module);
      if (!s.Stats.TryGetValue(key, out var totals)) s.Stats[key] = totals = new Totals();
      totals.Add(elapsed, f.Children, __exception != null);
      if (f.File != null)
      {
        var fileKey = (f.Target.Stage, f.File);
        if (s.Files.TryGetValue(fileKey, out var ft)) ft.Add(elapsed, f.Children, __exception != null);
        else if (s.Files.Count < MaxFiles)
        {
          s.Files[fileKey] = ft = new Totals();
          ft.Add(elapsed, f.Children, __exception != null);
        }
        else s.DroppedFileCalls++;
      }
    }
    catch (Exception ex) { AccountingFailed(s, ex.Message); }
    return __exception;
  }

  private static void AccountingFailed(Session s, string message)
  {
    s.AccountingErrors++;
    if (AccountingWarning) return;
    AccountingWarning = true;
    Log.Warning("Startup profiler accounting error: " + message);
  }

  private static DateTime GermanTime(DateTime utc)
  {
    foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
      try { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(id)); }
      catch (TimeZoneNotFoundException) { }
      catch (InvalidTimeZoneException) { }
    return utc;
  }

  private static void Finish(string reason)
  {
    var s = Current;
    if (s == null || s.Thread != ThreadingId()) return;
    Current = null;
    var elapsed = Stopwatch.GetTimestamp() - s.Started;
    try
    {
      var gc = GcCounts();
      var heap = GC.GetTotalMemory(false);
      var side = ZNet.instance == null ? "unknown" : ZNet.instance.IsServer() ? "server" : "client";
      var root = Path.Combine(FileLoading.BaseDirectory, "logs", "EwpProfiler");
      var stamp = GermanTime(s.Utc).ToString("yyyyMMdd_HHmmss_fff", Invariant);
      var path = Path.Combine(root, stamp + "_startup-loading");
      for (int suffix = 1; Directory.Exists(path); suffix++) path = Path.Combine(root, stamp + "_startup-loading_" + suffix);
      Directory.CreateDirectory(path);
      const string header = "Module\tStage\tMethod\tCalls\tInclusiveMs\tExclusiveMs\tMaxMs\tExceptions";
      string Row(string module, string stage, string method, Totals t) => string.Join("\t", module, stage, Cell(method),
        t.Calls.ToString(Invariant), F(t.Inclusive), F(t.Exclusive), F(t.Max), t.Exceptions.ToString(Invariant));
      var rows = new List<string> { header };
      foreach (var target in Targets.Values.OrderBy(t => t.Module).ThenBy(t => t.Stage))
      {
        var modules = target.Module == "YAML" ? new[] { "EWP", "EWD" } : new[] { target.Module };
        foreach (var module in modules)
        {
          var totals = s.Stats.TryGetValue((target, module), out var t) ? t : new Totals();
          rows.Add(Row(module, target.Stage, target.Method.DeclaringType!.FullName + "." + target.Method.Name, totals));
        }
      }
      File.WriteAllLines(Path.Combine(path, "StartupMethods.tsv"), rows);
      var files = new List<string> { "Stage\tFile\tCalls\tInclusiveMs\tExclusiveMs\tMaxMs\tExceptions" };
      foreach (var pair in s.Files.OrderByDescending(p => p.Value.Inclusive))
      {
        var t = pair.Value;
        files.Add(string.Join("\t", pair.Key.Item1, Cell(pair.Key.Item2), t.Calls.ToString(Invariant),
          F(t.Inclusive), F(t.Exclusive), F(t.Max), t.Exceptions.ToString(Invariant)));
      }
      File.WriteAllLines(Path.Combine(path, "StartupFiles.tsv"), files);
      var timeline = new List<string> { "Module\tStage\tStartMs\tElapsedMs\tEndMs" };
      foreach (var span in s.Roots.OrderBy(r => r.Started))
        timeline.Add(string.Join("\t", span.Module, span.Stage, F(span.Started), F(span.Elapsed), F(span.Started + span.Elapsed)));
      File.WriteAllLines(Path.Combine(path, "StartupTimeline.tsv"), timeline);
      File.WriteAllLines(Path.Combine(path, "StartupCoverage.tsv"), new[] { "Module\tMethod\tPatchStatus" }.Concat(Coverage));
      var summary = new List<string>
      {
        "Startup loading profiler 1.1", "UTC start: " + s.Utc.ToString("O", Invariant), "Side: " + side,
        "EWP assembly: " + EwpAssembly.FullName + " / " + EwpAssembly.ManifestModule.ModuleVersionId,
        "EWD assembly: " + (EwdAssembly == null ? "not found" : EwdAssembly.FullName + " / " + EwdAssembly.ManifestModule.ModuleVersionId),
        "End: " + reason, "Zone completed: " + s.Zone, "Dungeon completed: " + s.Dungeon,
        "Zone completion ms: " + (s.Zone ? F(s.ZoneAt) : "not observed"),
        "Dungeon completion ms: " + (s.Dungeon ? F(s.DungeonAt) : "not observed"),
        "Elapsed wall ms: " + F(elapsed), "Measured root wall ms: " + F(s.RootTicks),
        "Outside measured roots ms: " + F(Math.Max(0, elapsed - s.RootTicks)),
        "Exclusive EWP ms: " + F(s.Stats.Where(p => p.Key.Item2 == "EWP").Sum(p => p.Value.Exclusive)),
        "Exclusive EWD ms: " + F(s.Stats.Where(p => p.Key.Item2 == "EWD").Sum(p => p.Value.Exclusive)),
        "GC collections (process-wide, by generation): " + string.Join(", ", gc.Select((n, i) => (n - s.Gc[i]).ToString(Invariant))),
        "Managed heap bytes (process-wide, approximate): " + s.Heap + " -> " + heap,
        "Accounting errors: " + s.AccountingErrors + "; open scopes: " + s.Stack.Count + "; dropped file calls: " + s.DroppedFileCalls + "; dropped root timeline rows: " + s.DroppedRoots,
        "Inclusive includes nested measured calls; exclusive subtracts them. Residual includes uninstrumented code, other mods and measurement overhead.",
        "Only the startup thread is measured. Zero calls means not observed, not proof that work was absent. Generic EWD read helpers are not patched; their I/O remains in parent residual.",
        "Exceptions are escaping exceptions per measured call, not a count of all parser failures. SDC/WEC have no loader hooks.",
        "Report I/O is outside the capture. Timings include instrumentation overhead; heap delta is not allocated bytes.",
        "", "Top methods by exclusive wall time:"
      };
      foreach (var pair in s.Stats.OrderByDescending(p => p.Value.Exclusive).Take(30))
        summary.Add(pair.Key.Item2 + " / " + pair.Key.Item1.Stage + ": " + F(pair.Value.Exclusive) + " ms exclusive, " + F(pair.Value.Inclusive) + " ms inclusive, " + pair.Value.Calls + " calls");
      File.WriteAllLines(Path.Combine(path, "StartupSummary.txt"), summary);
      Log.Info("Startup profiler: " + stamp + " (" + reason + ", " + F(elapsed) + " ms wall, " + F(s.RootTicks) + " ms measured roots)");
    }
    catch (Exception ex) { Log.Warning("Startup profiler report failed: " + ex.Message); }
  }
}
