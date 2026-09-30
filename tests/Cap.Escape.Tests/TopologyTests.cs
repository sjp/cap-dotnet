using Cap.Primitives;
using Cap.Std;
using Cap.Tests;

namespace Cap.Escape.Tests;

/// <summary>
/// Attacks that need a tree the case table cannot describe: a mount, a second path to the
/// root, a link planted before the root existed, and a pair of links that must be refused
/// indistinguishably.
/// </summary>
[Collection(CorpusGroup.Name)]
public sealed class TopologyTests
{
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
        HostFile.CreateHardLink(
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
        HostDirectory.CreateSymbolicLink(alias, arena.SandboxPath);

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
        if (OperatingSystem.IsWindows() || HostEntry.LinkTarget(Location) is null)
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
        string prepared = BindMountFixture.Require();

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

    /// <summary>
    /// A move or a second name between two filesystems is refused as crossing a device, by
    /// the filesystem, and leaves both trees as they were.
    /// </summary>
    /// <remarks>
    /// Both handles are held, so the capability for each end is there; what refuses is the
    /// filesystem, which cannot join two volumes. The refusal has to reach the caller as what
    /// it is, rather than as a missing name or an escape, and has to happen before anything
    /// changes: the library does not copy in place of a move it cannot make.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("T2", "T3")]
    public void A_move_or_hard_link_onto_another_volume_is_reported_as_cross_device(
        string backend, SymlinkPolicy policy)
    {
        string otherVolume = OtherVolume.Require();

        using Arena arena = new();
        using ScratchTree away = new(otherVolume);
        arena.Plant([new(SetupKind.File, $"tree/{EscapeCorpus.PlainFile}")]);

        using (Dir sandbox = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire()))
        using (Dir other = Dir.Open(away.HostPath, AmbientAuthority.Acquire()))
        {
            Assert.True(
                sandbox.GetMetadata().FileId.VolumeId != other.GetMetadata().FileId.VolumeId,
                $"{OtherVolume.Variable} names '{otherVolume}', which is on the same volume as " +
                $"'{arena.SandboxPath}'. Name a directory on another filesystem.");
        }

        string sandboxBefore = arena.SnapshotSandbox();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);
        using Dir destination = Dir.Open(away.HostPath, AmbientAuthority.Acquire(), policy);
        string file = $"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}";

        foreach ((string what, Action attempt) in new (string, Action)[]
        {
            ("moving a file", () => root.Rename(file, destination, "moved")),
            ("moving a directory", () => root.Rename("tree", destination, "moved")),
            ("moving a file back", () => destination.Rename("absent", root, "moved")),
            ("giving a file a second name", () => root.CreateHardLink(file, destination, "linked")),
        })
        {
            if (what == "moving a file back")
            {
                // The other way round, from a name there: a file is made there to be moved.
                HostFile.WriteAllText(Path.Join(away.HostPath, "absent"), EscapeCorpus.InsideContent);
            }

            Exception refused = Assert.ThrowsAny<CapIOException>(attempt);
            Assert.True(
                CapIOException.KindOf(refused) == CapErrorKind.CrossDevice,
                $"{what}, {backend}, {policy}: {refused.GetType().Name}: {refused.Message}");

            string planted = Path.Join(away.HostPath, "absent");
            if (HostEntry.IsTaken(planted))
            {
                HostFile.Delete(planted);
            }
        }

        Assert.Empty(HostDirectory.GetFileSystemEntries(away.HostPath));
        Assert.Equal(sandboxBefore, arena.SnapshotSandbox());
        scope.AssertItRan();
    }

    /// <summary>
    /// A root removed while a handle is open on it holds nothing, and nothing done through
    /// the handle brings it or anything in it back.
    /// </summary>
    /// <remarks>
    /// The handle keeps the directory itself alive, not its name or its place: it can still be
    /// described, every name beneath it is missing, listing it reports it removed, and a
    /// creation there is refused by the filesystem, which will not add an entry to a directory
    /// that has been removed. Nothing is resolved by the path the root was opened by, so
    /// nothing is made at that path either.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("T6")]
    public void A_root_removed_while_open_reports_missing_names_and_never_recreates_them(
        string backend, SymlinkPolicy policy)
    {
        SkipWhereOpenDirectoriesCannotBeMoved();

        foreach (Operation operation in Enum.GetValues<Operation>())
        {
            using Arena arena = new();

            Observation observation;
            using (BackendScope scope = Backends.Enter(backend))
            {
                using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);
                HostDirectory.Delete(arena.SandboxPath, recursive: true);

                observation = OperationRunner.Run(root, operation, "x");

                Assert.Throws<DirectoryNotFoundException>(() => root.EnumerateEntries().ToList());
                Assert.Equal(CapFileType.Directory, root.GetMetadata().Type);
            }

            string context = $"{operation}, {backend}, {policy}";
            Assert.True(
                observation.Outcome == Outcome.NotFound,
                $"{context}: expected NotFound, got {observation.Outcome}" +
                (observation.Detail is null ? "." : $": {observation.Detail.GetType().Name}: {observation.Detail.Message}"));
            Assert.False(HostEntry.IsTaken(arena.SandboxPath), $"{context}: the removed root was made again.");
        }
    }

    /// <summary>
    /// A root moved while a handle is open on it is still the root, wherever it now sits: names
    /// beneath it resolve beneath it, and a step or a link above it is still refused.
    /// </summary>
    /// <remarks>
    /// Moved into the directory beside it, so that what lies above the root afterwards is the
    /// very place the sandbox must not reach. A handle that remembered the path it was opened
    /// by, or looked its own place up again, would find it gone, or find the parent changed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("T6")]
    public void A_root_renamed_while_open_keeps_resolving_beneath_the_handle(string backend, SymlinkPolicy policy)
    {
        SkipWhereOpenDirectoriesCannotBeMoved();
        RequireFeatures(HostFeature.Symlinks);

        using Arena arena = new();
        arena.Plant([new(SetupKind.DirectoryLink, "up", "..")]);
        string moved = Path.Join(arena.OutsidePath, "moved");

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);
        HostDirectory.Move(arena.SandboxPath, moved);

        Assert.Equal(
            EscapeCorpus.InsideContent,
            root.ReadAllText($"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(".."));
        Assert.Throws<SandboxEscapeException>(() => root.OpenFile($"../{EscapeCorpus.OutsideFile}"));

        if (policy == SymlinkPolicy.FollowWithinSandbox)
        {
            Assert.Throws<SandboxEscapeException>(() => root.OpenDir("up"));
            Assert.Throws<SandboxEscapeException>(() => root.OpenFile($"up/{EscapeCorpus.OutsideFile}"));
        }
        else
        {
            Exception refused = Assert.ThrowsAny<CapIOException>(() => root.OpenDir("up"));
            Assert.IsNotType<SandboxEscapeException>(refused);
        }

        root.WriteAllText("made", EscapeCorpus.InsideContent);
        Assert.True(HostFile.Exists(Path.Join(moved, "made")));
        Assert.False(HostEntry.IsTaken(arena.SandboxPath));
        Assert.Equal(EscapeCorpus.OutsideContent, HostFile.ReadAllText(Path.Join(arena.OutsidePath, EscapeCorpus.OutsideFile)));
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

    private static void RequireFeatures(HostFeature needed) =>
        HostFeatures.Require(needed, "The topology cannot be built without it.");

    /// <summary>
    /// Windows will not remove or rename a directory while a handle is open on it, so there the
    /// attack cannot be arranged.
    /// </summary>
    private static void SkipWhereOpenDirectoriesCannotBeMoved()
    {
        if (OperatingSystem.IsWindows() && !HostTree.InMemory)
        {
            Assert.Skip("Windows does not remove or rename a directory while a handle is open on it.");
        }
    }
}
