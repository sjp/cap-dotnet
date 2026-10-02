#if !CAP_XUNIT_AOT
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cap.Testing;

namespace Cap.Tests;

/// <summary>
/// That every test class which reaches the host runs in one collection with the others that
/// do, in an assembly where a test replaces the host or reads its counters.
/// </summary>
/// <remarks>
/// <para>
/// The host implementation is one slot for the whole process. A test that replaces it
/// changes what every root opened by path resolves through, on every thread, and a test that
/// reads its counters counts every other test's opens too. xunit runs collections in
/// parallel, and a class with no collection is a collection of its own, so a class that opens
/// a root by path and is left out of the group runs alongside the class that replaced the
/// host. That compiles, passes on a quiet machine, and fails now and then in whichever test
/// happened to overlap, which is the worst way to learn about it.
/// </para>
/// <para>
/// So the rule is asserted about the compiled test assembly. A class reaches the host when
/// any of its methods, or any method in this assembly they call, however indirectly —
/// helpers, lambdas, iterator and async bodies, the throwaway tree — opens a root by path,
/// reads the host or replaces it. In an assembly where some class replaces or reads the host,
/// every such class must share one collection, unless the whole assembly is one collection.
/// An assembly where nothing replaces or reads the host can open roots from every class at
/// once, since each root is a directory of its own; there the rule holds trivially and the
/// test says so.
/// </para>
/// <para>
/// The places the host is reached are named rather than referenced, because this file is
/// compiled into every test assembly and most of them cannot see the host's internal type,
/// or do not reference the assembly that opens roots at all.
/// </para>
/// </remarks>
public sealed class CollectionDisciplineTests
{
    private const string HostSlot = "Cap.Primitives.Interop.PlatformOps";

    /// <summary>The members, by type and name, that open a root by path or touch the host.</summary>
    private static readonly (string Type, string[] Members)[] HostEntryPoints =
    [
        (HostSlot, ["get_Host", "Substitute"]),
        ("Cap.Std.Dir", ["Open", "TryOpen", "FromHandle"]),
        ("Cap.Std.CapTempDir", ["New"]),
    ];

    /// <summary>
    /// Every class that reaches the host runs in the assembly's serialising collection, where
    /// the assembly has one to need.
    /// </summary>
    [Fact]
    public void Every_class_that_reaches_the_host_runs_in_the_serialising_collection()
    {
        Assembly assembly = typeof(CollectionDisciplineTests).Assembly;
        CallGraph graph = new(assembly.ManifestModule);

        Type[] testClasses = [.. TestClasses(assembly)];
        Assert.Contains(typeof(CollectionDisciplineTests), testClasses);

        Type[] replacersAndReaders = [.. testClasses.Where(type => graph.Reaches(Roots(type), IsHostSlot))];
        if (replacersAndReaders.Length == 0)
        {
            Report($"Nothing in {assembly.GetName().Name} replaces or reads the host, so its classes may open roots in parallel.");
            return;
        }

        if (RunsAsOneCollection(assembly))
        {
            Report($"{assembly.GetName().Name} runs as one collection, so its classes cannot overlap.");
            return;
        }

        (Type Type, string? Collection)[] reachers =
        [
            .. testClasses
                .Where(type => graph.Reaches(Roots(type), IsHostEntryPoint))
                .Select(type => (type, CollectionOf(type)))
                .OrderBy(reacher => reacher.type.FullName, StringComparer.Ordinal),
        ];

        // The group is whichever collection most of them are already in; the rest are the ones to move.
        string? group = reachers
            .Where(reacher => reacher.Collection is not null)
            .GroupBy(reacher => reacher.Collection)
            .OrderByDescending(members => members.Count())
            .Select(members => members.Key)
            .FirstOrDefault();
        string[] strays =
        [
            .. reachers
                .Where(reacher => reacher.Collection != group)
                .Select(reacher => $"{reacher.Type.FullName} ({(reacher.Collection is null ? "no collection" : $"collection '{reacher.Collection}'")})"),
        ];

        Assert.True(
            group is not null && strays.Length == 0,
            $"In {assembly.GetName().Name}, {string.Join(", ", replacersAndReaders.Select(type => type.Name))} " +
            "replace or read the host, so every class that opens a root by path or touches the host must run " +
            "in one collection with them, or they overlap and fail intermittently. Not in " +
            $"{(group is null ? "any collection" : $"collection '{group}'")}: {string.Join(", ", strays)}. " +
            "Put each one in the assembly's group with [Collection(...)].");

        Report($"{reachers.Length} classes in {assembly.GetName().Name} reach the host, all in collection '{group}'.");
    }

    /// <summary>
    /// The scan follows calls into helpers, lambdas and async bodies, and finds nothing where
    /// there is nothing, so a pass means something.
    /// </summary>
    /// <remarks>
    /// Without this the rule passes by finding nothing, which is how every scan fails: a
    /// change to how the compiler lays out a lambda or an async method would turn the
    /// guarantee into a loop that reports success.
    /// </remarks>
    [Fact]
    public void The_scan_follows_helpers_lambdas_and_async_bodies()
    {
        CallGraph graph = new(typeof(Probe).Module);

        static bool IsMarker(MethodBase called) =>
            called.DeclaringType == typeof(Probe) && called.Name == nameof(Probe.Marker);

        Assert.True(graph.Reaches([Method(nameof(Probe.ThroughHelper))], IsMarker));
        Assert.True(graph.Reaches([Method(nameof(Probe.ThroughLambda))], IsMarker));
        Assert.True(graph.Reaches([Method(nameof(Probe.ThroughAsyncBody))], IsMarker));
        Assert.True(graph.Reaches([Method(nameof(Probe.ThroughAsyncHelper))], IsMarker));
        Assert.False(graph.Reaches([Method(nameof(Probe.Nowhere))], IsMarker));

        static MethodBase Method(string name) =>
            typeof(Probe).GetMethod(name, IlCalls.Everything)!;
    }

    private static bool IsHostSlot(MethodBase called) =>
        called.DeclaringType?.FullName == HostSlot && called.Name is "get_Host" or "Substitute";

    private static bool IsHostEntryPoint(MethodBase called) =>
        HostEntryPoints.Any(entry => called.DeclaringType?.FullName == entry.Type && entry.Members.Contains(called.Name));

    /// <summary>The classes xunit runs: concrete, with at least one fact or theory.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject is every type in the test assembly. The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:UnrecognizedReflectionPattern",
        Justification = "The types reflected over are whatever the assembly defines. The suite is not trimmed.")]
    private static IEnumerable<Type> TestClasses(Assembly assembly) =>
        assembly.GetTypes().Where(type =>
            type is { IsClass: true, IsAbstract: false } &&
            !IlCalls.IsAddedByTooling(type) &&
            type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Any(method => method.IsDefined(typeof(FactAttribute), inherit: true)));

    /// <summary>
    /// Where a scan of a test class starts: every method it and the base classes in its
    /// assembly declare, and the methods of the types the compiler nested inside them.
    /// </summary>
    private static IEnumerable<MethodBase> Roots(Type testClass)
    {
        for (Type? type = testClass; type is not null && type.Module == testClass.Module; type = type.BaseType)
        {
            foreach (MethodBase method in DeclaredWithin(type))
            {
                yield return method;
            }
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:UnrecognizedReflectionPattern",
        Justification = "The types reflected over are whatever the assembly defines. The suite is not trimmed.")]
    private static IEnumerable<MethodBase> DeclaredWithin(Type type) =>
        IlCalls.MethodsOf(type).Concat(type.GetNestedTypes(IlCalls.Everything).SelectMany(DeclaredWithin));

    /// <summary>The collection a class is in, or null for the one of its own xunit gives it.</summary>
    private static string? CollectionOf(Type type) =>
        type.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName?.StartsWith("Xunit.CollectionAttribute", StringComparison.Ordinal) == true)
            .Select(attribute => attribute.AttributeType.IsGenericType
                ? attribute.AttributeType.GetGenericArguments()[0].FullName
                : attribute.ConstructorArguments.FirstOrDefault().Value switch
                {
                    string name => name,
                    Type definition => definition.FullName,
                    _ => null,
                })
            .FirstOrDefault();

    /// <summary>Whether the assembly runs every class in one collection.</summary>
    private static bool RunsAsOneCollection(Assembly assembly) =>
        assembly.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(CollectionBehaviorAttribute))
            .Any(attribute => attribute.ConstructorArguments.Any(argument =>
                argument.Value is int behavior && behavior == (int)CollectionBehavior.CollectionPerAssembly));

    private static void Report(string message) =>
        TestContext.Current.TestOutputHelper?.WriteLine(message);

    /// <summary>
    /// The methods a method calls, followed through every method of one module, with each
    /// body read once.
    /// </summary>
    private sealed class CallGraph(Module module)
    {
        private readonly Dictionary<MethodBase, MethodBase[]> _callees = [];

        /// <summary>
        /// Whether anything reachable from <paramref name="roots"/> through methods of the
        /// module calls something matching <paramref name="target"/>.
        /// </summary>
        /// <remarks>
        /// A call into another module is not followed: what those assemblies reach is theirs
        /// to answer for, and the entry points named above are where they reach the host. A
        /// call through an interface or a delegate is not followed either, since which body
        /// runs is not in the instructions.
        /// </remarks>
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075:UnrecognizedReflectionPattern",
            Justification = "The state machines reflected over are whatever the assembly defines. The suite is not trimmed.")]
        public bool Reaches(IEnumerable<MethodBase> roots, Func<MethodBase, bool> target)
        {
            HashSet<MethodBase> seen = [];
            Queue<MethodBase> pending = new(roots);

            while (pending.TryDequeue(out MethodBase? method))
            {
                if (!seen.Add(method))
                {
                    continue;
                }

                // An iterator or async method's body is moved into a generated type, and the
                // method left behind only starts it.
                if (method.GetCustomAttribute<StateMachineAttribute>() is { } stateMachine &&
                    stateMachine.StateMachineType.GetMethod("MoveNext", IlCalls.Everything) is { } body)
                {
                    pending.Enqueue(body);
                }

                foreach (MethodBase called in Callees(method))
                {
                    if (target(called))
                    {
                        return true;
                    }

                    if (called.Module == module && Definition(called) is { } definition)
                    {
                        pending.Enqueue(definition);
                    }
                }
            }

            return false;
        }

        private MethodBase[] Callees(MethodBase method)
        {
            if (!_callees.TryGetValue(method, out MethodBase[]? callees))
            {
                callees = [.. IlCalls.Callees(method)];
                _callees[method] = callees;
            }

            return callees;
        }

        /// <summary>The method as declared, rather than one instantiation of it.</summary>
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026:RequiresUnreferencedCode",
            Justification = "The methods resolved are whatever the assembly defines. The suite is not trimmed.")]
        private MethodBase? Definition(MethodBase called)
        {
            try
            {
                return module.ResolveMethod(called.MetadataToken);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>The shapes the scan must see through, for <see cref="The_scan_follows_helpers_lambdas_and_async_bodies"/>.</summary>
    /// <remarks>Never run; only read.</remarks>
    private static class Probe
    {
        public static void Marker()
        {
        }

        public static void ThroughHelper() => Helper.Direct();

        public static void ThroughLambda()
        {
            Action action = () => Helper.Direct();
            action();
        }

        public static async Task ThroughAsyncBody()
        {
            await Task.Yield();
            Helper.Direct();
        }

        public static void ThroughAsyncHelper() => _ = Helper.DirectLater();

        public static void Nowhere() => Helper.Unrelated();
    }

    private static class Helper
    {
        public static void Direct() => Probe.Marker();

        public static async Task DirectLater()
        {
            await Task.Yield();
            Direct();
        }

        public static void Unrelated()
        {
        }
    }
}
#endif
