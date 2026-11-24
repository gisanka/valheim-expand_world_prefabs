using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Data;
using ExpandWorld.Prefab;
using NUnit.Framework;
using Service;

namespace ExpandWorldPrefabs.Tests;

public class PersistentYamlCacheTests
{
  private string directory = null!;
  private PersistentYamlCache cache = null!;

  [SetUp]
  public void Setup()
  {
    directory = Path.Combine(Path.GetTempPath(), "ewp-persistent-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    cache = new PersistentYamlCache(directory, false);
  }
  [TearDown]
  public void Cleanup() => Directory.Delete(directory, true);

  internal static ChangedFile Snapshot(string path, string text)
  {
    using var sha = SHA256.Create();
    return new ChangedFile(path, text, Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text))));
  }

  internal static Yaml.MixedFileEntries Parse(ChangedFile file, bool streaming = false)
  {
    Assert.That(Yaml.ParseMixed(file.Path, file.Content, streaming, out var result, out var error, out var cacheable), Is.True, error);
    Assert.That(cacheable, Is.True);
    result.ContentHash = file.Hash;
    return result;
  }

  [Test]
  public void MixedRoundTripPreservesAllFieldsAndSourceSpans()
  {
    const string yaml = """
      # heading
      - name: group
        values: [a, b]
        item: {prefab: Wood, chance: 0.25, customData: {key: value}}
      - prefab: Player
        types: [create, state]
        filter: ""
        bannedFilter: x
        spawn: [{prefab: Wood, data: {ints: [a, 1]}}]
        swap: "Wood, named"
        objects: [{prefab: Stone, filter: y}]
        poke: [{prefab: Stone, bannedFilter: z, delay: "0.1"}]
        exec: [{command: test}, "<test>"]
        command: [a, b]
        delay: 0.25
        triggerRules: false
        objectRpc: [{name: test, target: x}]
        terrain: [{levelRadius: "1"}]
        log: [{log: [a, b], logFile: test}]
        data: |
          strings: [a, b]
      - config: Settings
        key: value
      """;
    var file = Snapshot(Path.Combine(directory, "mixed.yaml"), yaml);
    var expected = Parse(file);
    Assert.That(expected.ScriptEntries, Has.Count.EqualTo(1));
    Assert.That(expected.DataEntries, Has.Count.EqualTo(3));
    Assert.That(expected.ScriptSources[0].YamlItem, Is.EqualTo(2));
    cache.Store(file, expected);
    Assert.That(new PersistentYamlCache(directory, false).TryLoad(file, out var actual), Is.True);
    DtoAssert.Equal(expected, actual);
    Assert.That(actual.ScriptEntries[0].data!.Name, Is.EqualTo(expected.ScriptEntries[0].data!.Name));
    Assert.That(actual.ScriptEntries[0].data.Inline, Is.Null);
    actual.ScriptEntries[0].prefab = "changed";
    Assert.That(cache.TryLoad(file, out var fresh), Is.True);
    Assert.That(fresh.ScriptEntries[0].prefab, Is.EqualTo("Player"));
  }

  [Test]
  public void SharedMutableReferencesAndNullsRoundTrip()
  {
    var fields = new[] { "x", "" };
    var data = new DataField("name");
    var log = new List<object> { "x", new Dictionary<object, object> { ["log"] = "test" } };
    var entries = new Yaml.MixedFileEntries
    {
      ScriptEntries = [new RuleYaml { filters = fields, bannedFilters = fields, data = data, log = new RuleLogData(log),
        logFile = new RuleLogFiles(log), command = new StringList([]), spawnDelay = null, delay = 0f },
        new RuleYaml { data = data, triggerRules = false, spawnDelay = float.NaN }],
      DataEntries = [new DataYaml { name = "name", strings = [] }]
    };
    var actual = YamlDtoBinary.Decode(YamlDtoBinary.Encode(entries));
    DtoAssert.Equal(entries, actual);
    Assert.That(actual.ScriptEntries[0].filters, Is.SameAs(actual.ScriptEntries[0].bannedFilters));
    Assert.That(actual.ScriptEntries[0].data, Is.SameAs(actual.ScriptEntries[1].data));
    Assert.That(actual.ScriptEntries[0].log!.Value, Is.SameAs(actual.ScriptEntries[0].logFile!.Value));
    Assert.That(actual.ScriptEntries[0].filters, Is.Not.SameAs(fields));
  }

  [Test]
  public void ParsedAliasesKeepTheirReferenceRelationships()
  {
    var file = Snapshot(Path.Combine(directory, "alias.yaml"), "- prefab: Player\n  filters: &shared [a, b]\n  bannedFilters: *shared\n");
    var expected = Parse(file);
    cache.Store(file, expected);
    Assert.That(cache.TryLoad(file, out var actual), Is.True);
    DtoAssert.Equal(expected, actual);
    Assert.That(ReferenceEquals(actual.ScriptEntries[0].filters, actual.ScriptEntries[0].bannedFilters),
      Is.EqualTo(ReferenceEquals(expected.ScriptEntries[0].filters, expected.ScriptEntries[0].bannedFilters)));
  }

  [Test]
  public void CultureDependentLegacyConversionInvalidatesCache()
  {
    var original = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
      System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
      var file = Snapshot(Path.Combine(directory, "culture.yaml"), "- prefab: Player\n  objects: ['Wood, 1.5']\n");
      cache.Store(file, Parse(file));
      Assert.That(cache.TryLoad(file, out _), Is.True);
      System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
      Assert.That(cache.TryLoad(file, out _), Is.False);
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = original; }
  }

  [Test]
  public void RejectsUnsupportedOrCyclicFlexibleValues()
  {
    var list = new List<object>(); list.Add(list);
    var entries = new Yaml.MixedFileEntries { ScriptEntries = [new RuleYaml { log = new RuleLogData(list) }] };
    Assert.Throws<InvalidDataException>(() => YamlDtoBinary.Encode(entries));
    entries.ScriptEntries[0].log = new RuleLogData(new Version(1, 2));
    Assert.Throws<InvalidDataException>(() => YamlDtoBinary.Encode(entries));
  }

  [Test]
  public void CacheInvalidatesOnContentPathAndParserMode()
  {
    var file = Snapshot(Path.Combine(directory, "old.yaml"), "- prefab: Player\n  data: {ints: [x, 1]}\n");
    var parsed = Parse(file);
    cache.Store(file, parsed);
    Assert.That(cache.TryLoad(file, out _), Is.True);
    var changed = Snapshot(file.Path, file.Content.Replace("x, 1", "x, 2"));
    Assert.That(cache.TryLoad(changed, out _), Is.False);
    var renamed = Snapshot(Path.Combine(directory, "new.yaml"), file.Content);
    Assert.That(cache.TryLoad(renamed, out _), Is.False);
    Assert.That(Parse(renamed).DataEntries[0].name, Is.Not.EqualTo(parsed.DataEntries[0].name));
    Assert.That(new PersistentYamlCache(directory, true).TryLoad(file, out _), Is.False);
  }

  [TestCase(4)] // format
  [TestCase(8)] // schema
  [TestCase(12)] // parser
  [TestCase(16)] // environment identity
  [TestCase(100)] // checksum
  public void CacheRejectsCorruptOrObsoleteHeaderAndCanRepair(int offset)
  {
    var file = Snapshot(Path.Combine(directory, "file.yaml"), "- prefab: Player\n");
    var parsed = Parse(file);
    cache.Store(file, parsed);
    var path = Directory.GetFiles(directory, "*.ewpc").Single();
    var bytes = File.ReadAllBytes(path); bytes[offset] ^= 0xff; File.WriteAllBytes(path, bytes);
    Assert.That(cache.TryLoad(file, out _), Is.False);
    cache.Store(file, parsed);
    Assert.That(cache.TryLoad(file, out var actual), Is.True);
    DtoAssert.Equal(parsed, actual);
  }

  [Test]
  public void TruncationAndUnavailableDirectoryAreMisses()
  {
    var file = Snapshot(Path.Combine(directory, "file.yaml"), "- prefab: Player\n");
    cache.Store(file, Parse(file));
    var path = Directory.GetFiles(directory, "*.ewpc").Single();
    File.WriteAllBytes(path, [1, 2, 3]);
    Assert.That(cache.TryLoad(file, out _), Is.False);
    var unavailable = new PersistentYamlCache(path, false); // regular file, not a directory
    Assert.That(unavailable.TryLoad(file, out _), Is.False);
    Assert.DoesNotThrow(() => unavailable.Store(file, Parse(file)));
  }

  [Test]
  public void ConcurrentWritersPublishOnlyCompleteFiles()
  {
    var file = Snapshot(Path.Combine(directory, "file.yaml"), "- prefab: Player\n");
    var expected = Parse(file);
    Parallel.For(0, 12, _ => new PersistentYamlCache(directory, false).Store(file, expected));
    Assert.That(cache.TryLoad(file, out var actual), Is.True);
    DtoAssert.Equal(expected, actual);
    Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
  }

  [Test]
  public void CleanupKeepsLiveEntriesAndUnrelatedFiles()
  {
    var live = Snapshot(Path.Combine(directory, "live.yaml"), "- prefab: Player\n");
    var orphan = Snapshot(Path.Combine(directory, "orphan.yaml"), live.Content);
    cache.Store(live, Parse(live)); cache.Store(orphan, Parse(orphan));
    var unrelated = Path.Combine(directory, "other.ewpc"); File.WriteAllText(unrelated, "unrelated");
    foreach (var path in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-31));
    cache.Prune([live.Path]);
    Assert.That(cache.TryLoad(live, out _), Is.True);
    Assert.That(cache.TryLoad(orphan, out _), Is.False);
    Assert.That(File.Exists(unrelated), Is.True);
  }
}
