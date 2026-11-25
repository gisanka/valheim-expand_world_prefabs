using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExpandWorld.Prefab;
using NUnit.Framework;
using Service;

namespace ExpandWorldPrefabs.Tests;

[NonParallelizable]
public class ProfilerPhaseTests
{
  private string directory = null!;
  private TestPathScope paths = null!;

  [SetUp]
  public void Setup()
  {
    paths = new TestPathScope();
    directory = Path.Combine(Path.GetTempPath(), "ewp-phase-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    Log.Init(new ManualLogSource("ProfilerPhaseTests"));
    EwpProfiler.Initialize(new ConfigFile(Path.Combine(directory, "profiler.cfg"), false));
    EwpProfiler.SourceCatalogRootOverride = Path.Combine(directory, "catalogs");
  }

  [TearDown]
  public void Cleanup()
  {
    EwpProfiler.Stop("test-cleanup");
    EwpProfiler.SetLoadedRuleSources(Array.Empty<EwpProfiler.RuleSource>());
    EwpProfiler.SourceCatalogRootOverride = null;
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
    paths.Dispose();
  }

  private void Start(string name)
  {
    Assert.That(EwpProfiler.Start("test-phases"), Is.True);
    typeof(EwpProfiler).GetField("OutputDirectory", BindingFlags.Static | BindingFlags.NonPublic)!
      .SetValue(null, Path.Combine(directory, name));
  }

  [Test]
  public void BatchMessagesOnlyContainTypeAndTimestampIdentifier()
  {
    const string batch = "20261123_044100_484";
    Assert.That(EwpProfiler.FormatBatchLog("started", "continuous-gameplay", batch),
      Is.EqualTo("Profiler started: continuous-gameplay #20261123_044100_484"));
    Assert.That(EwpProfiler.FormatBatchLog("saved", "continuous-gameplay", batch, "interval"),
      Is.EqualTo("Profiler saved: continuous-gameplay #20261123_044100_484 (interval)"));
    Assert.That(EwpProfiler.FormatBatchLog("FAILED", "startup-headless-awake", batch, "world-save"),
      Is.EqualTo("Profiler FAILED: startup-headless-awake #20261123_044100_484 (world-save)"));
    Assert.That(EwpProfiler.FormatBatchLog("started", "continuous-gameplay", batch),
      Does.Not.Contain("/"));
  }

  [Test]
  public void RotationLogNamesOnlyNewBatchAndPreviousEndReason()
  {
    const string batch = "20261123_163000_466";
    Assert.That(EwpProfiler.FormatRotationLog("continuous-gameplay", batch, "interval"),
      Is.EqualTo("Profiler: new continuous-gameplay segment #20261123_163000_466 (interval)"));
    Assert.That(EwpProfiler.FormatRotationLog("continuous-gameplay", batch, "world-save"),
      Is.EqualTo("Profiler: new continuous-gameplay segment #20261123_163000_466 (world-save)"));
    Assert.That(EwpProfiler.FormatRotationLog("continuous-server-idle", batch, "manual-snapshot"),
      Does.Not.Contain("C:"));
    Assert.That(EwpProfiler.FormatRotationLog("continuous-gameplay", batch, "interval", 1),
      Is.EqualTo("Profiler: new continuous-gameplay segment #20261123_163000_466 (interval) - 1 clients"));
    Assert.That(EwpProfiler.FormatRotationLog("continuous-gameplay", batch, "world-save", 2),
      Is.EqualTo("Profiler: new continuous-gameplay segment #20261123_163000_466 (world-save) - 2 clients"));
    Assert.That(EwpProfiler.FormatRotationLog("continuous-server-idle", batch, "interval", 0),
      Is.EqualTo("Profiler: new continuous-server-idle segment #20261123_163000_466 (interval) - 0 clients"));
    Assert.That(EwpProfiler.FormatRotationLog("continuous-gameplay", batch, "interval", -1),
      Is.EqualTo("Profiler: new continuous-gameplay segment #20261123_163000_466 (interval)"));
  }

  [Test]
  public void PluginMetadataKeepsUpstreamVersionAndProfilerHasIndependentVersion()
  {
    var metadata = typeof(EWP).GetCustomAttribute<BepInEx.BepInPlugin>(inherit: false);
    Assert.That(metadata, Is.Not.Null);
    Assert.That(metadata!.Name + " " + metadata.Version,
      Is.EqualTo("Expand World Prefabs 1.68"));
    Assert.That(EWP.VERSION, Is.EqualTo("1.68"));
    Assert.That(EwpProfiler.ProfilerVersion, Is.EqualTo("1.16"));
  }

  [Test]
  public void PhaseReport_AgreesWithWholeCaptureTotals_WithoutCountingNestedScopesTwice()
  {
    Start("first");
    using (EwpProfiler.Begin("tick", "spawn"))
    {
      using var nested = EwpProfiler.Begin("data-write", "synthetic-child");
      GC.KeepAlive(new object());
    }
    using (EwpProfiler.Begin("tick", "spawn")) { }
    // An exception must close the outer phase just like an ordinary using scope.
    Assert.Throws<InvalidOperationException>(() =>
    {
      using var phase = EwpProfiler.Begin("tick", "terrain");
      throw new InvalidOperationException("synthetic phase failure");
    });
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);

    var phaseRows = File.ReadAllLines(Path.Combine(directory, "first", "ProfilerPhases.tsv"))
      .Skip(1).Select(line => line.Split('\t')).ToArray();
    Assert.That(phaseRows.Select(row => row[1]).Distinct(), Is.EquivalentTo(new[] { "spawn", "terrain" }));
    var ruleRows = File.ReadAllLines(Path.Combine(directory, "first", "ProfilerRules.tsv"))
      .Skip(1).Select(line => line.Split('\t')).Where(row => row[0] == "tick").ToArray();
    foreach (var name in new[] { "spawn", "terrain" })
    {
      var rows = phaseRows.Where(row => row[1] == name).ToArray();
      var expectedCalls = name == "spawn" ? 2 : 1;
      Assert.That(rows.Sum(row => long.Parse(row[2], CultureInfo.InvariantCulture)), Is.EqualTo(expectedCalls));
      var total = rows.Sum(row => double.Parse(row[3], CultureInfo.InvariantCulture));
      var maximum = rows.Max(row => double.Parse(row[4], CultureInfo.InvariantCulture));
      Assert.That(total, Is.GreaterThanOrEqualTo(maximum));
      var aggregate = ruleRows.Single(row => row[1] == name);
      Assert.That(double.Parse(aggregate[3], CultureInfo.InvariantCulture), Is.EqualTo(total).Within(0.003));
    }
  }

  [Test]
  public void SourceReportPreservesLoadedVersionAndWarnsAfterReload()
  {
    const string identity = "config/expand_world/expand_prefabs.yaml#entry=2|Create|Player|";
    var original = new EwpProfiler.RuleSource(identity, "expand_world/expand_prefabs.yaml", 2, 5, 42, 48, "hash-A");
    var modified = new EwpProfiler.RuleSource(identity, "expand_world/expand_prefabs.yaml", 2, 5, 45, 51, "hash-B");
    EwpProfiler.SetLoadedRuleSources(new[] { original });
    try
    {
      Start("sources");
      using (EwpProfiler.Begin("rule", identity, identity)) { }
      EwpProfiler.SetLoadedRuleSources(new[] { modified });
      using (EwpProfiler.Begin("rule", identity, identity)) { }
      Assert.That(EwpProfiler.Stop("test-report"), Is.True);

      var refs = File.ReadAllLines(Path.Combine(directory, "sources", "ProfilerSourceCatalogs.tsv"))
        .Skip(1).Select(line => line.Split('\t')).ToArray();
      Assert.That(refs.Length, Is.EqualTo(2));
      Assert.That(refs[0][3], Is.Not.EqualTo(refs[1][3]));
      Assert.That(refs.Select(row => row[2]), Is.All.EqualTo("1"));
      var sourceRows = refs.SelectMany(row =>
      {
        var catalog = Path.Combine(directory, "catalogs", row[3].Split('/')[1], "ProfilerRuleSources.tsv");
        return File.ReadAllLines(catalog).Skip(1).Select(line => line.Split('\t'));
      }).ToArray();
      Assert.That(sourceRows.Length, Is.EqualTo(2));
      Assert.That(sourceRows.Select(row => row[6]), Is.EquivalentTo(new[] { "hash-A", "hash-B" }));
      Assert.That(sourceRows.Select(row => row[3]), Is.All.EqualTo("5"));
      Assert.That(sourceRows.Select(row => row[4]), Is.EquivalentTo(new[] { "42", "45" }));
      Assert.That(File.Exists(Path.Combine(directory, "sources", "ProfilerRuleSources.tsv")), Is.False);
      var summary = File.ReadAllText(Path.Combine(directory, "sources", "ProfilerSummary.txt"));
      Assert.That(summary, Does.Contain("Successful rule-source changes during this segment: 1"));
      Assert.That(summary, Does.Contain("Rule source catalogs referenced: 2"));
    }
    finally
    {
      EwpProfiler.SetLoadedRuleSources(Array.Empty<EwpProfiler.RuleSource>());
    }
  }

  [Test]
  public void UnchangedSourceCatalogIsReusedAndSurvivesSegmentPruning()
  {
    var source = new EwpProfiler.RuleSource("rule-A", "expand_world/a.yaml", 1, 3, 42, 44, "hash-A");
    EwpProfiler.SetLoadedRuleSources(new[] { source });
    Start("first-catalog");
    Assert.That(EwpProfiler.Stop("first"), Is.True);
    var firstRef = File.ReadAllLines(Path.Combine(directory, "first-catalog", "ProfilerSourceCatalogs.tsv"))[1].Split('\t');
    var catalog = Path.Combine(directory, "catalogs", firstRef[3].Split('/')[1], "ProfilerRuleSources.tsv");
    Assert.That(File.Exists(catalog), Is.True);
    Assert.That(Path.GetFileName(Path.GetDirectoryName(catalog)!), Does.Match(
      @"^\d{8}_\d{6}_\d{3}_sources_\d{6}(?:_\d{2})?$"));

    // Deliberately delete the old performance segment but keep its catalog.
    Directory.Delete(Path.Combine(directory, "first-catalog"), true);
    EwpProfiler.SetLoadedRuleSources(new[] { new EwpProfiler.RuleSource(
      "rule-A", "expand_world/a.yaml", 1, 3, 42, 44, "hash-A") });
    Start("second-catalog");
    Assert.That(EwpProfiler.Stop("second"), Is.True);
    var secondRef = File.ReadAllLines(Path.Combine(directory, "second-catalog", "ProfilerSourceCatalogs.tsv"))[1].Split('\t');
    Assert.That(secondRef[3], Is.EqualTo(firstRef[3]));
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "catalogs")), Has.Length.EqualTo(1));
    Assert.That(File.Exists(catalog), Is.True);
  }

  [Test]
  public void IdenticalCatalogIsReusedEvenAfterLoadedRevisionChanges()
  {
    var source = new EwpProfiler.RuleSource("rule-A", "expand_world/a.yaml", 1, 3, 42, 44, "hash-A");
    EwpProfiler.SetLoadedRuleSources(new[] { source });
    Start("initial-revision");
    Assert.That(EwpProfiler.Stop("first"), Is.True);
    var first = File.ReadAllLines(Path.Combine(directory, "initial-revision", "ProfilerSourceCatalogs.tsv"))[1]
      .Split('\t')[3];

    // Simulate another server lifecycle: an in-memory source revision alone
    // must not force an identical full on-disk catalog to be written again.
    EwpProfiler.SetLoadedRuleSources(Array.Empty<EwpProfiler.RuleSource>());
    EwpProfiler.SetLoadedRuleSources(new[] { source });
    Start("new-revision");
    Assert.That(EwpProfiler.Stop("second"), Is.True);
    var second = File.ReadAllLines(Path.Combine(directory, "new-revision", "ProfilerSourceCatalogs.tsv"))[1]
      .Split('\t')[3];
    Assert.That(second, Is.EqualTo(first));
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "catalogs")), Has.Length.EqualTo(1));
  }

  [Test]
  public void OnlyNewestCatalogIsCheckedEvenWhenAnOlderCatalogMatches()
  {
    var a = new EwpProfiler.RuleSource("rule-A", "expand_world/a.yaml", 1, 3, 42, 44, "hash-A");
    var b = new EwpProfiler.RuleSource("rule-A", "expand_world/a.yaml", 1, 3, 42, 44, "hash-B");
    EwpProfiler.SetLoadedRuleSources(new[] { a });
    Start("catalog-a");
    Assert.That(EwpProfiler.Stop("first"), Is.True);
    var first = File.ReadAllLines(Path.Combine(directory, "catalog-a", "ProfilerSourceCatalogs.tsv"))[1]
      .Split('\t')[3];

    EwpProfiler.SetLoadedRuleSources(new[] { b });
    Start("catalog-b");
    Assert.That(EwpProfiler.Stop("second"), Is.True);
    var middle = File.ReadAllLines(Path.Combine(directory, "catalog-b", "ProfilerSourceCatalogs.tsv"))[1]
      .Split('\t')[3];
    Assert.That(middle, Is.Not.EqualTo(first));

    EwpProfiler.SetLoadedRuleSources(new[] { a });
    Start("catalog-a-returned");
    Assert.That(EwpProfiler.Stop("third"), Is.True);
    var last = File.ReadAllLines(Path.Combine(directory, "catalog-a-returned", "ProfilerSourceCatalogs.tsv"))[1]
      .Split('\t')[3];
    Assert.That(last, Is.Not.EqualTo(first),
      "Only the latest catalog B may be compared, even if older A matches.");
    Assert.That(last, Is.Not.EqualTo(middle));
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "catalogs")), Has.Length.EqualTo(3));
  }

  [Test]
  public void MissingLatestCatalogFileCreatesFreshCatalogInsteadOfReusingOlder()
  {
    var root = Path.Combine(directory, "catalogs");
    Directory.CreateDirectory(root);
    var earlier = Path.Combine(root, "20260101_000000_000_sources_000001");
    var latest = Path.Combine(root, "20260102_000000_000_sources_000001");
    Directory.CreateDirectory(earlier);
    Directory.CreateDirectory(latest);
    var lines = new[] { "RuleIdentity\tSourceFile", "rule-A\texpand_world/a.yaml" };
    File.WriteAllLines(Path.Combine(earlier, "ProfilerRuleSources.tsv"), lines);
    Assert.That(EwpProfiler.FindLatestMatchingSourceCatalog(root, lines), Is.Null);
    File.WriteAllLines(Path.Combine(latest, "ProfilerRuleSources.tsv"), lines);
    Assert.That(EwpProfiler.FindLatestMatchingSourceCatalog(root, lines),
      Is.EqualTo(Path.Combine(latest, "ProfilerRuleSources.tsv")));
    Assert.That(EwpProfiler.FindLatestMatchingSourceCatalog(root, new[] { lines[0], "changed" }), Is.Null);
  }

  [Test]
  public void ChangedSourceAfterRotationCreatesNewTimestampedCatalog()
  {
    EwpProfiler.SetLoadedRuleSources(new[] {
      new EwpProfiler.RuleSource("rule-A", "expand_world/a.yaml", 1, 1, 1, 3, "hash-A") });
    Start("before-change");
    Assert.That(EwpProfiler.Stop("first"), Is.True);
    var a = File.ReadAllLines(Path.Combine(directory, "before-change", "ProfilerSourceCatalogs.tsv"))[1].Split('\t')[3];

    EwpProfiler.SetLoadedRuleSources(new[] {
      new EwpProfiler.RuleSource("rule-A", "expand_world/a.yaml", 1, 1, 1, 4, "hash-B") });
    Start("after-change");
    Assert.That(EwpProfiler.Stop("second"), Is.True);
    var b = File.ReadAllLines(Path.Combine(directory, "after-change", "ProfilerSourceCatalogs.tsv"))[1].Split('\t')[3];
    Assert.That(b, Is.Not.EqualTo(a));
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "catalogs")), Has.Length.EqualTo(2));
  }

  [Test]
  public void SavedDataCounters_RecordSkippedAndCompletedWrites()
  {
    Start("saved-data");
    EwpProfiler.SavedDataSkipped(false);
    EwpProfiler.SavedDataSkipped(true);
    EwpProfiler.SavedDataWriteStarted(44);
    EwpProfiler.SavedDataWriteCompleted(512);
    using (EwpProfiler.Begin("saved-data-detail", "serialize")) { }
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);
    var summary = File.ReadAllText(Path.Combine(directory, "saved-data", "ProfilerSummary.txt"));
    Assert.That(summary, Does.Contain("idle checks=1; cooldown checks=1; write attempts=1; completed=1"));
    Assert.That(summary, Does.Contain("last database entries=44; last UTF-8 bytes=512"));
    var scopes = File.ReadAllText(Path.Combine(directory, "saved-data", "ProfilerRules.tsv"));
    Assert.That(scopes, Does.Contain("saved-data-detail\tserialize"));
  }

  [Test]
  public void RuntimeAndManualMarkers_AreReported()
  {
    Start("runtime");
    var now = System.Diagnostics.Stopwatch.GetTimestamp();
    EwpProfiler.RecordUpdate(now, now + System.Diagnostics.Stopwatch.Frequency / 100);
    EwpProfiler.RecordPokeDispatch(0.25, true);
    Assert.That(EwpProfiler.Mark("manual-item-spawn"), Is.True);
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);
    var runtime = File.ReadAllLines(Path.Combine(directory, "runtime", "ProfilerRuntime.tsv"));
    Assert.That(runtime.Length, Is.GreaterThan(1));
    Assert.That(runtime[0], Does.Contain("EwpLateUpdateMaxMs"));
    Assert.That(runtime.Skip(1).Any(row => row.Split('\t')[14] == "1"), Is.True,
      "A restored poke must be counted without claiming a trace origin.");
    var markers = File.ReadAllText(Path.Combine(directory, "runtime", "ProfilerMarkers.tsv"));
    Assert.That(markers, Does.Contain("manual-item-spawn"));
  }

  [Test]
  public void SlowRootTrace_RetainsBoundedChildHierarchy()
  {
    var setting = (ConfigEntry<bool>)typeof(EwpProfiler)
      .GetField("CaptureSlowRootTrees", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    var threshold = (ConfigEntry<float>)typeof(EwpProfiler)
      .GetField("SlowRootThreshold", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    setting.Value = true;
    threshold.Value = 0f;
    Start("slow-roots");
    using (EwpProfiler.Begin("event", "root"))
    {
      using (EwpProfiler.Begin("rule", "child")) { }
    }
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);
    var rows = File.ReadAllLines(Path.Combine(directory, "slow-roots", "ProfilerSlowRoots.tsv"));
    Assert.That(rows.Length, Is.EqualTo(3));
    var root = rows[1].Split('\t');
    var child = rows[2].Split('\t');
    Assert.That(root[0], Is.EqualTo(child[0]));
    Assert.That(root[1], Is.EqualTo(child[2]), "Child parent span must match root span.");
    Assert.That(child[4], Is.EqualTo("rule"));
  }

  [Test]
  public void PrivateProfilerDefaults_StartWithSlowRootsAndAllRuleDetailsEnabled()
  {
    static ConfigEntry<T> Field<T>(string name) => (ConfigEntry<T>)typeof(EwpProfiler)
      .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    Assert.That(Field<bool>("Enable").Value, Is.True);
    Assert.That(Field<bool>("Detailed").Value, Is.True);
    Assert.That(Field<bool>("Allocations").Value, Is.True);
    Assert.That(Field<bool>("AllSpawnEvents").Value, Is.True);
    Assert.That(Field<bool>("CaptureSlowRootTrees").Value, Is.True);
    Assert.That(Field<float>("SlowRootThreshold").Value, Is.EqualTo(100f));
    Assert.That(Field<string>("RuleDetailFilter").Value, Is.EqualTo("*"));
    Assert.That(EwpProfiler.ShouldProfileRuleDetails("config/a.yaml#entry=42"), Is.True);
  }

  [Test]
  public void ExistingProfilerConfigIsPreservedWithoutMigration()
  {
    var cfg = new ConfigFile(Path.Combine(directory, "legacy-profiler.cfg"), false);
    cfg.Bind("EWP Profiler", "EnableProfiler", false).Value = false;
    cfg.Bind("EWP Profiler", "EnableDetailedEvents", true).Value = false;
    cfg.Bind("EWP Profiler", "EnableAllocationDiagnostics", true).Value = false;
    cfg.Bind("EWP Profiler", "RecordAllSpawnEvents", true).Value = false;
    cfg.Bind("EWP Profiler", "CaptureSlowRootTrees", true).Value = false;
    cfg.Bind("EWP Profiler", "SlowRootThresholdMs", 100f).Value = 250f;
    cfg.Bind("EWP Profiler", "RuleDetailFilter", "*").Value = "source#entry=42";

    EwpProfiler.Initialize(cfg);

    Assert.That(cfg.Bind("EWP Profiler", "EnableProfiler", true).Value, Is.False);
    Assert.That(cfg.Bind("EWP Profiler", "EnableDetailedEvents", true).Value, Is.False);
    Assert.That(cfg.Bind("EWP Profiler", "EnableAllocationDiagnostics", true).Value, Is.False);
    Assert.That(cfg.Bind("EWP Profiler", "RecordAllSpawnEvents", true).Value, Is.False);
    Assert.That(cfg.Bind("EWP Profiler", "CaptureSlowRootTrees", true).Value, Is.False);
    Assert.That(cfg.Bind("EWP Profiler", "SlowRootThresholdMs", 100f).Value, Is.EqualTo(250f));
    Assert.That(cfg.Bind("EWP Profiler", "RuleDetailFilter", "*").Value, Is.EqualTo("source#entry=42"));
  }

  [Test]
  public void SlowRootBufferKeepsLateHeavyBranchAndAllItsAncestors()
  {
    var root = new EwpProfiler.Frame { Span = 1, Root = true };
    var buffer = new EwpProfiler.RootTraceBuffer();
    for (var id = 2; id < 130; id++)
    {
      var item = new EwpProfiler.Frame { Span = id, Parent = root,
        ParentSpan = 1, CallParentSpan = 1, Started = 0, Kind = "search", Name = "cheap" };
      buffer.Complete(item, 5, 5, 0);
    }
    Assert.That(buffer.Snapshot(), Has.Length.EqualTo(128));
    Assert.That(buffer.Observed, Is.EqualTo(128));
    Assert.That(buffer.Omitted, Is.Zero, "Exactly 128 spans is not truncated.");

    var parent = new EwpProfiler.Frame { Span = 1000, Parent = root, ParentSpan = 1,
      CallParentSpan = 1, Started = 0, Kind = "resolve", Name = "heavy-parent" };
    var child = new EwpProfiler.Frame { Span = 1001, Parent = parent, ParentSpan = 7777,
      CallParentSpan = 1000, Started = 0, Kind = "search", Name = "heavy-child" };
    buffer.Complete(child, 50000, 50000, 0);
    buffer.Complete(parent, 70000, 20000, 0);

    var retained = buffer.Snapshot();
    Assert.That(retained, Has.Length.EqualTo(128));
    Assert.That(buffer.Observed, Is.EqualTo(130));
    Assert.That(buffer.Omitted, Is.EqualTo(2));
    var heavy = retained.Single(s => s.Id == 1001);
    Assert.That(heavy.Parent, Is.EqualTo(1000));
    Assert.That(heavy.CausalParent, Is.EqualTo(7777));
    Assert.That(retained.Any(s => s.Id == 1000), Is.True,
      "A retained child must never lose its synchronous parent.");
  }

  [Test]
  public void DeferredPokeRetainsCausalParentWithoutBreakingSynchronousTree()
  {
    var threshold = (ConfigEntry<float>)typeof(EwpProfiler)
      .GetField("SlowRootThreshold", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    threshold.Value = 0f;
    Start("deferred-tree");
    EwpProfiler.TraceToken token;
    using (EwpProfiler.Begin("rule", "origin", "origin"))
      token = EwpProfiler.CaptureTrace();
    using (EwpProfiler.Begin("tick", "pokes"))
    {
      using (EwpProfiler.BeginDeferred(token, "resolveReady"))
      {
        using (EwpProfiler.Begin("search", "chest-query")) { }
      }
    }
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);
    var rows = File.ReadAllLines(Path.Combine(directory, "deferred-tree", "ProfilerSlowRoots.tsv"))
      .Skip(1).Select(r => r.Split('\t')).ToArray();
    var tick = rows.Single(row => row[4] == "tick" && row[5] == "pokes");
    var poke = rows.Single(row => row[4] == "deferred-poke" && row[5] == "resolveReady");
    Assert.That(poke[2], Is.EqualTo(tick[1]), "Physical call parent must be the active poke tick.");
    Assert.That(poke[10], Is.EqualTo(token.ParentSpan.ToString(CultureInfo.InvariantCulture)));
    Assert.That(poke[10], Is.Not.EqualTo(poke[2]), "Deferred causal parent belongs to an earlier rule.");
    var search = rows.Single(row => row[4] == "search" && row[5] == "chest-query");
    Assert.That(search[2], Is.EqualTo(poke[1]));
  }

  [Test]
  public void SlowRootLimitKeepsTheSlowestLaterRootAndReportsLosses()
  {
    var threshold = (ConfigEntry<float>)typeof(EwpProfiler)
      .GetField("SlowRootThreshold", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    threshold.Value = 0f;
    Start("top-roots");
    for (var i = 0; i < 120; i++)
      using (EwpProfiler.Begin("event", "small-" + i)) { }
    using (EwpProfiler.Begin("event", "expensive-final"))
      System.Threading.Thread.Sleep(20);
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);
    var rows = File.ReadAllLines(Path.Combine(directory, "top-roots", "ProfilerSlowRoots.tsv"));
    Assert.That(rows, Has.Length.EqualTo(121));
    Assert.That(rows.Any(row => row.Contains("expensive-final")), Is.True);
    var summary = File.ReadAllText(Path.Combine(directory, "top-roots", "ProfilerSummary.txt"));
    Assert.That(summary, Does.Contain(
      "Slow roots qualifying: 121; retained slowest: 120; not retained by top-120 limit: 1"));
    Assert.That(summary, Does.Contain("truncated retained roots: 0"));
  }

  [Test]
  public void SlowRootWorkAccountsForChildrenOmittedByTheTreeLimit()
  {
    var threshold = (ConfigEntry<float>)typeof(EwpProfiler)
      .GetField("SlowRootThreshold", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    threshold.Value = 0f;
    Start("slow-root-work");
    using (EwpProfiler.Begin("tick", "heavy"))
    {
      for (var i = 0; i < 200; i++)
        using (EwpProfiler.Begin("search", "query-" + i, "rule-A")) { }
    }
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);

    var rootRows = File.ReadAllLines(Path.Combine(directory, "slow-root-work", "ProfilerSlowRoots.tsv"))
      .Skip(1).Select(r => r.Split('\t')).ToArray();
    Assert.That(rootRows.Count(r => r[4] == "search"), Is.LessThanOrEqualTo(128));
    var root = rootRows.Single(r => r[4] == "tick" && r[5] == "heavy");
    Assert.That(root[11], Is.EqualTo("200"));
    Assert.That(int.Parse(root[12], CultureInfo.InvariantCulture), Is.GreaterThanOrEqualTo(72));

    var rows = File.ReadAllLines(Path.Combine(directory, "slow-root-work", "ProfilerSlowRootWork.tsv"))
      .Skip(1).Select(r => r.Split('\t')).Where(r => r[0] == root[0]).ToArray();
    var category = rows.Single(r => r[1] == "kind-total" && r[2] == "search");
    Assert.That(category[5], Is.EqualTo("200"),
      "All instrumented child calls count even when only 128 fit in the tree.");
    var detailCount = rows.Where(r => r[1] == "detail" || r[1] == "detail-overflow")
      .Sum(r => long.Parse(r[5], CultureInfo.InvariantCulture));
    Assert.That(detailCount, Is.EqualTo(200));
    var self = rows.Single(r => r[1] == "root-self");
    var categoryMs = double.Parse(category[6], CultureInfo.InvariantCulture);
    var selfMs = double.Parse(self[6], CultureInfo.InvariantCulture);
    var rootMs = double.Parse(root[7], CultureInfo.InvariantCulture);
    Assert.That(categoryMs + selfMs, Is.EqualTo(rootMs).Within(0.02));
  }

  [Test]
  public void SlowRootDetailLimitStillKeepsExactKindAndOverflowTotals()
  {
    var root = new EwpProfiler.Frame { Span = 1, Root = true };
    var buffer = new EwpProfiler.RootTraceBuffer();
    for (var i = 0; i < 300; i++)
    {
      var child = new EwpProfiler.Frame { Span = i + 2, Parent = root,
        CallParentSpan = 1, ParentSpan = 1, Started = 0,
        Kind = "search", Name = "dynamic-query-" + i, Rule = "rule-X" };
      buffer.Complete(child, 30, 12, 0);
    }
    var rows = buffer.WorkSnapshot();
    Assert.That(rows.Single(r => r.Level == "kind-total" && r.Kind == "search").Totals.Calls,
      Is.EqualTo(300));
    Assert.That(rows.Single(r => r.Level == "kind-total").Totals.Exclusive, Is.EqualTo(3600));
    Assert.That(rows.Where(r => r.Level == "detail" || r.Level == "detail-overflow")
      .Sum(r => r.Totals.Calls), Is.EqualTo(300));
    Assert.That(buffer.DetailOverflowCalls, Is.EqualTo(44));
    Assert.That(buffer.Omitted, Is.EqualTo(172));
  }

  [Test]
  public void SlowEventsKeepSlowestEntriesAndSummarizeAllThresholdHits()
  {
    var threshold = (ConfigEntry<float>)typeof(EwpProfiler)
      .GetField("SlowMs", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    threshold.Value = 0f;
    Start("slow-event-top");
    for (var i = 0; i < 3000; i++)
      using (EwpProfiler.Begin("event", "fast-" + i)) { }
    using (EwpProfiler.Begin("event", "slow-after-limit"))
      System.Threading.Thread.Sleep(20);
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);

    var events = File.ReadAllLines(Path.Combine(directory, "slow-event-top", "ProfilerSlowEvents.tsv"));
    Assert.That(events, Has.Length.EqualTo(3001));
    Assert.That(events.Any(row => row.Contains("slow-after-limit")), Is.True,
      "Late slow events must displace fast early events.");
    var summary = File.ReadAllLines(Path.Combine(directory, "slow-event-top", "ProfilerSlowEventSummary.tsv"))
      .Skip(1).Select(row => row.Split('\t')).ToArray();
    var kind = summary.Single(row => row[0] == "kind-total" && row[1] == "event");
    Assert.That(kind[4], Is.EqualTo("3001"));
    Assert.That(summary.Where(row => row[0] == "detail" || row[0] == "detail-overflow")
      .Sum(row => long.Parse(row[4], CultureInfo.InvariantCulture)), Is.EqualTo(3001));
    var text = File.ReadAllText(Path.Combine(directory, "slow-event-top", "ProfilerSummary.txt"));
    Assert.That(text, Does.Contain("Slow events retained (slowest): 3000; not retained: 1; qualifying: 3001"));
  }

  [Test]
  public void TargetedRuleScopes_AreAttributedWithoutTouchingUnselectedRules()
  {
    Start("target-rule");
    var selected = new Rule { ProfilerKey = "source#entry=2|Create|Player|", ProfileDetails = true };
    var other = new Rule { ProfilerKey = "source#entry=3|Create|Player|", ProfileDetails = false };
    using (EwpProfiler.BeginRule(selected))
      using (EwpProfiler.BeginRuleDetail(selected, "condition")) { }
    using (EwpProfiler.BeginRule(other))
      using (EwpProfiler.BeginRuleDetail(other, "condition")) { }
    Assert.That(EwpProfiler.Stop("test-report"), Is.True);
    var rows = File.ReadAllLines(Path.Combine(directory, "target-rule", "ProfilerRules.tsv"))
      .Skip(1).Where(x => x.StartsWith("rule-detail\t")).ToArray();
    Assert.That(rows.Length, Is.EqualTo(1));
    Assert.That(rows[0], Does.Contain("condition"));
  }

  [Test]
  public void NewCapture_DiscardsPreviousPhaseBuckets_AndIgnoresInactiveScopes()
  {
    Start("first");
    using (EwpProfiler.Begin("tick", "spawn")) { }
    // Keep an old scope open across the boundary; it must not enter the new report.
    var old = EwpProfiler.Begin("tick", "owner");
    Assert.That(EwpProfiler.Stop("first-end"), Is.True);
    using (EwpProfiler.Begin("tick", "rpc")) { }
    Start("second");
    old.Dispose();
    using (EwpProfiler.Begin("tick", "saved-data")) { }
    Assert.That(EwpProfiler.Stop("second-end"), Is.True);
    var rows = File.ReadAllLines(Path.Combine(directory, "second", "ProfilerPhases.tsv"))
      .Skip(1).Select(line => line.Split('\t')).ToArray();
    Assert.That(rows.Length, Is.EqualTo(1));
    Assert.That(rows[0][1], Is.EqualTo("saved-data"));
    Assert.That(rows[0][2], Is.EqualTo("1"));
  }
}
