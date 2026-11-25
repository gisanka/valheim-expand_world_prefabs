using Mono.Cecil;
using Mono.Cecil.Cil;

// The NUnit host cannot execute Unity's native quaternion-to-Euler conversion.
// Rewrite only the copy in the test output, never the game or reference assembly.
if (args.Length != 1)
  throw new ArgumentException("Expected the test output assembly_valheim.dll path.");

var path = Path.GetFullPath(args[0]);
using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
var zdo = assembly.MainModule.GetType("ZDO") ?? throw new InvalidOperationException("ZDO type not found.");
var cctor = zdo.Methods.SingleOrDefault(m => m.IsConstructor && m.IsStatic);
if (cctor == null || !cctor.HasBody)
  throw new InvalidOperationException("ZDO static constructor not found.");

var instructions = cctor.Body.Instructions;
bool IsQuaternionCall(Instruction instruction, string name, string result, bool instance) =>
  instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference method &&
  method.DeclaringType.FullName == "UnityEngine.Quaternion" && method.Name == name &&
  method.ReturnType.FullName == result && method.HasThis == instance && method.Parameters.Count == 0;

// Older versions and an already prepared copy have no such native call.
if (!instructions.Any(i => i.Operand is MethodReference method &&
    method.DeclaringType.FullName == "UnityEngine.Quaternion" && method.Name == "get_eulerAngles"))
{
  Console.WriteLine("ZDO initializer does not need the test-only quaternion substitution.");
  return;
}

// Fail closed if a game update changes the known initializer shape.
// Keep all subsequent static initialization (including the seven write delegates).
if (instructions.Count < 6 ||
    !IsQuaternionCall(instructions[0], "get_identity", "UnityEngine.Quaternion", false) ||
    instructions[1].OpCode != OpCodes.Stloc_0 ||
    instructions[2].OpCode != OpCodes.Ldloca_S ||
    instructions[2].Operand is not VariableDefinition local || local.Index != 0 ||
    !IsQuaternionCall(instructions[3], "get_eulerAngles", "UnityEngine.Vector3", true) ||
    instructions[4].OpCode != OpCodes.Stsfld ||
    instructions[4].Operand is not FieldDefinition field ||
    field.DeclaringType != zdo || !field.IsStatic ||
    field.Name != "c_quaternionIdentity" || field.FieldType.FullName != "UnityEngine.Vector3")
  throw new InvalidOperationException("Unexpected ZDO quaternion initializer; review the test-only patch.");

var replaced = instructions.Take(5).ToHashSet();
if (cctor.Body.HasExceptionHandlers || instructions.Any(i =>
    i.Operand is Instruction target && replaced.Contains(target) ||
    i.Operand is Instruction[] targets && targets.Any(replaced.Contains)))
  throw new InvalidOperationException("Unexpected control flow into the ZDO quaternion initializer.");

// Quaternion.identity.eulerAngles is exactly Vector3.zero.
instructions[0].OpCode = OpCodes.Ldsflda;
instructions[0].Operand = field;
instructions[1].OpCode = OpCodes.Initobj;
instructions[1].Operand = field.FieldType;
for (var i = 2; i < 5; ++i)
{
  instructions[i].OpCode = OpCodes.Nop;
  instructions[i].Operand = null;
}

var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
  assembly.Write(temporary);
  // Replace the directory entry rather than modifying a possibly hard-linked copy.
  File.Move(temporary, path, true);
}
finally
{
  if (File.Exists(temporary))
    File.Delete(temporary);
}
Console.WriteLine("Prepared test-only ZDO initializer in " + path);
