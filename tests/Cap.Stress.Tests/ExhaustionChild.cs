using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>
/// Runs a process out of descriptors, resolves at the edge of the limit, and reports.
/// </summary>
/// <remarks>
/// <para>
/// Half of a test, in a process of its own because the limit it lowers applies to the whole
/// process and the test framework needs descriptors of its own to go on running.
/// </para>
/// <para>
/// What is being checked is that running out is a failure and nothing else. The places it could
/// be something else are specific. A resolution that cannot open the next directory on its way
/// down must fail rather than fall back to something that needs no descriptor — resolving the
/// rest of the path as a string, say, which would resolve it with no sandbox at all. A
/// capability probe that met the limit could conclude the kernel lacks the confined open and
/// quietly demote every later resolution to the walk. And a walk that ran out half-way through
/// a path must close the handles it had already opened, or every failure leaves the process
/// closer to the limit than the last.
/// </para>
/// <para>
/// So for each backend, the child holds handles until an open fails, then gives them back one
/// at a time, and at every step between tries two things: a path deep enough that the walk
/// needs a descriptor per level, which must either fail or reach the right file; and a path
/// through a link that points outside, which must never succeed. Afterwards the limit is put
/// back and the descriptors on the tree are counted, and must be what they were before.
/// </para>
/// </remarks>
internal static class ExhaustionChild
{
    /// <summary>Set to the sandbox to work in, which makes this process the child.</summary>
    public const string RequestVariable = "CAPDOTNET_STRESS_EXHAUSTION_CHILD";

    /// <summary>The directories on the way to the deep file, as a path.</summary>
    public const string DeepDirectories = "deep/a/b/c/d/e/f/g/h/i/j";

    /// <summary>The name of the link in the sandbox pointing at the directory outside.</summary>
    public const string OutwardLink = "out";

    /// <summary>How many descriptors above what the process already holds the limit is set to.</summary>
    private const int Headroom = 24;

    /// <summary>The line the child ends with when every check held.</summary>
    public const string Passed = "exhaustion: passed";

    [ModuleInitializer]
    internal static void RunIfRequested()
    {
        string? sandbox = Environment.GetEnvironmentVariable(RequestVariable);
        if (string.IsNullOrEmpty(sandbox))
        {
            return;
        }

        int exitCode;
        try
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("The descriptor limit is a Unix setting.");
            }

#pragma warning disable CA1416 // Guarded above; the analyser does not follow a negated pair.
            foreach (string backend in Backends.OnThisHost)
            {
                Exhaust(sandbox, backend);
            }
#pragma warning restore CA1416

            Console.Out.WriteLine(Passed);
            exitCode = 0;
        }
        catch (Exception e)
        {
            Console.Out.WriteLine($"exhaustion: FAILED: {e}");
            exitCode = 1;
        }

        Console.Out.Flush();
        Environment.Exit(exitCode);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void Exhaust(string sandbox, string backend)
    {
        BackendScope scope;
        try
        {
            scope = Backends.Enter(backend);
        }
        catch (Exception e) when (e.GetType().Name == "SkipException")
        {
            Console.Out.WriteLine($"{backend}: skipped: {e.Message}");
            return;
        }

        using (scope)
        {
            string deepFile = DeepDirectories + "/" + StressArena.FileName;
            string outward = OutwardLink + "/" + StressArena.FileName;
            CapFileId deepIdentity = StressArena.IdentityOf(Path.Join(sandbox, deepFile));
            CapFileId outsideIdentity = StressArena.IdentityOf(Path.Join(sandbox, outward));

            using Dir root = Dir.Open(sandbox, AmbientAuthority.Acquire());

            // Everything the run will do is done once first, with descriptors to spare, so that
            // nothing the runtime loads lazily -- an assembly, a resource string -- first needs
            // a descriptor at the moment there are none.
            Require(Race.OpenFile(root, deepFile) == deepIdentity, "the deep file was not the one planted");
            Refusal(() => Race.OpenFile(root, outward), outsideIdentity);
            Refusal(() => Race.OpenDir(root, OutwardLink), outsideIdentity);
            using (root.OpenDir(StressArena.DirectoryName))
            {
            }

            int before = OnTree(sandbox);
            (ulong soft, ulong hard) = HostOps.GetDescriptorLimit();
            HostOps.SetDescriptorLimit((ulong)Descriptors.Total() + Headroom, hard);

            List<Dir> held = [];
            Dictionary<string, int> seen = new(StringComparer.Ordinal);
            int deepReached = 0;
            try
            {
                Exception? limit = null;
                while (limit is null)
                {
                    try
                    {
                        held.Add(root.OpenDir(StressArena.DirectoryName));
                    }
                    catch (Exception e)
                    {
                        limit = e;
                    }

                    Require(held.Count < 100_000, "the limit was never reached");
                }

                Require(
                    limit is IOException and not SandboxEscapeException,
                    $"running out surfaced as {limit.GetType().Name} rather than as a failure to open: {limit.Message}");
                Count(seen, "at the limit: " + limit.GetType().Name);

                // Out of descriptors entirely, and then with one more free at each step until
                // everything is given back: the walk's deep path passes from failing part-way
                // down to succeeding somewhere in between.
                while (true)
                {
                    Count(seen, "deep: " + Deep(root, deepFile, deepIdentity, ref deepReached));
                    Count(seen, "outward: " + Refusal(() => Race.OpenFile(root, outward), outsideIdentity));
                    Count(seen, "outward directory: " + Refusal(() => Race.OpenDir(root, OutwardLink), outsideIdentity));

                    if (held.Count == 0)
                    {
                        break;
                    }

                    held[^1].Dispose();
                    held.RemoveAt(held.Count - 1);
                }
            }
            finally
            {
                foreach (Dir dir in held)
                {
                    dir.Dispose();
                }

                HostOps.SetDescriptorLimit(soft, hard);
            }

            Require(deepReached > 0, "the deep file was never reached, even with every descriptor given back");

            // Exact on Linux, where only the tree's own descriptors are counted. Elsewhere the
            // count is the whole process's, which the runtime moves by a few for its own reasons;
            // a walk that leaked on its failing exits would have leaked on every one of the
            // dozens it took, which is far beyond that.
            int after = OnTree(sandbox);
            int allowed = OperatingSystem.IsLinux() ? 0 : 4;
            Require(
                after - before <= allowed,
                $"{after - before} more descriptor(s) were open after the handles were given back than before");

            // Running out must not have been read as the kernel lacking the confined open.
            if (OperatingSystem.IsLinux() && backend == Backends.ConfinedOpen && PlatformOps.Host is LinuxPlatformOps linux)
            {
                long attempts = linux.ConfinedOpenAttempts;
                Require(linux.Capabilities.SupportsConfinedOpen, "running out of descriptors demoted the confined open");
                Race.OpenFile(root, deepFile);
                Require(linux.ConfinedOpenAttempts > attempts, "resolution stopped using the confined open after running out");
            }

            scope.AssertItRan();

            foreach ((string what, int count) in seen.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Console.Out.WriteLine($"{backend}: {what} x{count}");
            }
        }
    }

    /// <summary>Opens the deep file, which must be the one planted if it opens at all.</summary>
    private static string Deep(Dir root, string path, CapFileId expected, ref int reached)
    {
        try
        {
            CapFileId identity = Race.OpenFile(root, path);
            Require(identity == expected, "the deep path reached a different file at the edge of the limit");
            reached++;
            return "reached";
        }
        catch (SandboxEscapeException e)
        {
            throw new InvalidOperationException("a path with no escape in it was refused as one at the edge of the limit", e);
        }
        catch (IOException e)
        {
            return e.GetType().Name;
        }
    }

    /// <summary>Tries an operation that leads outside, which must never succeed.</summary>
    private static string Refusal(Func<CapFileId> attempt, CapFileId outside)
    {
        CapFileId reached;
        try
        {
            reached = attempt();
        }
        catch (IOException e)
        {
            return e.GetType().Name;
        }

        throw new InvalidOperationException(
            reached == outside
                ? "a path through a link pointing outside reached the object outside"
                : "a path through a link pointing outside succeeded");
    }

    /// <summary>The descriptors on anything beneath the sandbox, or the process total where that is all there is.</summary>
    private static int OnTree(string sandbox) =>
        OperatingSystem.IsLinux() ? Descriptors.HeldBeneath(sandbox + Path.DirectorySeparatorChar).Count : Descriptors.Total();

    private static void Count(Dictionary<string, int> seen, string what) =>
        seen[what] = seen.TryGetValue(what, out int count) ? count + 1 : 1;

    private static void Require(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }
}
