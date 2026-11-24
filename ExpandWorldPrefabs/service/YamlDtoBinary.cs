using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Data;
using ExpandWorld.Prefab;

namespace Service;

// Only this closed set of DTOs can be constructed. No type names are read from disk.
// Field order is deterministic; the outer cache invalidates on any assembly rebuild.
internal static class YamlDtoBinary
{
  internal const int MaxBytes = 64 * 1024 * 1024;
  private const int MaxItems = 1000000;
  private const int MaxDepth = 128;
  private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
  private static readonly Dictionary<Type, FieldInfo[]> Fields = new[]
  {
    typeof(Yaml.MixedFileEntries), typeof(RuleYaml), typeof(DataYaml), typeof(ItemYaml), typeof(ConfigYaml),
    typeof(SpawnYaml), typeof(ObjectYaml), typeof(PokeYaml), typeof(ExecYaml), typeof(TerrainYaml)
  }.ToDictionary(t => t, MutableFields);

  // Fixed tags for values produced by the flexible log converter. Unknown values
  // or collection implementations make a file ineligible, not a lossy cache entry.
  private static readonly Type[] DynamicTypes =
  [typeof(string), typeof(List<object>), typeof(Dictionary<object, object>), typeof(bool), typeof(int),
    typeof(long), typeof(float), typeof(double), typeof(decimal), typeof(uint), typeof(ulong)];

  private static readonly HashSet<Type> Lists = new()
  {
    typeof(List<RuleYaml>), typeof(List<DataYaml>), typeof(List<ConfigYaml>), typeof(List<Yaml.SourceSpan>), typeof(List<object>)
  };
  private static readonly HashSet<Type> Dictionaries = new() { typeof(Dictionary<string, string>), typeof(Dictionary<object, object>) };
  private static readonly HashSet<Type> Arrays = new()
  {
    typeof(string[]), typeof(SpawnYaml[]), typeof(ObjectYaml[]), typeof(PokeYaml[]), typeof(ExecYaml[]),
    typeof(TerrainYaml[]), typeof(ItemYaml[]), typeof(Dictionary<string, string>[])
  };

  static YamlDtoBinary()
  {
    CheckFields(typeof(StringList), ("Items", typeof(string[])));
    CheckFields(typeof(SpawnEntries), ("Data", typeof(SpawnYaml[])));
    CheckFields(typeof(ObjectEntries), ("Data", typeof(ObjectYaml[])));
    CheckFields(typeof(ExecEntries), ("Data", typeof(ExecYaml[])));
    CheckFields(typeof(DataField), ("Name", typeof(string)), ("Inline", typeof(DataYaml)));
    CheckFields(typeof(RuleLogData), ("Value", typeof(object)));
    CheckFields(typeof(RuleLogFiles), ("Value", typeof(object)));
    CheckFields(typeof(Yaml.SourceSpan), ("<YamlItem>k__BackingField", typeof(int)),
      ("<StartLine>k__BackingField", typeof(int)), ("<EndLine>k__BackingField", typeof(int)));
  }

  private static FieldInfo[] MutableFields(Type type)
  {
    var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (fields.Any(f => !f.IsPublic || f.IsInitOnly)) throw Invalid("unreviewed DTO field layout: " + type.Name);
    return fields.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
  }

  private static void CheckFields(Type type, params (string Name, Type Type)[] expected)
  {
    var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (fields.Length != expected.Length || expected.Any(e => !fields.Any(f => f.Name == e.Name && f.FieldType == e.Type)))
      throw Invalid("unreviewed DTO field layout: " + type.Name);
  }

  private sealed class ReferenceComparer : IEqualityComparer<object>
  {
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
  }

  internal static byte[] Encode(Yaml.MixedFileEntries entries)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Utf8, true);
    new Encoder(writer).Value(typeof(Yaml.MixedFileEntries), entries, 0);
    writer.Flush();
    if (stream.Length > MaxBytes) throw Invalid("payload too large");
    return stream.ToArray();
  }

  internal static Yaml.MixedFileEntries Decode(byte[] bytes)
  {
    if (bytes.Length > MaxBytes) throw Invalid("payload too large");
    using var stream = new MemoryStream(bytes, false);
    using var reader = new BinaryReader(stream, Utf8, true);
    var result = (Yaml.MixedFileEntries?)new Decoder(reader).Value(typeof(Yaml.MixedFileEntries), 0);
    if (result == null || stream.Position != stream.Length || result.ScriptEntries == null || result.DataEntries == null ||
        result.ConfigEntries == null || result.ScriptSources == null || result.ContentHash == null ||
        (result.ScriptSources.Count != 0 && result.ScriptSources.Count != result.ScriptEntries.Count))
      throw Invalid("invalid DTO root");
    return result;
  }

  private static InvalidDataException Invalid(string reason) => new("YAML cache: " + reason);
  private static void CheckDepth(int depth) { if (depth > MaxDepth) throw Invalid("nesting limit"); }
  private static void CheckCount(int count) { if (count < 0 || count > MaxItems) throw Invalid("collection limit"); }
  private static bool Wrapper(Type t) => t == typeof(StringList) || t == typeof(SpawnEntries) || t == typeof(ObjectEntries) ||
    t == typeof(ExecEntries) || t == typeof(DataField) || t == typeof(RuleLogData) || t == typeof(RuleLogFiles);
  private static bool ReferenceType(Type t) => Fields.ContainsKey(t) || Wrapper(t) || Arrays.Contains(t) || Lists.Contains(t) || Dictionaries.Contains(t);

  private sealed class Encoder(BinaryWriter writer)
  {
    private readonly Dictionary<object, int> references = new(new ReferenceComparer());
    private readonly HashSet<object> active = new(new ReferenceComparer());
    private int nodes;

    internal void Value(Type type, object? value, int depth)
    {
      CheckDepth(depth);
      if (++nodes > MaxItems || writer.BaseStream.Length > MaxBytes) throw Invalid("object budget");
      if (type == typeof(object))
      {
        if (value == null) { writer.Write((byte)0); return; }
        int tag = Array.IndexOf(DynamicTypes, value.GetType());
        if (tag < 0) throw Invalid("unsupported flexible value");
        writer.Write((byte)(tag + 1));
        Value(DynamicTypes[tag], value, depth + 1);
        return;
      }
      var underlying = Nullable.GetUnderlyingType(type);
      if (underlying != null)
      {
        writer.Write(value != null);
        if (value != null) Value(underlying, value, depth + 1);
        return;
      }
      if (type == typeof(string))
      {
        writer.Write(value != null);
        if (value is string text)
        {
          int length = Utf8.GetByteCount(text);
          if (length > MaxBytes - writer.BaseStream.Length - 4) throw Invalid("string limit");
          writer.Write(length);
          writer.Write(Utf8.GetBytes(text));
        }
        return;
      }
      if (type == typeof(bool)) { writer.Write((bool)value!); return; }
      if (type == typeof(int)) { writer.Write((int)value!); return; }
      if (type == typeof(uint)) { writer.Write((uint)value!); return; }
      if (type == typeof(long)) { writer.Write((long)value!); return; }
      if (type == typeof(ulong)) { writer.Write((ulong)value!); return; }
      if (type == typeof(float)) { writer.Write((float)value!); return; }
      if (type == typeof(double)) { writer.Write((double)value!); return; }
      if (type == typeof(decimal)) { writer.Write((decimal)value!); return; }
      if (type == typeof(Yaml.SourceSpan))
      {
        var span = (Yaml.SourceSpan)value!;
        writer.Write(span.YamlItem); writer.Write(span.StartLine); writer.Write(span.EndLine);
        return;
      }
      if (!ReferenceType(type)) throw Invalid("unsupported DTO field type");
      if (value == null) { writer.Write(0); return; }
      if (value.GetType() != type) throw Invalid("unexpected DTO subtype");
      if (references.TryGetValue(value, out int id))
      {
        if (active.Contains(value)) throw Invalid("cyclic DTO graph");
        writer.Write(-id);
        return;
      }
      id = references.Count + 1;
      references.Add(value, id);
      active.Add(value);
      writer.Write(id);
      int d = depth + 1;
      if (value is StringList strings) Value(typeof(string[]), strings.Items, d);
      else if (value is SpawnEntries spawns) Value(typeof(SpawnYaml[]), spawns.Data, d);
      else if (value is ObjectEntries objects) Value(typeof(ObjectYaml[]), objects.Data, d);
      else if (value is ExecEntries execs) Value(typeof(ExecYaml[]), execs.Data, d);
      else if (value is DataField data) { Value(typeof(string), data.Name, d); Value(typeof(DataYaml), data.Inline, d); }
      else if (value is RuleLogData log) Value(typeof(object), log.Value, d);
      else if (value is RuleLogFiles files) Value(typeof(object), files.Value, d);
      else if (value is IDictionary map && Dictionaries.Contains(type))
      {
        if (value is Dictionary<string, string> sm && !ReferenceEquals(sm.Comparer, EqualityComparer<string>.Default) ||
            value is Dictionary<object, object> om && !ReferenceEquals(om.Comparer, EqualityComparer<object>.Default))
          throw Invalid("unsupported dictionary comparer");
        CheckCount(map.Count); writer.Write(map.Count);
        var args = type.GetGenericArguments();
        foreach (DictionaryEntry pair in map) { Value(args[0], pair.Key, d); Value(args[1], pair.Value, d); }
      }
      else if (value is IList list && (Arrays.Contains(type) || Lists.Contains(type)))
      {
        CheckCount(list.Count); writer.Write(list.Count);
        var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
        foreach (var item in list) Value(element, item, d);
      }
      else if (Fields.TryGetValue(type, out var fields))
        foreach (var field in fields) Value(field.FieldType, field.GetValue(value), d);
      else throw Invalid("unsupported DTO");
      active.Remove(value);
    }
  }

  private sealed class Decoder(BinaryReader reader)
  {
    private readonly Dictionary<int, object> references = new();
    private int nextId, nodes;
    private long Remaining => reader.BaseStream.Length - reader.BaseStream.Position;
    private int Count()
    {
      int n = reader.ReadInt32(); CheckCount(n);
      if (n > Remaining || n > MaxItems - nodes) throw Invalid("collection exceeds payload or object budget");
      return n;
    }
    private bool Flag()
    {
      byte flag = reader.ReadByte();
      if (flag > 1) throw Invalid("invalid boolean");
      return flag != 0;
    }

    internal object? Value(Type type, int depth)
    {
      CheckDepth(depth);
      if (++nodes > MaxItems) throw Invalid("object budget");
      int d = depth + 1;
      if (type == typeof(object))
      {
        int tag = reader.ReadByte();
        if (tag == 0) return null;
        if (tag > DynamicTypes.Length) throw Invalid("unknown flexible tag");
        return Value(DynamicTypes[tag - 1], d);
      }
      var underlying = Nullable.GetUnderlyingType(type);
      if (underlying != null) return Flag() ? Value(underlying, d) : null;
      if (type == typeof(string))
      {
        if (!Flag()) return null;
        int length = reader.ReadInt32();
        if (length < 0 || length > Remaining || length > MaxBytes) throw Invalid("invalid string length");
        return Utf8.GetString(reader.ReadBytes(length));
      }
      if (type == typeof(bool)) return Flag();
      if (type == typeof(int)) return reader.ReadInt32();
      if (type == typeof(uint)) return reader.ReadUInt32();
      if (type == typeof(long)) return reader.ReadInt64();
      if (type == typeof(ulong)) return reader.ReadUInt64();
      if (type == typeof(float)) return reader.ReadSingle();
      if (type == typeof(double)) return reader.ReadDouble();
      if (type == typeof(decimal)) return reader.ReadDecimal();
      if (type == typeof(Yaml.SourceSpan))
      {
        int item = reader.ReadInt32(), start = reader.ReadInt32(), end = reader.ReadInt32();
        if (item <= 0 || start <= 0 || end < start) throw Invalid("invalid source span");
        return new Yaml.SourceSpan(item, start, end);
      }
      if (!ReferenceType(type)) throw Invalid("unsupported DTO field type");
      int id = reader.ReadInt32();
      if (id == 0) return null;
      if (id < 0)
      {
        if (id == int.MinValue || !references.TryGetValue(-id, out var previous) || previous.GetType() != type)
          throw Invalid("invalid reference");
        return previous;
      }
      if (id != ++nextId) throw Invalid("invalid reference sequence");
      object result;
      if (type == typeof(StringList)) result = new StringList((string[])Value(typeof(string[]), d)!);
      else if (type == typeof(SpawnEntries)) result = new SpawnEntries((SpawnYaml[])Value(typeof(SpawnYaml[]), d)!);
      else if (type == typeof(ObjectEntries)) result = new ObjectEntries((ObjectYaml[])Value(typeof(ObjectYaml[]), d)!);
      else if (type == typeof(ExecEntries)) result = new ExecEntries((ExecYaml[])Value(typeof(ExecYaml[]), d)!);
      else if (type == typeof(DataField)) result = new DataField((string?)Value(typeof(string), d), (DataYaml?)Value(typeof(DataYaml), d));
      else if (type == typeof(RuleLogData)) result = new RuleLogData(Value(typeof(object), d));
      else if (type == typeof(RuleLogFiles)) result = new RuleLogFiles(Value(typeof(object), d));
      else if (Dictionaries.Contains(type))
      {
        int n = Count(); var args = type.GetGenericArguments();
        var map = (IDictionary)Activator.CreateInstance(type)!;
        for (int i = 0; i < n; i++) map.Add(Value(args[0], d) ?? throw Invalid("null dictionary key"), Value(args[1], d));
        result = map;
      }
      else if (Arrays.Contains(type))
      {
        int n = Count(); var element = type.GetElementType()!;
        var array = Array.CreateInstance(element, n);
        for (int i = 0; i < n; i++) array.SetValue(Value(element, d), i);
        result = array;
      }
      else if (Lists.Contains(type))
      {
        int n = Count(); var element = type.GetGenericArguments()[0];
        var list = (IList)Activator.CreateInstance(type)!;
        for (int i = 0; i < n; i++) list.Add(Value(element, d));
        result = list;
      }
      else if (Fields.TryGetValue(type, out var fields))
      {
        result = Activator.CreateInstance(type)!;
        foreach (var field in fields) field.SetValue(result, Value(field.FieldType, d));
      }
      else throw Invalid("unsupported DTO");
      references.Add(id, result);
      return result;
    }
  }
}
