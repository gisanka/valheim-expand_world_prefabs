using System;
using ExpandWorld.Prefab;
using NUnit.Framework;

namespace ExpandWorldPrefabs.Tests;

public class ProfilerAccountingTests
{
  [Test]
  public void NestedScopes_SubtractEachDirectChildExactlyOnce()
  {
    // Synthetic inclusive ticks for A(1000) -> B(360) -> C(140),
    // and A -> D(210). B's own child C must not be subtracted from A twice.
    var exclusiveC = EwpProfiler.ExclusiveTicks(140, 0);
    var exclusiveB = EwpProfiler.ExclusiveTicks(360, 140);
    var exclusiveD = EwpProfiler.ExclusiveTicks(210, 0);
    var exclusiveA = EwpProfiler.ExclusiveTicks(1000, 360 + 210);

    Assert.That(exclusiveA, Is.EqualTo(430));
    Assert.That(exclusiveB, Is.EqualTo(220));
    Assert.That(exclusiveC, Is.EqualTo(140));
    Assert.That(exclusiveD, Is.EqualTo(210));
    Assert.That(exclusiveA + exclusiveB + exclusiveC + exclusiveD, Is.EqualTo(1000));
  }

  [Test]
  public void ExclusiveTime_NeverBecomesNegative()
  {
    Assert.That(EwpProfiler.ExclusiveTicks(100, 120), Is.Zero);
  }

  [Test]
  public void DeferredOrigin_RequiresMatchingCaptureGeneration()
  {
    var scheduled = new EwpProfiler.TraceToken(41, 7, "original-origin", "original-rule", 3);
    Assert.That(EwpProfiler.CanRestoreTrace(scheduled, 3), Is.True);
    Assert.That(EwpProfiler.CanRestoreTrace(scheduled, 4), Is.False);
    Assert.That(EwpProfiler.CanRestoreTrace(default, 3), Is.False);
  }

  [Test]
  public void UsingScope_ClosesOnException()
  {
    var disposeCalls = 0;
    try
    {
      using var scope = new CountingScope(() => disposeCalls++);
      throw new InvalidOperationException("synthetic workload failure");
    }
    catch (InvalidOperationException) { }
    Assert.That(disposeCalls, Is.EqualTo(1));
  }

  private sealed class CountingScope : IDisposable
  {
    private readonly Action onDispose;
    public CountingScope(Action onDispose) { this.onDispose = onDispose; }
    public void Dispose() => onDispose();
  }
}
