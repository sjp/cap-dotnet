using System.Runtime.CompilerServices;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std.Testing;

namespace Cap.Testing;

/// <summary>
/// Runs a suite written against the disk against a filesystem held in memory instead, when
/// the run is told to.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory filesystem is only worth having if it behaves like the disk, and the way to
/// keep it honest is not a second set of tests written for it, which would encode the same
/// assumptions twice, but the suites that already pin down the real behaviour. So a run with
/// <see cref="Variable"/> set to <see cref="Backends.InMemoryWalk"/> or
/// <see cref="Backends.InMemoryConfined"/> puts one such filesystem in place of the host for
/// the whole process, before the first test: every root opened by path resolves in it, every
/// tree a test arranges through <see cref="HostTree"/> is built in it, and
/// <see cref="Backends.OnThisHost"/> offers that one leg. The tests do not change.
/// </para>
/// <para>
/// The filesystem follows the running platform's path rules, as the disk it stands in for
/// does, and takes its times from the system clock, so that a test that watches a time move
/// sees it move. Tests about the host itself stand aside with
/// <see cref="NotInMemoryAttribute"/>.
/// </para>
/// </remarks>
internal static class InMemoryLeg
{
    /// <summary>The environment variable naming the leg.</summary>
    public const string Variable = "CAPDOTNET_TEST_BACKEND";

    /// <summary>The filesystem standing in for the host, when this run has one.</summary>
    public static InMemoryFileSystem? FileSystem { get; private set; }

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "The substitution has to be in place before test discovery opens anything.")]
    internal static void Install()
    {
        string? leg = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(leg))
        {
            return;
        }

        ResolutionBackend resolution = leg switch
        {
            Backends.InMemoryWalk => ResolutionBackend.PortableWalk,
            Backends.InMemoryConfined => ResolutionBackend.ConfinedOpen,
            _ => throw new InvalidOperationException(
                $"{Variable} is '{leg}', which is not a leg this suite can run on. It takes " +
                $"'{Backends.InMemoryWalk}' or '{Backends.InMemoryConfined}'."),
        };

        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions
        {
            Resolution = resolution,
            TimeProvider = TimeProvider.System,
        });

        MemoryTree tree = new(fs);
        fs.AddDirectory(MemoryTree.TemporaryDirectory);

        FileSystem = fs;
        HostTree.Current = tree;
        Backends.StandIn = (leg, fs.Backend);

        // Never put back: the process ends with the run.
        _ = PlatformOps.Substitute(fs.Backend);
    }
}
