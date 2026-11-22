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
