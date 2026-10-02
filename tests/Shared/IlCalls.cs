#if !CAP_XUNIT_AOT
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;

namespace Cap.Testing;

/// <summary>
/// Finds the methods a compiled method body calls, by reading its instructions.
/// </summary>
/// <remarks>
/// <para>
/// The audits that assert something about code rather than about results read the compiled
/// assemblies rather than the source. Every call is found in the instructions of every method,
/// including the ones the compiler generates for lambdas and iterators. A source search would
/// have to understand all of those, and could still be defeated by an alias.
/// </para>
/// <para>
/// Left out of the build that publishes the suite ahead of time, which has no method bodies
/// to read.
/// </para>
/// </remarks>
internal static class IlCalls
{
    /// <summary>Every member a method body can hold, whatever its visibility.</summary>
    public const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>Every method in the assembly whose body calls something matching <paramref name="target"/>.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject is every method body in an assembly, which cannot be named " +
                        "statically, and the suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "The types reflected over are whatever the assembly defines. The suite is not trimmed.")]
    public static IEnumerable<(Type Type, MethodBase Method)> Callers(Assembly assembly, Func<MethodBase, bool> target)
    {
        foreach (Type type in assembly.GetTypes())
        {
            if (IsAddedByTooling(type))
            {
                continue;
            }

            foreach (MethodBase method in MethodsOf(type))
            {
                if (Calls(method, target))
                {
                    yield return (type, method);
                }
            }
        }
    }

    /// <summary>The methods and constructors a type declares itself.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:UnrecognizedReflectionPattern",
        Justification = "The types reflected over are whatever the assembly defines. The suite is not trimmed.")]
    public static IEnumerable<MethodBase> MethodsOf(Type type) =>
        [.. type.GetMethods(Everything), .. type.GetConstructors(Everything)];

    /// <summary>
    /// Whether a type was put into the assembly after it was compiled, rather than written
    /// here.
    /// </summary>
    /// <remarks>
    /// Measuring which instructions run means rewriting each assembly measured, and what the
    /// rewriting adds is a counter table and the code that flushes it to a file of its own.
    /// That code is nobody's here: it does not ship, it holds no handle this library issued,
    /// and the file it writes is its own. Leaving it out by name keeps the audits narrow — an
    /// assembly nothing has rewritten is still read whole, and a name that stops matching
    /// fails an audit rather than quietly passing it.
    /// </remarks>
    public static bool IsAddedByTooling(Type type) =>
        type.FullName?.StartsWith("Microsoft.CodeCoverage.", StringComparison.Ordinal) == true;

    /// <summary>Whether a method's body calls, or takes the address of, anything matching <paramref name="target"/>.</summary>
    public static bool Calls(MethodBase method, Func<MethodBase, bool> target) =>
        Callees(method).Any(target);

    /// <summary>Everything a method's body calls, or takes the address of.</summary>
    /// <remarks>
    /// Walks the body one instruction at a time, skipping each operand by its declared size,
    /// so that an operand's bytes are never mistaken for an instruction. A reference into an
    /// assembly this process never loaded is left out, since nothing in it can be looked at.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject is every method body in an assembly, which cannot be named " +
                        "statically, and the suite is not trimmed.")]
    public static IEnumerable<MethodBase> Callees(MethodBase method)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
        {
            yield break;
        }

        int offset = 0;
        while (offset < body.Length)
        {
            short value = body[offset] == 0xFE
                ? (short)(0xFE00 | body[offset + 1])
                : body[offset];
            OpCode code = OpCodesByValue[value];
            offset += code.Size;

            if (code.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(body, offset);
                if (Resolve(method, token) is { } called)
                {
                    yield return called;
                }
            }

            offset += OperandSize(code, body, offset);
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject is every method body in an assembly, which cannot be named " +
                        "statically, and the suite is not trimmed.")]
    private static MethodBase? Resolve(MethodBase method, int token)
    {
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        try
        {
            return method.Module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (ArgumentException)
        {
            // A reference into an assembly this process never loaded cannot be resolved.
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }

    private static int OperandSize(OpCode code, byte[] body, int offset) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(body, offset)),
        _ => 4,
    };
}
#endif
