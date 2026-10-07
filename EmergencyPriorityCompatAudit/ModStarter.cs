using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Timberborn.ModManagerScene;

namespace EmergencyPriorityCompatAudit;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        Runtime.Initialize(modEnvironment.ModPath);
        var harmony = new Harmony("tharbad08.EmergencyPriorityCompatAudit");

        var assembly = EmergencyPriorityLocator.FindOriginalAssembly();
        if (assembly is null)
        {
            Runtime.Log("ERROR: original grantemsley.EmergencyPriority assembly not found. Install/enable the Workshop mod.");
            return;
        }

        Runtime.Log($"original assembly: {assembly.FullName}");
        EmergencyPriorityGuard.Install(harmony, assembly);
        AssemblyAudit.Run(assembly);
    }
}

internal static class Runtime
{
    private static string _modPath = "";
    private static readonly object Gate = new();

    public static string AuditPath => Path.Combine(_modPath, "emergency-priority-compat-audit.log");

    public static void Initialize(string modPath)
    {
        _modPath = modPath;
        try { File.WriteAllText(AuditPath, ""); } catch { }
    }

    public static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(AuditPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

internal static class EmergencyPriorityLocator
{
    public static Assembly? FindOriginalAssembly()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetName().Name?.Contains("EmergencyPriority", StringComparison.OrdinalIgnoreCase) == true &&
                    assembly.GetType("grantemsley.EmergencyPriority.EmergencyInterruptionService", false) is not null)
                {
                    return assembly;
                }
            }
            catch { }
        }

        return null;
    }
}

internal static class EmergencyPriorityGuard
{
    private static MethodInfo? _tryInterrupt;

    public static void Install(Harmony harmony, Assembly assembly)
    {
        var serviceType = assembly.GetType("grantemsley.EmergencyPriority.EmergencyInterruptionService", false);
        if (serviceType is null)
        {
            Runtime.Log("ERROR: EmergencyInterruptionService missing.");
            return;
        }

        _tryInterrupt = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "TryInterrupt" && m.GetParameters().Length == 1);

        var onJobRegistered = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "OnJobRegistered" && m.GetParameters().Length == 1);

        if (_tryInterrupt is null || onJobRegistered is null)
        {
            Runtime.Log("ERROR: expected interruption methods not found.");
            return;
        }

        harmony.Patch(onJobRegistered,
            transpiler: new HarmonyMethod(typeof(EmergencyPriorityGuard), nameof(TranspileOnJobRegistered))
            {
                priority = Priority.First
            });

        Runtime.Log("compat guard installed on EmergencyInterruptionService.OnJobRegistered.");
    }

    private static IEnumerable<CodeInstruction> TranspileOnJobRegistered(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(EmergencyPriorityGuard), nameof(SafeSkipInterrupt));
        var rewritten = new List<CodeInstruction>();
        var replaced = 0;

        foreach (var instruction in instructions)
        {
            if (_tryInterrupt is not null &&
                (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo called &&
                called.Module == _tryInterrupt.Module &&
                called.MetadataToken == _tryInterrupt.MetadataToken)
            {
                var safe = new CodeInstruction(OpCodes.Call, replacement);
                safe.labels.AddRange(instruction.labels);
                safe.blocks.AddRange(instruction.blocks);
                rewritten.Add(safe);
                replaced++;
            }
            else
            {
                rewritten.Add(instruction);
            }
        }

        Runtime.Log($"OnJobRegistered transpiler replaced {replaced} TryInterrupt call(s).");
        return rewritten;
    }

    private static void SafeSkipInterrupt(object service, object worker)
    {
        // Intentionally empty.
        // Timberborn 1.1 removed/changed the carrying API used by the original mod's
        // immediate interruption logic. Skipping only this step is safer than executing
        // malformed IL or guessing how to mutate Worker/GoodCarrier state.
    }
}

internal static class AssemblyAudit
{
    private sealed record Problem(string Kind, string Owner, int Offset, string Operand, string Error);

    public static void Run(Assembly assembly)
    {
        Runtime.Log("=== FULL ORIGINAL ASSEMBLY AUDIT BEGIN ===");
        var problems = new List<Problem>();
        var typeCount = 0;
        var methodCount = 0;

        foreach (var type in GetTypesSafe(assembly).OrderBy(t => t.FullName))
        {
            typeCount++;
            Runtime.Log($"TYPE {type.FullName}");

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Runtime.Log($"  FIELD {field.FieldType.FullName} {field.Name}");

            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Runtime.Log($"  PROP {prop.PropertyType.FullName} {prop.Name}");

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                       .Cast<MethodBase>()
                                       .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
            {
                methodCount++;
                Runtime.Log($"  METHOD {FormatMethod(method)}");
                AuditMethod(method, problems);
            }
        }

        Runtime.Log($"audit summary: types={typeCount}, methods={methodCount}, unresolved-operands={problems.Count}");
        foreach (var problem in problems)
            Runtime.Log($"UNRESOLVED {problem.Kind} owner={problem.Owner} IL_{problem.Offset:X4} operand={problem.Operand} error={problem.Error}");

        Runtime.Log("=== FULL ORIGINAL ASSEMBLY AUDIT END ===");
    }

    private static void AuditMethod(MethodBase method, List<Problem> problems)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch (Exception ex)
        {
            Runtime.Log($"    BODY-ERROR {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (body is null) return;
        var il = body.GetILAsByteArray();
        if (il is null || il.Length == 0) return;

        var module = method.Module;
        var offset = 0;
        while (offset < il.Length)
        {
            var instructionOffset = offset;
            OpCode op;
            var b = il[offset++];
            if (b == 0xFE)
            {
                if (offset >= il.Length) break;
                op = MultiByteOpCodes[il[offset++]];
            }
            else
            {
                op = SingleByteOpCodes[b];
            }

            var size = OperandSize(op.OperandType, il, offset);
            if (size < 0 || offset + size > il.Length)
            {
                Runtime.Log($"    IL-PARSE-ERROR IL_{instructionOffset:X4} opcode={op}");
                break;
            }

            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineString or OperandType.InlineSig)
            {
                var token = BitConverter.ToInt32(il, offset);
                try
                {
                    object? resolved = op.OperandType switch
                    {
                        OperandType.InlineMethod => module.ResolveMethod(token, GetTypeArgs(method.DeclaringType), GetMethodArgs(method)),
                        OperandType.InlineField => module.ResolveField(token, GetTypeArgs(method.DeclaringType), GetMethodArgs(method)),
                        OperandType.InlineType => module.ResolveType(token, GetTypeArgs(method.DeclaringType), GetMethodArgs(method)),
                        OperandType.InlineString => module.ResolveString(token),
                        OperandType.InlineSig => module.ResolveSignature(token),
                        OperandType.InlineTok => ResolveMember(module, token, method),
                        _ => null
                    };

                    if (resolved is MemberInfo member)
                        Runtime.Log($"    IL_{instructionOffset:X4} {op.Name} {member.DeclaringType?.FullName}.{member.Name}");
                    else if (resolved is string str)
                        Runtime.Log($"    IL_{instructionOffset:X4} {op.Name} string:{Trim(str)}");
                }
                catch (Exception ex)
                {
                    problems.Add(new Problem(op.OperandType.ToString(), FormatMethod(method), instructionOffset, $"0x{token:X8}", $"{ex.GetType().Name}: {ex.Message}"));
                }
            }

            offset += size;
        }
    }

    private static MemberInfo ResolveMember(Module module, int token, MethodBase method)
        => module.ResolveMember(token, GetTypeArgs(method.DeclaringType), GetMethodArgs(method));

    private static Type[]? GetTypeArgs(Type? type)
        => type?.IsGenericType == true ? type.GetGenericArguments() : null;

    private static Type[]? GetMethodArgs(MethodBase method)
        => method.IsGenericMethod ? method.GetGenericArguments() : null;

    private static IEnumerable<Type> GetTypesSafe(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).Cast<Type>(); }
    }

    private static string FormatMethod(MethodBase method)
    {
        var pars = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
        var ret = method is MethodInfo mi ? (mi.ReturnType.FullName ?? mi.ReturnType.Name) : "ctor";
        return $"{ret} {method.DeclaringType?.FullName}.{method.Name}({pars})";
    }

    private static string Trim(string value)
        => value.Length <= 120 ? value.Replace("\r", "\\r").Replace("\n", "\\n") : value[..120] + "...";

    private static int OperandSize(OperandType type, byte[] il, int offset) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI => 4,
        OperandType.InlineBrTarget => 4,
        OperandType.InlineField => 4,
        OperandType.InlineMethod => 4,
        OperandType.InlineSig => 4,
        OperandType.InlineString => 4,
        OperandType.InlineTok => 4,
        OperandType.InlineType => 4,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        OperandType.InlineSwitch => offset + 4 <= il.Length ? 4 + BitConverter.ToInt32(il, offset) * 4 : -1,
        _ => -1
    };

    private static readonly OpCode[] SingleByteOpCodes = BuildSingleByteTable();
    private static readonly OpCode[] MultiByteOpCodes = BuildMultiByteTable();

    private static OpCode[] BuildSingleByteTable()
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op) continue;
            var value = unchecked((ushort)op.Value);
            if (value < 0x100) table[value] = op;
        }
        return table;
    }

    private static OpCode[] BuildMultiByteTable()
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op) continue;
            var value = unchecked((ushort)op.Value);
            if ((value & 0xFF00) == 0xFE00) table[value & 0xFF] = op;
        }
        return table;
    }
}
