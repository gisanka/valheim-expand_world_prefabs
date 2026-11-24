using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Service;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using System.IO;
using System.Collections.Generic;

namespace ExpandWorldPrefabs.Tests;

public class StreamingYamlTests
{
  [TestCase("filter: x")]
  [TestCase("bannedFilter: [a, b]")]
  [TestCase("filter: {a: b}")]
  [TestCase("'filter': \"\"")]
  [TestCase("filter:")]
  [TestCase("filter: null")]
  [TestCase("filter: !!str null")]
  [TestCase("filter: []")]
  [TestCase("filter: |\n  filter: text\n  second line")]
  [TestCase("a: &anchor test\nfilter: *anchor")]
  [TestCase("a: &anchor [x, y]\nfilter: *anchor")]
  [TestCase("a: &anchor {filter: x}\nfilter: *anchor")]
  [TestCase("{filter: x, nested: [{bannedFilter: y}], text: filter}")]
  [TestCase("? [filter, x]\n: {filter: y}")]
  [TestCase("a: 'filter'\nfilters: [filter, bannedFilter]")]
  public void EventsMatchReference(string yaml)
  {
    var legacy = new Parser(new StringReader(FilterShorthand.Normalize(yaml)));
    var streaming = new NormalizingParser(yaml);
    Assert.That(Events(streaming), Is.EqualTo(Events(legacy)));
  }

  // Compare semantic event content, not regenerated physical marks or collection style.
  private static List<string> Events(IParser parser)
  {
    var result = new List<string>();
    while (parser.MoveNext())
    {
      var ev = parser.Current!;
      string node = ev is NodeEvent n ? $"/{n.Anchor}/{n.Tag}" : "";
      string value = ev is Scalar s ? $"/{s.Value}/{s.Style}/{s.IsPlainImplicit}/{s.IsQuotedImplicit}/{s.IsKey}" :
        ev is AnchorAlias a ? $"/{a.Value}" : "";
      result.Add(ev.GetType().Name + node + value);
    }
    return result;
  }

  [Test]
  public void OrdinaryRulesUseStreamingPathWithoutFallback()
  {
    const string yaml = "- prefab: Player\n  filter: x\n  bannedFilter: [a, b]\n";
    Assert.That(Yaml.ParseMixed("direct.yaml", yaml, true, out var result, out var error, out var cacheable), Is.True, error);
    Assert.That(cacheable, Is.True);
    Assert.That(result.ScriptEntries[0].filters, Is.EqualTo(new[] { "x" }));
  }

  [Test]
  public void EmitterSensitiveTagsUseReferencePipeline()
  {
    const string yaml = "- prefab: Player\n  filter: !!str null\n";
    bool old = Yaml.ParseMixed("tagged.yaml", yaml, false, out var expected, out var oldError);
    bool current = Yaml.ParseMixed("tagged.yaml", yaml, true, out var actual, out var newError, out var cacheable);
    Assert.That(current, Is.EqualTo(old));
    Assert.That(newError, Is.EqualTo(oldError));
    Assert.That(cacheable, Is.False);
    DtoAssert.Equal(expected, actual);
  }

  [TestCase("- prefab: Player\n  filter: x\n  bannedFilter: [a, b]\n")]
  [TestCase("- prefab: Player\n  filter:\n  bannedFilter: ''\n")]
  [TestCase("- prefab: Player\n  command: 'filter: text'\n  objects: [{prefab: x, filter: y}]\n")]
  [TestCase("- prefab: Player\n  data: |\n    ints: [a, 1]\n  spawn: [{prefab: x, data: {ints: [b, 2]}}]\n- name: group\n  values: [x, y]\n")]
  [TestCase("- prefab: Player\n  log: [{log: [a, b], logFile: x}]\n  poke: [{prefab: x, filter: &f y}, {prefab: y, bannedFilter: *f}]\n")]
  [TestCase("- prefab: Player\n  filter: [broken\n")]
  public void MixedPipelineMatchesReference(string yaml)
  {
    bool old = Yaml.ParseMixed("streaming-test.yaml", yaml, false, out var expected, out var oldError);
    bool current = Yaml.ParseMixed("streaming-test.yaml", yaml, true, out var actual, out var newError);
    Assert.That(current, Is.EqualTo(old));
    Assert.That(newError, Is.EqualTo(oldError));
    DtoAssert.Equal(expected, actual);
  }
}

internal static class DtoAssert
{
  internal static void Equal(object? expected, object? actual) => Equal(expected, actual,
    new Dictionary<object, object>(ReferenceEqualityComparer.Instance), new HashSet<object>(ReferenceEqualityComparer.Instance));

  private static void Equal(object? expected, object? actual, Dictionary<object, object> seen, HashSet<object> actualSeen)
  {
    if (expected == null) { Assert.That(actual, Is.Null); return; }
    Assert.That(actual, Is.Not.Null);
    var type = expected.GetType();
    Assert.That(actual!.GetType(), Is.EqualTo(type));
    if (type.IsValueType || expected is string) { Assert.That(actual, Is.EqualTo(expected)); return; }
    if (seen.TryGetValue(expected, out var previous)) { Assert.That(actual, Is.SameAs(previous)); return; }
    Assert.That(actualSeen.Add(actual), Is.True, "Unexpected shared mutable reference.");
    seen.Add(expected, actual);
    if (expected is IDictionary map)
    {
      var other = (IDictionary)actual;
      Assert.That(other.Count, Is.EqualTo(map.Count));
      var enumerator = other.GetEnumerator();
      foreach (DictionaryEntry pair in map)
      {
        Assert.That(enumerator.MoveNext(), Is.True);
        Equal(pair.Key, enumerator.Key, seen, actualSeen);
        Equal(pair.Value, enumerator.Value, seen, actualSeen);
      }
      return;
    }
    if (expected is IList list)
    {
      var other = (IList)actual;
      Assert.That(other.Count, Is.EqualTo(list.Count));
      for (int i = 0; i < list.Count; i++) Equal(list[i], other[i], seen, actualSeen);
      return;
    }
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
      Equal(field.GetValue(expected), field.GetValue(actual), seen, actualSeen);
  }
}
