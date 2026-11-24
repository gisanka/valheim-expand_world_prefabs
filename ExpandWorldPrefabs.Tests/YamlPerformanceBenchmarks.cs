using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Xml;
using BepInEx.Logging;
using NUnit.Framework;
using Service;

namespace ExpandWorldPrefabs.Tests;

[NonParallelizable]
public class YamlPerformanceBenchmarks
{
  private sealed class Sample(long ticks, long allocated, long bytes, int hits, int parsed, int errors)
  {
    public override string ToString() => string.Join("\t", (ticks * 1000d / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture),
      allocated, bytes, hits, parsed, errors);
  }

  [Test, Explicit("Set EWP_BENCHMARK_ROOT to a copy of the actual YAML corpus.")]
  public void CompareParserAndPersistentCache()
  {
    var root = Environment.GetEnvironmentVariable("EWP_BENCHMARK_ROOT");
    Assert.That(root, Is.Not.Null.And.Not.Empty);
    var files = Directory.GetFiles(root!, "*.yaml", SearchOption.AllDirectories)
      .OrderBy(p => p, StringComparer.Ordinal).Select(p => PersistentYamlCacheTests.Snapshot(p, File.ReadAllText(p))).ToArray();
    Assert.That(files, Is.Not.Empty);
    var logger = typeof(Log).GetField("Logger", BindingFlags.Static | BindingFlags.NonPublic)!;
    var old = logger.GetValue(null);
    using var source = new ManualLogSource("YAML performance benchmark");
    Log.Init(source);
    var directory = Path.Combine(Path.GetTempPath(), "ewp-yaml-benchmark-" + Guid.NewGuid().ToString("N"));
    try
    {
      // Validate once outside the measured runs, using an independent recursive
      // comparison including references, nulls, spans and all public/private fields.
      var compatible = new List<ChangedFile>();
      int strictFiles = 0;
      foreach (var file in files)
      {
        bool a = Yaml.ParseMixed(file.Path, file.Content, false, out var legacy, out var oldError, out var strict);
        bool b = Yaml.ParseMixed(file.Path, file.Content, true, out var streaming, out var newError, out _);
        Assert.That(b, Is.EqualTo(a), file.Path);
        Assert.That(newError, Is.EqualTo(oldError), file.Path);
        DtoAssert.Equal(legacy, streaming);
        if (!a || !strict) continue;
        strictFiles++;
        legacy.ContentHash = file.Hash;
        byte[] encoded;
        try { encoded = YamlDtoBinary.Encode(legacy); }
        catch (InvalidDataException) { continue; }
        DtoAssert.Equal(legacy, YamlDtoBinary.Decode(encoded));
        compatible.Add(file);
      }
      TestContext.Progress.WriteLine($"Files: {files.Length}; strict files: {strictFiles}; codec-compatible files: {compatible.Count}");
      Assert.That(compatible, Is.Not.Empty, "No corpus file was eligible for a binary cache roundtrip.");
      TestContext.Progress.WriteLine("Scenario\tIteration\tMs\tManagedAllocatedBytes\tCacheBytes\tPersistentHits\tParsed\tErrors");
      // Same snapshot inputs for all runs. OS cache is not flushed; this is a
      // managed-host comparison, not a process-cold Valheim startup benchmark.
      for (int iteration = 0; iteration < 5; iteration++)
      {
        var plain = Run(files, null, false, directory);
        var p2 = Run(files, null, true, directory);
        TestContext.Progress.WriteLine($"baseline\t{iteration}\t{plain}");
        TestContext.Progress.WriteLine($"P2\t{iteration}\t{p2}");
        foreach (bool streaming in new[] { false, true })
        {
          if (Directory.Exists(directory)) Directory.Delete(directory, true);
          var cache = new PersistentYamlCache(directory, streaming);
          string mode = streaming ? "P1+P2" : "P1";
          TestContext.Progress.WriteLine($"{mode}-cold\t{iteration}\t{Run(files, cache, streaming, directory)}");
          TestContext.Progress.WriteLine($"{mode}-warm\t{iteration}\t{Run(files, cache, streaming, directory)}");
          if (streaming)
          {
            var single = files.ToArray();
            single[0] = PersistentYamlCacheTests.Snapshot(single[0].Path, single[0].Content + "\n# benchmark edit\n");
            TestContext.Progress.WriteLine($"{mode}-one-change\t{iteration}\t{Run(single, cache, true, directory)}");
            var multiple = single.Select((f, i) => i > 0 && i <= 10 ? PersistentYamlCacheTests.Snapshot(f.Path, f.Content + "\n# benchmark edit\n") : f).ToArray();
            TestContext.Progress.WriteLine($"{mode}-multiple-change\t{iteration}\t{Run(multiple, cache, true, directory)}");
          }
        }
      }
      CompareSerializers(compatible);
    }
    finally
    {
      logger.SetValue(null, old);
      if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
  }

  private static Sample Run(ChangedFile[] files, PersistentYamlCache? cache, bool streaming, string directory)
  {
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var started = Stopwatch.GetTimestamp();
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    int hits = 0, parsed = 0, errors = 0;
    foreach (var file in files)
    {
      if (cache != null && cache.TryLoad(file, out _)) { hits++; continue; }
      parsed++;
      if (!Yaml.ParseMixed(file.Path, file.Content, streaming, out var dto, out _, out var cacheable)) { errors++; continue; }
      dto.ContentHash = file.Hash;
      if (cacheable) cache?.Store(file, dto);
    }
    long ticks = Stopwatch.GetTimestamp() - started;
    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    long bytes = cache != null && Directory.Exists(directory) ? Directory.GetFiles(directory, "*.ewpc").Sum(p => new FileInfo(p).Length) : 0;
    return new Sample(ticks, allocated, bytes, hits, parsed, errors);
  }

  private static void CompareSerializers(List<ChangedFile> entries)
  {
    // A second, built-in candidate. Failure or a non-equivalent DTO graph
    // disqualifies it; it must not be benchmarked only on easy DTOs.
    var contract = new DataContractSerializer(typeof(Yaml.MixedFileEntries), new DataContractSerializerSettings
    {
      PreserveObjectReferences = true,
      KnownTypes = new[] { typeof(List<object>), typeof(Dictionary<object, object>) },
      MaxItemsInObjectGraph = 1000000
    });
    byte[] ContractEncode(Yaml.MixedFileEntries dto)
    {
      using var stream = new MemoryStream();
      using (var writer = XmlDictionaryWriter.CreateBinaryWriter(stream)) { contract.WriteObject(writer, dto); writer.Flush(); }
      return stream.ToArray();
    }
    Yaml.MixedFileEntries ContractDecode(byte[] bytes)
    {
      using var reader = XmlDictionaryReader.CreateBinaryReader(bytes, XmlDictionaryReaderQuotas.Max);
      return (Yaml.MixedFileEntries)contract.ReadObject(reader)!;
    }
    void Compare(string name, Func<Yaml.MixedFileEntries, byte[]> encode, Func<byte[], Yaml.MixedFileEntries> decode)
    {
      long writeTicks = 0, readTicks = 0, size = 0, writeAlloc = 0, readAlloc = 0;
      try
      {
        foreach (var file in entries)
        {
          var dto = PersistentYamlCacheTests.Parse(file);
          long alloc = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
          byte[] bytes = encode(dto);
          writeTicks += Stopwatch.GetTimestamp() - start; writeAlloc += GC.GetAllocatedBytesForCurrentThread() - alloc;
          size += bytes.Length;
          alloc = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp();
          var roundTrip = decode(bytes);
          readTicks += Stopwatch.GetTimestamp() - start; readAlloc += GC.GetAllocatedBytesForCurrentThread() - alloc;
          DtoAssert.Equal(dto, roundTrip);
        }
        TestContext.Progress.WriteLine($"Serializer {name}: {entries.Count} equivalent files; encode {writeTicks * 1000d / Stopwatch.Frequency:F3} ms; decode {readTicks * 1000d / Stopwatch.Frequency:F3} ms; {size} bytes; allocations {writeAlloc}/{readAlloc} bytes");
      }
      catch (Exception ex) { TestContext.Progress.WriteLine($"Serializer {name} rejected: {ex.GetType().Name}: {ex.Message}"); }
    }
    Compare("closed binary DTO codec", YamlDtoBinary.Encode, YamlDtoBinary.Decode);
    Compare("DataContract binary XML", ContractEncode, ContractDecode);
  }
}
