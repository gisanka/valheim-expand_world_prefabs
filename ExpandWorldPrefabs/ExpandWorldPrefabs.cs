using System;
using System.IO;
using System.Linq;
using BepInEx;
using Data;
using HarmonyLib;
using Service;
using UnityEngine;
namespace ExpandWorld.Prefab;

[BepInPlugin(GUID, NAME, VERSION)]
public class EWP : BaseUnityPlugin
{
  public const string GUID = "expand_world_prefabs";
  public const string NAME = "Expand World Prefabs";
  public const string VERSION = "1.67";
#nullable disable
  public static Harmony Harmony;
#nullable enable
  public void Awake()
  {
    Prefab.Config.Init(Config);
    Log.Init(Logger);
    EwpProfiler.Initialize(Config);
    if (Application.isBatchMode) EwpProfiler.ServerStarting("headless-awake");
    using var awakeProfile = EwpProfiler.Begin("startup-awake", "EWP.Awake");
    PokeTimers.Initialize();
    Harmony = new(GUID);
    Harmony.PatchAll();
    Log.Init(Logger);
    FileLoading.EnsureBaseDirectory();
    RuleLog.Init(Path.Combine(FileLoading.BaseDirectory, "logs"));
    try
    {
      if (Prefab.Config.AutomaticReload)
      {
        FileLoading.SetupWatchers(Config, true);
        BlueprintWatcher.Start();
      }
      DataStorage.LoadSavedData();
    }
    catch (Exception e)
    {
      Log.Error(e.StackTrace);
    }
  }
  public void Start()
  {
    ServerDevcommands.Init();
    ExpandWorldData.Init();
    new Terminal.ConsoleCommand("ewp_reload_data", "Manually reloads the ewp_data.yaml file.", (args) =>
    {
      DataStorage.LoadSavedData();
    }, true);
    new Terminal.ConsoleCommand("ewp_reload", "Manually reloads all config and data files.", (args) =>
    {
      FileLoading.ReloadAll();
      DataStorage.LoadSavedData();
    }, true);
    new Terminal.ConsoleCommand("ewp_biomes", "Lists alternate biome names for biomes and bannedBiomes.", (args) =>
    {
      var names = AltBiomeList.m_altBiomes.Select(alt => alt.m_name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();
      args.Context.AddString(names.Length > 0 ? string.Join("\n", names) : "No alternate biomes loaded. Enter a world and try again.");
    });
    new Terminal.ConsoleCommand("ewp_profiler_start", "Begin an EWP diagnostic capture (server only).", (args) =>
    {
      if (ZNet.instance == null || !ZNet.instance.IsServer()) { args.Context.AddString("Profiler: requires the server world."); return; }
      args.Context.AddString(EwpProfiler.Start("console") ? "Profiler capture started." : "Profiler capture already running.");
    }, true);
    new Terminal.ConsoleCommand("ewp_profiler_snapshot", "Write current profiler reports and immediately continue.", (args) =>
    {
      if (ZNet.instance == null || !ZNet.instance.IsServer()) { args.Context.AddString("Profiler: requires the server world."); return; }
      args.Context.AddString(EwpProfiler.Snapshot() ? "Profiler snapshot written; next segment started." : "Profiler is not active, or report failed (see BepInEx log).");
    }, true);
    new Terminal.ConsoleCommand("ewp_profiler_stop", "Write and stop the current EWP profiler segment.", (args) =>
    {
      args.Context.AddString(EwpProfiler.Stop() ? "Profiler capture written." : "Profiler is not active.");
    }, true);
    new Terminal.ConsoleCommand("ewp_itemtype", "Lists the itemtype_* value groups that can be used in prefab.", (args) =>
    {
      if (!ObjectDB.instance)
      {
        args.Context.AddString("No items loaded. Enter a world and try again.");
        return;
      }
      var names = ObjectDB.instance.m_items.Select(item => item.GetComponent<ItemDrop>()).Where(item => item).Select(item => $"itemtype_{item.m_itemData.m_shared.m_itemType}").Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();
      args.Context.AddString(string.Join("\n", names));
    });
  }
  public void LateUpdate()
  {
    EwpProfiler.Tick();
    ConfigManager.Flush();
    if (ZNet.instance == null) return;
    if (ZNet.instance.IsServer()) EwpProfiler.ServerStarting("first-server-tick");
    FileLoading.Update();
    using (EwpProfiler.Begin("tick", "created")) HandleCreated.Execute();
    using (EwpProfiler.Begin("tick", "changed")) HandleChanged.Execute();
    using (EwpProfiler.Begin("tick", "spawn")) DelayedSpawn.Execute();
    using (EwpProfiler.Begin("tick", "remove")) DelayedRemove.Execute();
    using (EwpProfiler.Begin("tick", "pokes")) DelayedPoke.Execute();
    using (EwpProfiler.Begin("tick", "exec")) DelayedExec.Execute();
    using (EwpProfiler.Begin("tick", "rpc")) DelayedRpc.Execute();
    using (EwpProfiler.Begin("tick", "terrain")) DelayedTerrain.Execute();
    TerrainSnapshot.Execute();
    using (EwpProfiler.Begin("tick", "owner")) DelayedOwner.Execute();
    using (EwpProfiler.Begin("tick", "saved-data")) DataStorage.SaveSavedData();
    EwpProfiler.Tick();
  }
  public void OnDestroy()
  {
    EwpProfiler.Stop("plugin-destroy");
    FileLoading.StopWatching();
    BlueprintWatcher.Stop();
    ConfigManager.Flush(true);
    RuleLog.Close();
  }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
public class CleanupOnShutdown
{
  static void Prefix()
  {
    EwpProfiler.Stop("server-shutdown");
  }
  static void Postfix()
  {
    HandleCreated.Clear();
    HandleChanged.Clear();
    DelayedSpawn.Clear();
    DelayedRemove.Clear();
    PokeTimers.Clear();
    DelayedExec.Clear();
    DelayedRpc.Clear();
    DelayedTerrain.Clear();
    TerrainSnapshot.Clear();
    DelayedOwner.Clear();
  }
}
// Postfix is after Valheim's synchronous save setup; the reporter runs in
// EWP.LateUpdate, NEVER in this hook and never on the save writer thread.
[HarmonyPatch(typeof(ZNet), "SaveWorld")]
internal static class EwpWorldSaveReportTrigger
{
  static bool Prepare()
  {
    var exists = AccessTools.Method(typeof(ZNet), "SaveWorld") != null;
    if (!exists) Log.Warning("EWP Profiler: ZNet.SaveWorld is unavailable; world-save reports disabled (interval reports still work).");
    return exists;
  }

  static void Postfix(ZNet __instance)
  {
    if (__instance != null && __instance.IsServer()) EwpProfiler.WorldSaveRequested();
  }
}


[HarmonyPatch(typeof(ZNet), nameof(ZNet.ShutdownWithoutSave))]
internal static class ProfilerShutdownWithoutSave
{
  static void Prefix() { EwpProfiler.Stop("server-shutdown-without-save"); }
}
