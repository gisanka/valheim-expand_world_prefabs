using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Service;

// One frame per open collection, and at most two pending synthetic events.
internal sealed class NormalizingParser(string yaml, bool measure = false, bool conservative = false) : IParser
{
  private struct Frame(bool mapping)
  {
    internal bool Mapping = mapping;
    internal bool Key = mapping;
    internal bool Shorthand;
  }

  private readonly IParser parser = new Parser(new StringReader(yaml));
  private readonly List<Frame> stack = new();
  private ParsingEvent? pendingValue, pendingEnd;
  public ParsingEvent? Current { get; private set; }
  internal long Ticks { get; private set; }

  public bool MoveNext()
  {
    if (!measure) return Advance();
    long start = Stopwatch.GetTimestamp();
    try { return Advance(); }
    finally { Ticks += Stopwatch.GetTimestamp() - start; }
  }

  private bool Advance()
  {
    if (pendingValue != null)
    {
      Current = pendingValue;
      pendingValue = null;
      return true;
    }
    if (pendingEnd != null)
    {
      Current = pendingEnd;
      pendingEnd = null;
      return true;
    }
    if (!parser.MoveNext()) { Current = null; return false; }
    var next = parser.Current;
    // Explicit tags and unusual multiline plain scalars may be restyled by
    // the legacy emitter. Keep that path until these cases are validated.
    if (conservative && (next is NodeEvent tagged && !tagged.Tag.IsEmpty ||
        next is Scalar multiline && multiline.Style == ScalarStyle.Plain && multiline.Value.IndexOf('\n') >= 0))
      throw new YamlException("Emitter-sensitive YAML event requires legacy normalization.");
    if (next is MappingEnd || next is SequenceEnd)
    {
      stack.RemoveAt(stack.Count - 1);
      Current = next;
      return true;
    }
    if (next is not NodeEvent && next is not AnchorAlias)
    {
      Current = next;
      return true;
    }

    bool wrap = false;
    if (stack.Count > 0)
    {
      int index = stack.Count - 1;
      var frame = stack[index];
      if (frame.Mapping)
      {
        if (frame.Key)
        {
          frame.Shorthand = next is Scalar key && (key.Value == "filter" || key.Value == "bannedFilter");
          if (frame.Shorthand && next is Scalar field)
            next = new Scalar(field.Anchor, field.Tag, field.Value + "s", field.Style, field.IsPlainImplicit, field.IsQuotedImplicit, field.Start, field.End, field.IsKey);
        }
        else
          wrap = frame.Shorthand && (next is AnchorAlias || next is Scalar scalar && !EmptyPlain(scalar));
        frame.Key = !frame.Key;
        stack[index] = frame;
      }
    }
    // Match the old emitter round-trip, including the string seen by custom converters.
    if (next is Scalar empty && EmptyPlain(empty))
      next = new Scalar(empty.Anchor, empty.Tag, "null", ScalarStyle.Plain, true, false, empty.Start, empty.End, empty.IsKey);
    if (next is MappingStart) stack.Add(new Frame(true));
    else if (next is SequenceStart) stack.Add(new Frame(false));
    if (wrap)
    {
      pendingValue = next;
      pendingEnd = new SequenceEnd();
      Current = new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block);
    }
    else Current = next;
    return true;
  }

  private static bool EmptyPlain(Scalar value) => value.Value.Length == 0 && value.Style == ScalarStyle.Plain && value.Tag.IsEmpty;
}
