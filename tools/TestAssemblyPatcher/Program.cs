using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Patch only the test output, without loading Unity dependencies or rewriting metadata.
if (args.Length != 1)
  throw new ArgumentException("Expected the test output assembly_valheim.dll path.");

var path = Path.GetFullPath(args[0]);
Require(Path.GetFileName(path) == "assembly_valheim.dll" &&
  File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "ExpandWorldPrefabs.Tests.dll")),
  "Expected assembly_valheim.dll beside the compiled test assembly.");
var image = File.ReadAllBytes(path);
using var pe = new PEReader(new MemoryStream(image, writable: false));
var md = pe.GetMetadataReader();
Require(md.GetString(md.GetAssemblyDefinition().Name) == "assembly_valheim", "Unexpected assembly name.");
Require(pe.PEHeaders.CorHeader is { } cor && (cor.Flags & CorFlags.StrongNameSigned) == 0,
  "Cannot patch a strong-name signed assembly.");

var zdo = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t =>
  md.GetString(t.Name) == "ZDO" && md.GetString(t.Namespace) == "");
var cctor = zdo.GetMethods().Select(md.GetMethodDefinition).Single(m =>
  md.GetString(m.Name) == ".cctor" && (m.Attributes & MethodAttributes.Static) != 0);
var fieldHandle = zdo.GetFields().Single(h => md.GetString(md.GetFieldDefinition(h).Name) == "c_quaternionIdentity");
var field = md.GetFieldDefinition(fieldHandle);
Require((field.Attributes & FieldAttributes.Static) != 0, "Expected a static quaternion identity field.");
var vector = ReadValueType(md.GetBlobReader(field.Signature), 0x06, false);
Require(IsUnityType(md, vector, "Vector3"), "Unexpected quaternion identity field type.");

var body = pe.GetMethodBody(cctor.RelativeVirtualAddress);
var il = body.GetILBytes() ?? throw new InvalidOperationException("Missing ZDO initializer IL.");
const int prefixLength = 18;
var delegates = new[] { "WriteFloat", "WriteVec3", "WriteQuaternion", "WriteInt", "WriteLong", "WriteString", "WriteByteArray" };
// Check the entire straight-line initializer shape, including the untouched delegate stores.
Require(il.Length == prefixLength + delegates.Length * 17 + 1 && il[^1] == 0x2a &&
  body.ExceptionRegions.Length == 0 && body.MaxStack >= 2, "Unexpected ZDO initializer body.");
for (var i = 0; i < delegates.Length; i++)
{
  var p = prefixLength + i * 17;
  Require(il[p] == 0x14 && il[p + 1] == 0xfe && il[p + 2] == 0x06 &&
    il[p + 7] == 0x73 && il[p + 12] == 0x80 &&
    zdo.GetFields().Any(h => MetadataTokens.GetToken(h) == Token(il, p + 13) &&
      md.GetString(md.GetFieldDefinition(h).Name) == delegates[i]), "Unexpected ZDO delegate initialization.");
}

// ldsflda c_quaternionIdentity; initobj Vector3; nop padding (same length).
var replacement = new byte[prefixLength];
replacement[0] = 0x7f;
BinaryPrimitives.WriteInt32LittleEndian(replacement.AsSpan(1, 4), MetadataTokens.GetToken(fieldHandle));
replacement[5] = 0xfe;
replacement[6] = 0x15;
BinaryPrimitives.WriteInt32LittleEndian(replacement.AsSpan(7, 4), MetadataTokens.GetToken(vector));
if (il.AsSpan(0, prefixLength).SequenceEqual(replacement))
{
  Console.WriteLine("Test-only ZDO initializer already prepared.");
  return;
}
Require(il[0] == 0x28 && il[5] == 0x0a && il[6] == 0x12 && il[7] == 0 &&
  il[8] == 0x28 && il[13] == 0x80 && Token(il, 14) == MetadataTokens.GetToken(fieldHandle),
  "Unexpected ZDO quaternion initializer; review the test-only patch.");
var identity = ReadCall(md, Token(il, 1), "get_identity", 0x00);
var euler = ReadCall(md, Token(il, 9), "get_eulerAngles", 0x20);
Require(identity.Parent == euler.Parent && identity.Result == identity.Parent && euler.Result == vector,
  "Unexpected quaternion getter signatures.");

// Translate the method RVA to a raw file offset. Verify the bytes before editing.
var rva = cctor.RelativeVirtualAddress;
var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress &&
  (long)rva - s.VirtualAddress < s.SizeOfRawData);
var methodOffset = checked(section.PointerToRawData + rva - section.VirtualAddress);
var header = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(methodOffset, 2));
Require((header & 3) == 3 && (header >> 12) >= 3, "Expected a fat method header.");
var offset = checked(methodOffset + (header >> 12) * 4);
Require((long)offset + il.Length <= (long)section.PointerToRawData + section.SizeOfRawData &&
  image.AsSpan(offset, il.Length).SequenceEqual(il), "Method file bytes do not match metadata IL.");
replacement.CopyTo(image.AsSpan(offset, prefixLength));

var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
  File.WriteAllBytes(temporary, image);
  // Replace the directory entry, not a possibly hard-linked original file.
  File.Move(temporary, path, true);
}
finally
{
  if (File.Exists(temporary)) File.Delete(temporary);
}
Console.WriteLine("Prepared test-only ZDO initializer in " + path);

static void Require(bool condition, string message)
{
  if (!condition) throw new InvalidOperationException(message);
}

static int Token(byte[] il, int offset) => BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));

static EntityHandle ReadValueType(BlobReader signature, byte header, bool method)
{
  Require(signature.ReadByte() == header && (!method || signature.ReadCompressedInteger() == 0) &&
    signature.ReadByte() == 0x11, "Unexpected value type signature.");
  var type = signature.ReadTypeHandle();
  Require(!type.IsNil && signature.RemainingBytes == 0, "Unexpected trailing signature data.");
  return type;
}

static bool IsUnityType(MetadataReader md, EntityHandle handle, string name)
{
  if (handle.Kind != HandleKind.TypeReference) return false;
  var type = md.GetTypeReference((TypeReferenceHandle)handle);
  return md.GetString(type.Name) == name && md.GetString(type.Namespace) == "UnityEngine" &&
    type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
    md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "UnityEngine.CoreModule";
}

static (EntityHandle Parent, EntityHandle Result) ReadCall(MetadataReader md, int token, string name, byte header)
{
  var handle = MetadataTokens.EntityHandle(token);
  Require(handle.Kind == HandleKind.MemberReference, "Expected a quaternion member reference.");
  var member = md.GetMemberReference((MemberReferenceHandle)handle);
  Require(IsUnityType(md, member.Parent, "Quaternion") && md.GetString(member.Name) == name,
    "Unexpected quaternion member reference.");
  return (member.Parent, ReadValueType(md.GetBlobReader(member.Signature), header, true));
}
