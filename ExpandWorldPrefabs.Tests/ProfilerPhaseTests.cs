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
  }

  [TearDown]
  public void Cleanup()
  {
    EwpProfiler.Stop("test-cleanup");
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

      var sourceRows = File.ReadAllLines(Path.Combine(directory, "sources", "ProfilerRuleSources.tsv"))
        .Skip(1).Select(line => line.Split('\t')).ToArray();
      Assert.That(sourceRows.Length, Is.EqualTo(2));
      Assert.That(sourceRows.Select(row => row[6]), Is.EquivalentTo(new[] { "hash-A", "hash-B" }));
      Assert.That(sourceRows.Select(row => row[3]), Is.All.EqualTo("5"));
      Assert.That(sourceRows.Select(row => row[4]), Is.EquivalentTo(new[] { "42", "45" }));
      var summary = File.ReadAllText(Path.Combine(directory, "sources", "ProfilerSummary.txt"));
      Assert.That(summary, Does.Contain("Successful rule-source changes during this segment: 1"));
    }
    finally
    {
      EwpProfiler.SetLoadedRuleSources(Array.Empty<EwpProfiler.RuleSource>());
    }
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
