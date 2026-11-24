using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ExpandWorld.Prefab;
using YamlDotNet.Core;

namespace Service;

// Cache files are disposable, never authoritative. Each published file is a complete snapshot.
internal sealed class PersistentYamlCache(string directory, bool streaming)
{
  private const int FormatVersion = 1;
  private const int SchemaVersion = 1;
  private const int ParserVersion = 1;
  private const int Magic = 0x43505745; // EWPC
  private const int DigestSize = 32;
  private const int HeaderSize = 4 * sizeof(int) + 3 * DigestSize + sizeof(int);
  private const string Extension = ".ewpc";

  private string CachePath(string source) => Path.Combine(directory,
    Hex(Digest(Encoding.UTF8.GetBytes(Path.GetFullPath(source) + "\n" + streaming))) + Extension);

  private byte[] Identity(string source)
  {
    // Legacy ObjectYaml.FromLine formats numeric distances using CurrentCulture.
    var culture = CultureInfo.CurrentCulture;
    var number = culture.NumberFormat;
    var path = Path.GetFullPath(source);
    var identity = string.Join("\n", path, path.ToLowerInvariant().GetStableHashCode().ToString(CultureInfo.InvariantCulture),
      streaming, typeof(Yaml).Assembly.ManifestModule.ModuleVersionId,
      typeof(Parser).Assembly.ManifestModule.ModuleVersionId, typeof(ZDO).Assembly.ManifestModule.ModuleVersionId,
      typeof(object).Assembly.ManifestModule.ModuleVersionId, Environment.Version, culture.Name, number.NumberDecimalSeparator, number.NegativeSign,
      number.PositiveSign, number.NaNSymbol, number.PositiveInfinitySymbol, number.NegativeInfinitySymbol);
    return Digest(Encoding.UTF8.GetBytes(identity));
  }

  private static byte[] Digest(byte[] bytes)
  {
    using var sha = SHA256.Create();
    return sha.ComputeHash(bytes);
  }
  private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
  private static byte[] Fingerprint(ChangedFile file)
  {
    var hash = Convert.FromBase64String(file.Hash);
    if (hash.Length != DigestSize) throw new InvalidDataException("Invalid content fingerprint.");
    return hash;
  }

  internal bool TryLoad(ChangedFile file, out Yaml.MixedFileEntries entries)
  {
    entries = null!;
    try
    {
      var bytes = ReadPayload(CachePath(file.Path), Identity(file.Path), Fingerprint(file), out var reason);
      if (bytes == null)
      {
        StartupProfiler.Count("Persistent cache miss");
        StartupProfiler.Count("Persistent cache miss: " + reason);
        if (reason != "absent") StartupProfiler.Count("Persistent cache fallback");
        return false;
      }
      entries = YamlDtoBinary.Decode(bytes);
      if (!string.Equals(entries.ContentHash, file.Hash, StringComparison.Ordinal))
        throw new InvalidDataException("DTO content hash mismatch.");
      StartupProfiler.Count("Persistent cache hit");
      return true;
    }
    catch (Exception)
    {
      entries = null!;
      StartupProfiler.Count("Persistent cache miss");
      StartupProfiler.Count("Persistent cache miss: read or decode error");
      StartupProfiler.Count("Persistent cache fallback");
      return false;
    }
  }

  private static byte[]? ReadPayload(string path, byte[] identity, byte[] fingerprint, out string reason)
  {
    reason = "absent";
    FileStream stream;
    try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
    catch (FileNotFoundException) { return null; }
    catch (DirectoryNotFoundException) { return null; }
    using (stream)
    using (var reader = new BinaryReader(stream))
    {
      reason = "size or truncation";
      if (stream.Length < HeaderSize || stream.Length > HeaderSize + YamlDtoBinary.MaxBytes) return null;
      reason = "format version";
      if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion) return null;
      reason = "schema version";
      if (reader.ReadInt32() != SchemaVersion) return null;
      reason = "parser version";
      if (reader.ReadInt32() != ParserVersion) return null;
      reason = "build, path or environment";
      if (!reader.ReadBytes(DigestSize).SequenceEqual(identity)) return null;
      reason = "content changed";
      if (!reader.ReadBytes(DigestSize).SequenceEqual(fingerprint)) return null;
      var checksum = reader.ReadBytes(DigestSize);
      int length = reader.ReadInt32();
      reason = "size or truncation";
      if (length <= 0 || length != stream.Length - stream.Position) return null;
      var payload = reader.ReadBytes(length);
      StartupProfiler.Count("Persistent cache bytes read", stream.Position);
      reason = "checksum";
      if (payload.Length != length || !Digest(payload).SequenceEqual(checksum)) return null;
      reason = "";
      return payload;
    }
  }

  internal void Store(ChangedFile file, Yaml.MixedFileEntries entries)
  {
    byte[] payload;
    try
    {
      payload = YamlDtoBinary.Encode(entries);
    }
    catch (Exception)
    {
      StartupProfiler.Count("Persistent cache serialization skipped");
      return;
    }
    try
    {
      Directory.CreateDirectory(directory);
      WritePayload(CachePath(file.Path), Identity(file.Path), Fingerprint(file), payload);
      StartupProfiler.Count("Persistent cache bytes written", HeaderSize + payload.Length);
      StartupProfiler.Count("Persistent cache writes");
    }
    catch (Exception) { StartupProfiler.Count("Persistent cache write error"); }
  }

  private static void WritePayload(string path, byte[] identity, byte[] fingerprint, byte[] payload)
  {
    // Same directory and unique temporary name: readers only see old or new complete files.
    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      using (var writer = new BinaryWriter(stream))
      {
        writer.Write(Magic); writer.Write(FormatVersion); writer.Write(SchemaVersion); writer.Write(ParserVersion);
        writer.Write(identity); writer.Write(fingerprint); writer.Write(Digest(payload));
        writer.Write(payload.Length); writer.Write(payload);
        // A disposable cache need not force a disk flush per YAML file.
        // A torn file after power loss is rejected by length and checksum.
        writer.Flush();
      }
      if (File.Exists(path)) File.Replace(temp, path, null);
      else File.Move(temp, path);
    }
    finally { try { File.Delete(temp); } catch (Exception) { } }
  }

  // Never delete a current file's cache just because another process is scanning.
  // Aged orphan snapshots and abandoned temporary files are safe to remove;
  // racing readers/writers simply fall back to YAML.
  internal void Prune(IEnumerable<string> files)
  {
    try
    {
      if (!Directory.Exists(directory)) return;
      var live = new HashSet<string>(files.Select(CachePath), StringComparer.OrdinalIgnoreCase);
      var otherMode = new PersistentYamlCache(directory, !streaming);
      live.UnionWith(files.Select(otherMode.CachePath));
      var cutoff = DateTime.UtcNow.AddDays(-30);
      foreach (var path in Directory.EnumerateFiles(directory))
      {
        var name = Path.GetFileName(path);
        var parts = name.Split('.');
        bool hex = parts[0].Length == 64 && parts[0].All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F');
        bool owned = hex && (parts.Length == 2 && parts[1] == "ewpc" || parts.Length == 4 && parts[1] == "ewpc" &&
          Guid.TryParseExact(parts[2], "N", out _) && parts[3] == "tmp");
        if (!owned || live.Contains(path) || File.GetLastWriteTimeUtc(path) >= cutoff) continue;
        try { File.Delete(path); StartupProfiler.Count("Persistent cache pruned"); } catch (Exception) { }
      }
    }
    catch (Exception) { StartupProfiler.Count("Persistent cache cleanup error"); }
  }
}
