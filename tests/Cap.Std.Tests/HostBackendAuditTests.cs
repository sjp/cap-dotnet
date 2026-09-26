using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using Cap.Directories;
using Cap.Fs.Ext;
using Cap.IO.Abstractions;
using Cap.Net;
using Cap.Primitives.Interop;
using Cap.Rand;
using Cap.Std.Testing;
using Cap.Time;

namespace Cap.Std.Tests;

/// <summary>
/// That nothing in a shipping assembly reaches the host backend except where there is no
/// handle yet.
/// </summary>
/// <remarks>
/// <para>
/// Every handle records the backend that issued it, and an operation on a handle has to go
/// through that backend. A method that asked for the host instead would work in every test
/// that uses the disk and go wrong only when the handle came from somewhere else. It would
/// send a simulated handle's number to the kernel, where the number names some real object,
/// or a real descriptor to a simulation, where it names an unrelated entry. No test about
/// results is certain to catch that, so the property is asserted about the code.
/// </para>
/// <para>
/// The compiled assemblies are read rather than the source. Every call to the host's
/// accessor is found in the instructions of every method, including the ones the compiler
/// generates for lambdas and iterators. A source search would have to understand all of
/// those, and could still be defeated by an alias.
/// </para>
/// </remarks>
public sealed class HostBackendAuditTests
{
    /// <summary>The assemblies that ship, as a theory's worth of cases.</summary>
    public static TheoryData<string> ShippedAssemblies =>
    [
        .. Shipped.Select(assembly => assembly.GetName().Name!),
    ];

    private static readonly Assembly[] Shipped =
    [
        typeof(PlatformOps).Assembly,
        typeof(Dir).Assembly,
        typeof(DirExtensions).Assembly,
        typeof(CapUnixStream).Assembly,
        typeof(ProjectDirs).Assembly,
        typeof(CapClock).Assembly,
        typeof(CapRandom).Assembly,
        typeof(InMemoryFileSystem).Assembly,
        typeof(DirFileSystem).Assembly,
    ];

    /// <summary>
    /// The places allowed to read the host, as the type and method that read it.
    /// </summary>
    /// <remarks>
    /// Each one starts from the process rather than from a handle: opening a first directory
    /// by path, finding the system's temporary location, and publishing the host's own
    /// counters. The slot's own type is exempt as well, since the slot is what it holds.
    /// </remarks>
    private static readonly (Type Type, string? Method)[] EntryPoints =
    [
        (typeof(PlatformOps), null),
        (typeof(ResolutionMetrics), null),
        (typeof(Dir), nameof(Dir.Open)),
        (typeof(Dir), nameof(Dir.TryOpen)),
        (typeof(CapTempDir), nameof(CapTempDir.New)),
    ];

    private static readonly MethodInfo HostAccessor =
        typeof(PlatformOps).GetProperty(nameof(PlatformOps.Host))!.GetMethod!;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>
    /// The audit finds the accessor where it is known to be read, so an empty result means
    /// something.
    /// </summary>
    /// <remarks>
    /// Without this the audit passes by finding nothing, which is how every scan fails: a
    /// change to how the accessor is compiled would turn the guarantee into a loop that
    /// reports success.
    /// </remarks>
    [Fact]
    public void The_audit_finds_the_known_entry_points()
    {
        string[] readers = [.. Readers(typeof(Dir).Assembly)];

        Assert.Contains($"{typeof(Dir).FullName}.{nameof(Dir.Open)}", readers);
        Assert.Contains($"{typeof(CapTempDir).FullName}.{nameof(CapTempDir.New)}", readers);
    }

    /// <summary>No method outside the entry points reads the host.</summary>
    [Theory]
    [MemberData(nameof(ShippedAssemblies))]
    public void Only_the_entry_points_read_the_host(string assemblyName)
    {
        Assembly assembly = Shipped.Single(candidate => candidate.GetName().Name == assemblyName);

        string[] strays = [.. Readers(assembly, excludeEntryPoints: true)];

        Assert.True(
            strays.Length == 0,
            $"{assemblyName} reads the host backend from {string.Join(", ", strays)}. An " +
            "operation on a handle must go through the backend recorded on the handle; the " +
            "host is only for opening a first handle where there is none.");
    }

    /// <summary>
    /// No method outside the host's own content implementation reads or writes a file through
    /// the operating system directly.
    /// </summary>
    /// <remarks>
    /// A file's contents go through the backend that opened it, like everything else about
    /// it. A direct call to <see cref="RandomAccess"/>, or a <see cref="FileStream"/> built
    /// over a handle, would pass every test on the disk and then take a simulated handle's
    /// number for a real descriptor.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShippedAssemblies))]
    public void Only_the_host_reads_and_writes_files_through_the_system(string assemblyName)
    {
        Assembly assembly = Shipped.Single(candidate => candidate.GetName().Name == assemblyName);

        string[] strays =
        [
            .. Callers(assembly, IsDirectFileAccess)
                .Where(caller => caller.Type != typeof(HostFileContent))
                .Select(caller => $"{caller.Type.FullName}.{caller.Method.Name}"),
        ];

        Assert.True(
            strays.Length == 0,
            $"{assemblyName} reads or writes a file through the system from " +
            $"{string.Join(", ", strays)}. File contents must go through the backend that " +
            "issued the handle.");
    }

    /// <summary>The content audit finds the host's own implementation, so an empty result means something.</summary>
    [Fact]
    public void The_content_audit_finds_the_host_implementation()
    {
        Assert.Contains(
            Callers(typeof(HostFileContent).Assembly, IsDirectFileAccess),
            caller => caller.Type == typeof(HostFileContent));
    }

    private static bool IsDirectFileAccess(MethodBase called) =>
        called.DeclaringType == typeof(RandomAccess) ||
        (called is ConstructorInfo && called.DeclaringType == typeof(FileStream));

    /// <summary>Every method in the assembly that reads the host's accessor.</summary>
    private static IEnumerable<string> Readers(Assembly assembly, bool excludeEntryPoints = false)
    {
        foreach ((Type type, MethodBase method) in Callers(assembly, IsHostAccessor))
        {
            if (excludeEntryPoints && IsEntryPoint(type, method))
            {
                continue;
            }

            yield return $"{type.FullName}.{method.Name}";
        }
    }

    private static bool IsHostAccessor(MethodBase called) =>
        called.DeclaringType == HostAccessor.DeclaringType && called.Name == HostAccessor.Name;

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
    private static IEnumerable<(Type Type, MethodBase Method)> Callers(Assembly assembly, Func<MethodBase, bool> target)
    {
        const BindingFlags Everything =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in assembly.GetTypes())
        {
            if (IsAddedByTooling(type))
            {
                continue;
            }

            IEnumerable<MethodBase> methods = [.. type.GetMethods(Everything), .. type.GetConstructors(Everything)];
            foreach (MethodBase method in methods)
            {
                if (Calls(method, target))
                {
                    yield return (type, method);
                }
            }
        }
    }

    /// <summary>
    /// Whether a type was put into the assembly after it was compiled, rather than written
    /// here.
    /// </summary>
    /// <remarks>
    /// Measuring which instructions run means rewriting each assembly measured, and what the
    /// rewriting adds is a counter table and the code that flushes it to a file of its own.
    /// That code is nobody's here: it does not ship, it holds no handle this library issued,
    /// and the file it writes is its own. Leaving it out by name keeps the audit narrow — an
    /// assembly nothing has rewritten is still read whole, and a name that stops matching
    /// fails the audit rather than quietly passing it.
    /// </remarks>
    private static bool IsAddedByTooling(Type type) =>
        type.FullName?.StartsWith("Microsoft.CodeCoverage.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether a method is one of the entry points, or code the compiler generated on its
    /// behalf.
    /// </summary>
    /// <remarks>
    /// A lambda or an iterator body is compiled into a nested type of the type it was written
    /// in, under a name the language does not let anybody write, so it belongs to whatever the
    /// outermost enclosing type is.
    /// </remarks>
    private static bool IsEntryPoint(Type type, MethodBase method)
    {
        Type outermost = type;
        while (outermost.DeclaringType is { } enclosing)
        {
            outermost = enclosing;
        }

        return EntryPoints.Any(entry =>
            entry.Type == outermost && (entry.Method is null || (entry.Type == type && entry.Method == method.Name)));
    }

    /// <summary>Whether a method's body calls, or takes the address of, anything matching <paramref name="target"/>.</summary>
    /// <remarks>
    /// Walks the body one instruction at a time, skipping each operand by its declared size,
    /// so that an operand's bytes are never mistaken for an instruction.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject is every method body in an assembly, which cannot be named " +
                        "statically, and the suite is not trimmed.")]
    private static bool Calls(MethodBase method, Func<MethodBase, bool> target)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
        {
            return false;
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
                if (Resolve(method, token) is { } called && target(called))
                {
                    return true;
                }
            }

            offset += OperandSize(code, body, offset);
        }

        return false;
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
            // A reference into an assembly this process never loaded cannot be resolved, and
            // cannot be the accessor, which lives in an assembly that is loaded.
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
