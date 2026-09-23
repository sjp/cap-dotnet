using Cap.Escape.Tests;
using Cap.Primitives;
using Cap.Std;
using Cap.Tests;

#if CAP_XUNIT_AOT
[assembly: UnprivilegedRunRegistration]
#else
[assembly: AssemblyFixture(typeof(UnprivilegedRun))]
#endif

namespace Cap.Escape.Tests;

/// <summary>
/// Refuses to run the corpus at all in a process that could bypass the filesystem's own
/// permission checks.
/// </summary>
/// <remarks>
/// <para>
/// Most of the corpus is negative — this must be refused — and in a process that outranks the
/// permission system some of those turn green without the containment code having run. Every
/// assembly carries a test making the same check; here it is made before the first case
/// instead, as a fixture every test in the assembly depends on, so a privileged run fails
/// every case rather than reporting one failure beside a wall of passes that mean nothing.
/// </para>
/// <para>
/// The check is the one the other assemblies make: effective user 0 on Unix, and on Windows an
/// enabled privilege that skips access checks — not merely an elevated token, which the
/// symbolic-link cases need.
/// </para>
/// </remarks>
public sealed class UnprivilegedRun
{
    public UnprivilegedRun()
    {
        if (TestEnvironment.CanBypassFilePermissions)
        {
            throw new InvalidOperationException(
                "The escape corpus must not run in a process able to bypass file permissions: a " +
                "refusal the containment code was supposed to make can then be made by nothing " +
                "at all, or not be needed. Run as an ordinary user on Unix; on Windows, make sure " +
                "no backup, restore or take-ownership privilege is enabled in the token.");
        }
    }
}

#if CAP_XUNIT_AOT
/// <summary>
/// Registers <see cref="UnprivilegedRun"/> as an assembly fixture where the test framework is
/// the ahead-of-time build, which cannot discover fixtures by reflection.
/// </summary>
/// <remarks>
/// That build's source generator turns an <c>AssemblyFixture</c> attribute into this same
/// registration, but in the version pinned here it emits a factory that takes no argument
/// where the engine requires one, and the project does not compile. Writing the registration
/// out by hand keeps the guard in place — every case in the binary still depends on it — with
/// nothing else changed. Once the generator is fixed, both builds can go back to the
/// attribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class UnprivilegedRunRegistrationAttribute : Xunit.v3.EngineInitializationAttribute
{
    public override ValueTask InitializeAsync()
    {
        Xunit.v3.RegisteredEngineConfig.RegisterAssemblyFixtureFactory(
            typeof(UnprivilegedRun), _ => new ValueTask<object?>(new UnprivilegedRun()));
        return default;
    }
}
#endif

/// <summary>
/// Groups every test in the corpus so that they run one at a time.
/// </summary>
/// <remarks>
/// Each case installs the backend it runs on as the process-wide implementation for its
/// duration, and two cases doing that at once would each run on whichever the other had
/// installed.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class CorpusGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "escape corpus";
}

/// <summary>
/// The checks that hold after every operation, whatever the case expected it to come to.
/// </summary>
internal sealed class Oracle
{
    private readonly Arena _arena;
    private readonly string[] _entitled;
    private readonly string _outsideBefore;
    private readonly string _sandboxBefore;
    private readonly HashSet<CapFileId> _forbidden = [];

    /// <summary>
    /// Records the state outside the sandbox and the identities of everything there. Must run
    /// before the backend under test is installed, since it uses the library to learn the
    /// identities and should learn them from the host's own implementation.
    /// </summary>
    /// <param name="arena">The arena.</param>
    /// <param name="entitled">Directories beside the sandbox the operation may change.</param>
    public Oracle(Arena arena, params string[] entitled)
    {
        _arena = arena;
        _entitled = entitled;
        _outsideBefore = arena.SnapshotOutside(entitled);
        _sandboxBefore = arena.SnapshotSandbox();

        using Dir host = Dir.Open(arena.HostPath, AmbientAuthority.Acquire());
        _forbidden.Add(host.GetMetadata().FileId);
        _forbidden.Add(host.GetMetadata(EscapeCorpus.OutsideDirectory).FileId);
        _forbidden.Add(host.GetMetadata($"{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideFile}").FileId);
        _forbidden.Add(host.GetMetadata($"{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideSubdirectory}").FileId);
        _forbidden.Add(host.GetMetadata(
            $"{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideSubdirectory}/{EscapeCorpus.OutsideNestedFile}").FileId);
    }

    /// <summary>
    /// Nothing outside the sandbox changed, and nothing the operation let the caller see came
    /// from there.
    /// </summary>
    public void AssertContained(Observation observation, string context)
    {
        Assert.True(
            _outsideBefore == _arena.SnapshotOutside(_entitled),
            $"{context}: something outside the sandbox changed.\nBefore:\n{_outsideBefore}\n" +
            $"After:\n{_arena.SnapshotOutside(_entitled)}");

        foreach (string name in observation.Names)
        {
            Assert.False(
                name.StartsWith("outside", StringComparison.Ordinal) || name == "sandbox",
                $"{context}: listed '{name}', which exists only outside the sandbox.");
        }

        foreach (string contents in observation.Contents)
        {
            Assert.False(
                contents.Contains(EscapeCorpus.OutsideContent, StringComparison.Ordinal),
                $"{context}: read the contents of a file outside the sandbox.");
        }

        foreach (CapFileId reached in observation.Objects)
        {
            Assert.False(
                _forbidden.Contains(reached),
                $"{context}: reached {reached}, which is an object outside the sandbox.");
        }
    }

    /// <summary>A refused operation left the sandbox exactly as it was.</summary>
    /// <remarks>
    /// A refusal that half-happened — a link created with nothing in it, a directory made
    /// before the check that should have prevented it — is a bug even when it is inside,
    /// because a caller reading the refusal will not go looking for the debris.
    /// </remarks>
    public void AssertUnchangedInside(string context) =>
        Assert.True(
            _sandboxBefore == _arena.SnapshotSandbox(),
            $"{context}: the operation was refused but changed the sandbox.\nBefore:\n" +
            $"{_sandboxBefore}\nAfter:\n{_arena.SnapshotSandbox()}");
}
