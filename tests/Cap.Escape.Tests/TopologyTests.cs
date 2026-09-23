using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// Attacks that need a tree the case table cannot describe: a mount, a second path to the
/// root, a link planted before the root existed, and a pair of links that must be refused
/// indistinguishably.
/// </summary>
[Collection(CorpusGroup.Name)]
public sealed class TopologyTests
{
    /// <summary>
    /// The environment variable naming a directory prepared with a bind mount inside it.
    /// </summary>
    /// <remarks>
    /// Mounting needs a privilege the corpus must not hold, so the mount is made by whoever
    /// runs the suite and handed over by path. The directory holds a subdirectory
    /// <c>mnt</c> onto which another directory has been bind-mounted; that directory holds a
    /// file <c>file</c>, a link <c>climb</c> stored as <c>../..</c> and a link <c>up</c> stored
    /// as <c>..</c>. The CI workflow builds exactly this.
    /// </remarks>
    public const string BindMountVariable = "CAPDOTNET_TEST_BIND_MOUNT";

    public static TheoryData<string, SymlinkPolicy> BackendsAndPolicies
    {
        get
        {
            TheoryData<string, SymlinkPolicy> rows = [];
            foreach (string backend in Backends.OnThisHost)
            {
                rows.Add(backend, SymlinkPolicy.FollowWithinSandbox);
                rows.Add(backend, SymlinkPolicy.Deny);
            }

            return rows;
        }
    }

    /// <summary>
    /// A link aimed outside at something that exists and one aimed at something that does not
    /// are refused identically, by every operation.
    /// </summary>
    /// <remarks>
    /// If the two answers differed in any way — the outcome, the exception's type, its message —
    /// a handle would be a way to ask what exists outside the subtree it covers. The refusal is
    /// decided from the target as stored, before anything is looked up, so there is nothing for
    /// the answer to differ by; this checks that nothing on the way to the caller adds it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("S6")]
    public void A_link_outside_is_refused_the_same_way_whether_or_not_its_target_exists(
        string backend, SymlinkPolicy policy)
    {
        RequireFeatures(HostFeature.Symlinks);

        // Names of equal length, so that messages quoting them differ only in those characters.
        (string, string)[] pairs =
        [
            ("rel-real", "rel-none"),
            ("abs-real", "abs-none"),
        ];
        SetupStep[] links =
        [
            new(SetupKind.FileLink, "rel-real", $"../{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideFile}"),
            new(SetupKind.FileLink, "rel-none", $"../{EscapeCorpus.OutsideDirectory}/no-such-secret"),
            new(SetupKind.FileLink, "abs-real", $"{{outside}}/{EscapeCorpus.OutsideFile}"),
            new(SetupKind.FileLink, "abs-none", "{outside}/no-such-secret"),
        ];

        foreach ((string real, string none) in pairs)
        {
            foreach (Operation operation in Enum.GetValues<Operation>())
            {
                Observation toReal = RunOnce(backend, policy, links, operation, real);
                Observation toNone = RunOnce(backend, policy, links, operation, none);
                string context = $"{operation} on '{real}' and '{none}', {backend}, {policy}";

                Assert.True(toReal.Outcome == toNone.Outcome, $"{context}: {toReal.Outcome} against {toNone.Outcome}.");
                Assert.Equal(toReal.Detail?.GetType(), toNone.Detail?.GetType());
                Assert.Equal(
                    toReal.Detail?.Message.Replace(real, "<link>", StringComparison.Ordinal),
                    toNone.Detail?.Message.Replace(none, "<link>", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// A second name for a file outside, made before the root was opened, reaches that file.
    /// </summary>
    /// <remarks>
    /// Not a defence: the one attack in the model stated as undefendable, asserted so that the
    /// statement is kept honest. A hard link planted inside is an entry inside, reachable by
    /// descending, and indistinguishable from any other file — the object has no memory of which
    /// directory it was first created in. Should this ever start failing, the threat model's
    /// account of it is wrong and has to be rewritten, not the test.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("T4")]
    public void A_hard_link_planted_before_the_root_was_opened_reaches_the_file_it_names(
        string backend, SymlinkPolicy policy)
    {
        RequireFeatures(HostFeature.HardLinks);

        using Arena arena = new();
        HostFilesystem.CreateHardLink(
            Path.Join(arena.OutsidePath, EscapeCorpus.OutsideFile),
            Path.Join(arena.SandboxPath, "planted"));

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        Assert.Equal(EscapeCorpus.OutsideContent, root.ReadAllText("planted"));
    }

    /// <summary>
    /// A root opened through a path that passes through a link is the directory the link led
    /// to, and is confined to that directory and nothing that merely lies along the path.
    /// </summary>
    /// <remarks>
    /// The path is resolved once, under ambient authority, when the root is opened; nothing
    /// afterwards remembers how the directory was reached. That is what makes a system
    /// temporary location that is itself a link to somewhere else usable as a root.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("M3")]
    public void A_root_opened_through_a_link_is_confined_to_where_the_link_led(string backend, SymlinkPolicy policy)
    {
        RequireFeatures(HostFeature.Symlinks);

        using Arena arena = new();
        arena.Plant([new(SetupKind.DirectoryLink, "up", $"../{EscapeCorpus.OutsideDirectory}")]);
        string alias = Path.Join(arena.HostPath, "alias");
        Directory.CreateSymbolicLink(alias, arena.SandboxPath);

        CapFileId sandbox;
        using (Dir direct = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire()))
        {
            sandbox = direct.GetMetadata().FileId;
        }

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(alias, AmbientAuthority.Acquire(), policy);

        Assert.Equal(sandbox, root.GetMetadata().FileId);
        Assert.Equal(EscapeCorpus.InsideContent, root.ReadAllText($"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(".."));

        if (policy == SymlinkPolicy.FollowWithinSandbox)
        {
            Assert.Throws<SandboxEscapeException>(() => root.OpenDir($"up/{EscapeCorpus.OutsideSubdirectory}"));
        }
    }

    /// <summary>
    /// Where the system's temporary location is a link — as it is on macOS, where it leads into
    /// a private directory — a root opened by that name works and is confined like any other.
    /// </summary>
    [Fact]
    [Defends("M3")]
    public void A_temporary_location_that_is_a_link_can_be_a_root()
    {
        const string Location = "/tmp";
        if (OperatingSystem.IsWindows() || new DirectoryInfo(Location).LinkTarget is null)
        {
            Assert.Skip($"'{Location}' is not a link on this host.");
        }

        using Dir root = Dir.Open(Location, AmbientAuthority.Acquire());
        Assert.Equal(CapFileType.Directory, root.GetMetadata().Type);
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(".."));
    }

    /// <summary>
    /// A mount inside the root is crossed, and a link inside the mounted directory can no more
    /// climb out of the root than one anywhere else can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mount table is trusted: whoever mounted something inside the root put it there, and
    /// descending into it is descending. What is under test is that a step up from inside the
    /// mount is taken against the tree as it is mounted — up into the root — and not against
    /// wherever the mounted directory happens to live, and that a link there stays subject to
    /// the same root.
    /// </para>
    /// <para>
    /// A resolution that refuses to cross mounts exists inside the library, and is exercised by
    /// the resolver's own tests; no public member asks for it, so it is not reachable from here.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("T1")]
    public void A_mount_inside_the_root_is_crossed_but_cannot_be_climbed_out_of(string backend, SymlinkPolicy policy)
    {
        string? prepared = Environment.GetEnvironmentVariable(BindMountVariable);
        if (string.IsNullOrEmpty(prepared))
        {
            Assert.Skip(
                $"No bind mount was prepared for this run. Mounting needs a privilege the corpus " +
                $"must not hold, so it is made beforehand and named in {BindMountVariable}.");
        }

        string mountPoint = Path.Join(prepared, "mnt");
        Assert.True(
            IsMountPoint(mountPoint),
            $"{BindMountVariable} names '{prepared}', but '{mountPoint}' is not a mount point. The run " +
            "was set up to test a mount and would otherwise test an ordinary directory.");

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(prepared, AmbientAuthority.Acquire(), policy);

        Assert.False(string.IsNullOrEmpty(root.ReadAllText("mnt/file")));

        if (policy == SymlinkPolicy.FollowWithinSandbox)
        {
            Assert.Throws<SandboxEscapeException>(() => root.OpenDir("mnt/climb"));

            using Dir up = root.OpenDir("mnt/up");
            Assert.Equal(root.GetMetadata().FileId, up.GetMetadata().FileId);
        }
        else
        {
            Exception refused = Assert.ThrowsAny<CapIOException>(() => root.OpenDir("mnt/climb"));
            Assert.IsNotType<SandboxEscapeException>(refused);
        }

        scope.AssertItRan();
    }

    private static Observation RunOnce(
        string backend, SymlinkPolicy policy, SetupStep[] links, Operation operation, string path)
    {
        using Arena arena = new();
        arena.Plant(links);
        Oracle oracle = new(arena);

        Observation observation;
        using (Backends.Enter(backend))
        {
            using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);
            observation = OperationRunner.Run(root, operation, path);
        }

        oracle.AssertContained(observation, $"{operation} on '{path}'");
        return observation;
    }

    private static bool IsMountPoint(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        foreach (string line in File.ReadLines("/proc/self/mountinfo"))
        {
            // The fifth field is where the mount sits. Spaces in it are written as octal escapes.
            string[] fields = line.Split(' ');
            if (fields.Length > 4 && fields[4].Replace("\\040", " ", StringComparison.Ordinal) == full)
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireFeatures(HostFeature needed)
    {
        HostFeature missing = needed & ~HostFeatures.Current;
        if (missing != HostFeature.None)
        {
            Assert.Skip($"This host or volume does not have {missing}.");
        }
    }
}
