using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;

namespace Service;

// Parses the files served by YamlChangeDetector and rebuilds EWP's rules from them.
public static class FileLoading
{
  public static readonly string BaseDirectory = Path.Combine(Paths.ConfigPath, "expand_world");
  public static readonly string BackupDirectory = Path.Combine(Paths.ConfigPath, "expand_world_backups");
  private const string PrefabFileName = "expand_prefabs.yaml";
  private static readonly string PrefabFilePath = Path.Combine(BaseDirectory, PrefabFileName);
  public static readonly string DataGamePath = Path.GetFullPath(Path.Combine("BepInEx", "config", "data"));
  public static readonly string DataProfilePath = Path.GetFullPath(Path.Combine(Paths.ConfigPath, "data"));
  public const string DataPattern = "expand_data*.yaml";
  public const string PrefabPattern = "expand_prefabs*.yaml";

  private static readonly YamlChangeDetector Changes = CreateDetector();
  private static readonly YamlFileCache Cache = new();
  private static FileSystemWatcher? ConfigWatcher;
  private static bool OverwriteBackup = true;

  private static YamlChangeDetector CreateDetector()
  {
    var detector = new YamlChangeDetector(OnChanges, () => ExpandWorld.Prefab.Config.ReloadQuietSeconds);
    detector.AddSource(DataGamePath, "*.yaml");
    if (DataGamePath != DataProfilePath)
      detector.AddSource(DataProfilePath, "*.yaml");
    detector.AddSource(BaseDirectory, DataPattern);
    detector.AddSource(BaseDirectory, PrefabPattern);
    return detector;
  }

  public static bool IsPrefabPath(string path) =>
    YamlChangeDetector.IsYaml(path) && YamlChangeDetector.IsInFolder(path, BaseDirectory)
    && YamlChangeDetector.MatchesPattern(path, PrefabPattern);

  private static void EnsureDirectories()
  {
    Directory.CreateDirectory(DataGamePath);
    Directory.CreateDirectory(DataProfilePath);
    Directory.CreateDirectory(BaseDirectory);
  }

  private static void EnsurePrefabFile()
  {
    EnsureDirectories();
    if (!File.Exists(PrefabFilePath))
      File.WriteAllText(PrefabFilePath, "# Write your entries here. Good luck!");
  }

  public static void EnsureBaseDirectory() => Directory.CreateDirectory(BaseDirectory);

  public static void SetupWatchers(ConfigFile config, bool overwriteBackup = true)
  {
    StopWatching();
    OverwriteBackup = overwriteBackup;
    EnsurePrefabFile();
    try
    {
      WatchConfig(config);
      Changes.StartWatching(ThreadingHelper.SynchronizingObject);
    }
    catch
    {
      StopWatching();
      throw;
    }
  }

  public static void StopWatching()
  {
    Changes.StopWatching();
    ConfigWatcher?.Dispose();
    ConfigWatcher = null;
  }

  // The config file reloads immediately, without debounce or hashing.
  private static void WatchConfig(ConfigFile config)
  {
    var watcher = new FileSystemWatcher(Path.GetDirectoryName(config.ConfigFilePath)!, Path.GetFileName(config.ConfigFilePath))
    {
      SynchronizingObject = ThreadingHelper.SynchronizingObject
    };
    ConfigWatcher = watcher;
    watcher.Created += (s, e) => ReloadConfig(e.FullPath, config);
    watcher.Changed += (s, e) => ReloadConfig(e.FullPath, config);
    watcher.Renamed += (s, e) => ReloadConfig(e.FullPath, config);
    watcher.Deleted += (s, e) => ReloadConfig(e.FullPath, config);
    watcher.EnableRaisingEvents = true;
  }

  private static void ReloadConfig(string path, ConfigFile config)
  {
    if (!File.Exists(path)) return;
    if (ExpandWorld.Prefab.ConfigManager.IsSelfWrite && config == ExpandWorld.Prefab.Config.Main) return;
    BackupFile(path, true);
    try { config.Reload(); }
    catch
    {
      Log.Error($"There was an issue loading your {DisplayPath(config.ConfigFilePath)}");
      Log.Error("Please check your config entries for spelling and format!");
    }
  }

  // Startup and ewp_reload deliberately load the current files immediately.
  public static void ReloadAll()
  {
    if (ExpandWorld.Prefab.Helper.IsClient()) return;
    using var profile = ExpandWorld.Prefab.EwpProfiler.Begin("yaml-reload", "FileLoading.ReloadAll");
    EnsurePrefabFile();
    Cache.Clear();
    var changes = Changes.TakeAll();
    StoreChanges(changes, false);
    if (ExpandWorld.Prefab.Config.PersistentYaml) CreatePersistentCache(ExpandWorld.Prefab.Config.StreamingYaml).Prune(changes.Files);
    Rebuild(changes.Files, true);
  }

  // Pending events stay queued until a world is loaded; ReloadAll supersedes them.
  public static void Update()
  {
    if (ExpandWorld.Prefab.Helper.IsClient() || ZoneSystem.instance == null) return;
    Changes.Update();
  }

  private static bool OnChanges(YamlChanges changes)
  {
    using var profile = ExpandWorld.Prefab.EwpProfiler.Begin("yaml-reload", "FileLoading.OnChanges");
    try
    {
      var (modified, prefabs) = StoreChanges(changes, true);
      if (modified == 0)
        Log.Info($"YAML reload skipped: no applicable changes ({changes.Unchanged} file(s) unchanged).");
      else
        Rebuild(changes.Files, prefabs);
      return true;
    }
    catch (Exception ex)
    {
      Log.Error($"YAML reload failed: {ex}");
      return false;
    }
  }

  private static PersistentYamlCache CreatePersistentCache(bool streaming) => new(
    Path.Combine(Paths.ConfigPath, "expand_world_yaml_cache"), streaming);

  // Invalid YAML never replaces a previously valid entry. Returns how many cache entries changed.
  internal static (int Modified, bool Prefabs) StoreChanges(YamlChanges changes, bool backup)
  {
    bool streaming = ExpandWorld.Prefab.Config.StreamingYaml;
    return StoreChanges(changes, backup, ExpandWorld.Prefab.Config.PersistentYaml ? CreatePersistentCache(streaming) : null, streaming);
  }

  internal static (int Modified, bool Prefabs) StoreChanges(YamlChanges changes, bool backup, PersistentYamlCache? persistent, bool streaming)
  {
    var modified = 0;
    var prefabs = false;
    // This detector count is not proof of a valid parsed entry: failed parses
    // can also have an acknowledged hash in the existing reload pipeline.
    ExpandWorld.Prefab.StartupProfiler.Count("Unchanged file hashes (detector)", changes.Unchanged);
    ExpandWorld.Prefab.StartupProfiler.Count("Changed YAML files", changes.Changed.Count);
    ExpandWorld.Prefab.StartupProfiler.Count("Failed YAML reads", changes.Failed.Count);
    ExpandWorld.Prefab.StartupProfiler.Count("Listed YAML files", changes.Files.Count);
    if (ExpandWorld.Prefab.StartupProfiler.Collecting)
    {
      var changedOrFailed = new HashSet<string>(changes.Changed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
      changedOrFailed.UnionWith(changes.Failed.Select(f => f.Path));
      ExpandWorld.Prefab.StartupProfiler.Count("Retained in-memory entries (no detected change)",
        changes.Files.Count(path => !changedOrFailed.Contains(path) && Cache.Contains(path)));
    }
    foreach (var failed in changes.Failed)
      Log.Warning($"YAML reload skipped '{DisplayPath(failed.Path)}': {failed.Error}. Previous valid entries remain active (if any).");
    foreach (var file in changes.Changed)
    {
      Yaml.MixedFileEntries entries;
      if (persistent == null || !persistent.TryLoad(file, out entries))
      {
        if (!Yaml.ParseMixed(file.Path, file.Content, streaming, out entries, out var error, out var cacheable))
        {
          Log.Warning($"YAML reload skipped '{DisplayPath(file.Path)}': {error}. Previous valid entries remain active (if any).");
          continue;
        }
        entries.ContentHash = file.Hash; // Hash of precisely the text snapshot that parsed successfully.
        if (persistent != null)
        {
          if (cacheable) persistent.Store(file, entries);
          else ExpandWorld.Prefab.StartupProfiler.Count("Persistent cache skipped: parser fallback");
        }
      }
      Cache.Store(file.Path, entries);
      modified++;
      prefabs |= IsPrefabPath(file.Path);
      if (!backup) continue;
      try { BackupContent(file.Path, file.Content, OverwriteBackup); }
      catch (Exception ex) { Log.Warning($"YAML backup failed for '{DisplayPath(file.Path)}': {ex.Message}"); }
    }
    foreach (var path in changes.Deleted)
    {
      if (!Cache.Remove(path)) continue;
      modified++;
      prefabs |= IsPrefabPath(path);
    }
    return (modified, prefabs);
  }

  // Prefab files are loaded in reverse so that earlier files take precedence.
  private static void Rebuild(List<string> files, bool prefabs) =>
    Cache.Apply(files, prefabs ? files.Where(IsPrefabPath).Reverse().ToList() : null);

  public static string DisplayPath(string path)
  {
    var root = Path.GetFullPath(Paths.ConfigPath)
      .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(path);
    return (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full)
      .Replace('\\', '/');
  }

  private static string BackupPath(string path)
  {
    Directory.CreateDirectory(BackupDirectory);
    var stamp = DateTime.Now.ToString("yyyy-MM-dd");
    return Path.Combine(BackupDirectory, $"{Path.GetFileNameWithoutExtension(path)}_{stamp}{Path.GetExtension(path)}.bak");
  }

  public static void BackupFile(string path, bool overwrite)
  {
    if (!File.Exists(path)) return;
    var backup = BackupPath(path);
    if (overwrite || !File.Exists(backup)) File.Copy(path, backup, true);
  }

  private static void BackupContent(string path, string content, bool overwrite)
  {
    var backup = BackupPath(path);
    if (overwrite || !File.Exists(backup)) File.WriteAllText(backup, content);
  }
}
