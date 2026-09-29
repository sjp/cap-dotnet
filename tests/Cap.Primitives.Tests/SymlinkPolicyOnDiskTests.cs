using Cap.Primitives.Interop;
using Cap.Tests;

namespace Cap.Primitives.Tests;

/// <summary>
/// The policy table, driven against a real kernel and a real tree.
/// </summary>
/// <remarks>
/// <para>
/// The simulated half of the corpus asserts that the two strategies agree with the table.
/// What it cannot assert is that the table describes what the operating system does, because
/// the simulation is written from the same understanding as the code. A flag the kernel
/// ignores, an error that arrives as a different code, a link an open follows after all —
/// none of those show up until the calls are made for real.
/// </para>
/// <para>
/// Which strategies run here depends on the host. The walk runs everywhere, because it is
/// what macOS uses, what Linux falls back to when a sandbox filter has removed the confined
/// open, and — with its own per-step native call — what Windows uses. The confined open runs
/// only where the kernel offers one. A host that has both runs the table twice and has to get
/// the same answers, which is the property a forced-fallback deployment depends on.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class SymlinkPolicyOnDiskTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-symlink-policy-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Every case in the table, one theory case each.</summary>
    public static TheoryData<string> Cases => [.. SymlinkPolicyCorpus.Names];

    /// <summary>The walk answers the table against the host's own filesystem.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_hosts_walk_answers_the_policy_table(string caseName) =>
        Run(SymlinkPolicyBackend.Walk, caseName);

    /// <summary>
    /// So does the host's confined open, where it has one.
    /// </summary>
    /// <remarks>
    /// Skipped rather than silently satisfied by the walk on a host without it. Answering
    /// this theory by quietly walking instead would report a kernel-atomic backend as tested
    /// on every machine that does not have one, which is the demotion this library is most
    /// concerned with not letting happen unnoticed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_hosts_confined_open_answers_the_policy_table(string caseName)
    {
        if (!PlatformOps.Host.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip("This kernel has no confined, atomic open; the walk theory covers the host.");
        }

        Run(SymlinkPolicyBackend.ConfinedOpen, caseName);
    }

    /// <summary>Every row of the mount table, one theory case each.</summary>
    public static TheoryData<string> MountCases => [.. MountRows.Select(row => row.Name)];

    /// <summary>
    /// The walk answers the mount table against the bind mount prepared for the run.
    /// </summary>
    /// <remarks>
    /// Kept apart from the policy table because a tree with a mount in it cannot be built by
    /// the test: mounting needs a privilege the suite must not hold. The rows are resolved in
    /// the directory named in <see cref="BindMountFixture.Variable"/> instead.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MountCases))]
    public void The_hosts_walk_answers_the_mount_table(string rowName) =>
        RunMount(SymlinkPolicyBackend.Walk, rowName);

    /// <summary>So does the host's confined open, where it has one.</summary>
    [Theory]
    [MemberData(nameof(MountCases))]
    public void The_hosts_confined_open_answers_the_mount_table(string rowName)
    {
        if (!PlatformOps.Host.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip("This kernel has no confined, atomic open; the walk theory covers the host.");
        }

        RunMount(SymlinkPolicyBackend.ConfinedOpen, rowName);
    }

    /// <summary>
    /// A path into the mount, resolved with and without the refusal to cross one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refused crossing is a policy refusal of something beneath the root and is reported
    /// as crossing a device on every backend; only a path that leaves the root is an escape.
    /// </para>
    /// <para>
    /// A path that crosses the mount and then leaves the root is the one place the two
    /// disagree, and the row says so rather than letting either answer pass: the walk stops at
    /// the mount, while the kernel refuses the crossing with the code it uses for an escape,
    /// and the confined open can only tell the two apart by asking whether the path would
    /// leave the root with crossing allowed — which it would. Both refuse it.
    /// </para>
    /// </remarks>
    private static readonly MountRow[] MountRows =
    [
        new("mount-crossed", "mnt", ResolvedKind.Directory, ConfinedResolveOptions.None, CapErrorCategory.None),
        new("mount-refused", "mnt", ResolvedKind.Directory, ConfinedResolveOptions.RefuseMountCrossing, CapErrorCategory.CrossDevice),
        new("file-in-mount-refused", "mnt/file", ResolvedKind.File, ConfinedResolveOptions.RefuseMountCrossing, CapErrorCategory.CrossDevice),
        new("missing-in-mount-refused", "mnt/missing", ResolvedKind.File, ConfinedResolveOptions.RefuseMountCrossing, CapErrorCategory.CrossDevice),
        new("escape-without-mount", "../outside", ResolvedKind.Directory, ConfinedResolveOptions.RefuseMountCrossing, CapErrorCategory.Escaped),
        new(
            "link-out-of-mount-denied",
            "mnt/climb",
            ResolvedKind.Directory,
            ConfinedResolveOptions.RefuseMountCrossing | ConfinedResolveOptions.RefuseSymlinks,
            CapErrorCategory.CrossDevice),
        new(
            "link-out-of-mount-followed",
            "mnt/climb",
            ResolvedKind.Directory,
            ConfinedResolveOptions.RefuseMountCrossing,
            CapErrorCategory.CrossDevice,
            ConfinedExpected: CapErrorCategory.Escaped),
    ];

    private static void RunMount(SymlinkPolicyBackend backend, string rowName)
    {
        MountRow row = MountRows.Single(candidate => candidate.Name == rowName);
        string prepared = BindMountFixture.Require();

        CapResult<SafeDirHandle> opened = PlatformOps.Host.OpenAmbientDirectory(prepared, CapAccess.Read);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        using SafeDirHandle root = opened.Value!;

        CapError error = backend == SymlinkPolicyBackend.ConfinedOpen
            ? row.Kind == ResolvedKind.Directory
                ? Close(root.Backend.OpenConfinedDirectory(root, row.Path, CapAccess.Read, row.Options))
                : Close(root.Backend.OpenConfinedFile(root, row.Path, FileOpenRequest.Existing(FileAccess.Read), row.Options))
            : Walk(root, row);

        CapErrorCategory expected = backend == SymlinkPolicyBackend.ConfinedOpen
            ? row.ConfinedExpected ?? row.Expected
            : row.Expected;
        Assert.Equal(expected, error.Category);
    }

    private static CapError Walk(SafeDirHandle root, MountRow row)
    {
        Assert.True(
            CapPath.TryParse(row.Path, CapPath.HostSyntax, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError pathError),
            $"'{row.Path}' did not parse ({pathError}).");

        return row.Kind == ResolvedKind.Directory
            ? Close(PortableResolver.OpenDirectory(root, in path, CapAccess.Read, row.Options))
            : Close(PortableResolver.OpenFile(root, in path, FileOpenRequest.Existing(FileAccess.Read), row.Options));
    }

    private static CapError Close<T>(CapResult<T> result)
        where T : System.Runtime.InteropServices.SafeHandle
    {
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        result.Value!.Dispose();
        return CapError.Success;
    }

    /// <param name="ConfinedExpected">
    /// Where the confined open is documented to answer differently, what it answers.
    /// </param>
    private sealed record MountRow(
        string Name,
        string Path,
        ResolvedKind Kind,
        ConfinedResolveOptions Options,
        CapErrorCategory Expected,
        CapErrorCategory? ConfinedExpected = null);

    private void Run(SymlinkPolicyBackend backend, string caseName)
    {
        SymlinkPolicyCase entry = SymlinkPolicyCorpus.Named(caseName);

        RequireSymbolicLinks();

        OnDiskSymlinkTree tree = new(_root);
        if ((entry.Requires & ~tree.Features) != SymlinkTreeFeature.None)
        {
            Assert.Skip($"'{entry.Name}' needs {entry.Requires}, which this host cannot express.");
        }

        SymlinkPolicyCorpus.Build(tree);

        using SafeDirHandle root = OpenSandbox(tree);
        CapError error = SymlinkPolicyCorpus.Resolve(backend, root, entry);

        Assert.Equal(entry.Expected, error.Category);
    }

    private static SafeDirHandle OpenSandbox(OnDiskSymlinkTree tree)
    {
        CapResult<SafeDirHandle> root =
            PlatformOps.Host.OpenAmbientDirectory(tree.SandboxPath, CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);
        return root.Value!;
    }

    /// <summary>
    /// Skips where this host will not let the test process create a symbolic link, or fails
    /// where this run was set up to have them.
    /// </summary>
    private void RequireSymbolicLinks() =>
        HostLinks.Require(HostFeature.Symlinks, () =>
        {
            string probe = Path.Join(_root, "link-probe");
            File.CreateSymbolicLink(probe, "target");
            File.Delete(probe);
        });

    /// <summary>The corpus laid out on a real filesystem.</summary>
    /// <remarks>
    /// The sandbox is a subdirectory of the temporary directory rather than the temporary
    /// directory itself, so that there is somewhere above it for an escape to aim at. A root
    /// with nothing outside it cannot tell a refusal from a miss.
    /// </remarks>
    private sealed class OnDiskSymlinkTree : ISymlinkTree
    {
        private readonly string _outside;

        public OnDiskSymlinkTree(string root)
        {
            SandboxPath = Path.Join(root, "sandbox");
            _outside = Path.Join(root, SymlinkPolicyCorpus.OutsideDirectoryName);
            Directory.CreateDirectory(SandboxPath);
            Directory.CreateDirectory(_outside);
        }

        /// <summary>The directory the cases are resolved beneath.</summary>
        public string SandboxPath { get; }

        /// <summary>
        /// What a real tree can hold. A reparse point that is not a filesystem link is
        /// absent because creating one is not something a test can do — it needs interfaces
        /// and privileges reserved to the components that own those tags — which is why the
        /// simulated filesystem covers those cases instead.
        /// </summary>
        public SymlinkTreeFeature Features =>
            OperatingSystem.IsLinux() ? SymlinkTreeFeature.ProcessFilesystem : SymlinkTreeFeature.None;

        public string AbsoluteOutsideDirectory => _outside;

        public void AddDirectory(string path) => Directory.CreateDirectory(Inside(path));

        public void AddFile(string path)
        {
            string full = Inside(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }

        public void AddLink(string path, string target)
        {
            string full = Inside(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // Created as a file link even where the target is a directory, and even where the
            // case walks through it as one. On Unix there is no difference. On Windows the rest
            // of the system would refuse to traverse such a link, but resolution here reads a
            // link's target whatever kind it was made as, so the table's answers hold for it
            // unchanged -- which is what these cases, run on Windows, check.
            File.CreateSymbolicLink(full, target);
        }

        public void AddNonLinkReparsePoint(string path) =>
            throw new NotSupportedException(
                "A real tree cannot be given a reparse point whose tag is not a filesystem " +
                "link. Cases needing one declare it and are skipped here.");

        public void AddOutsideFile(string name) => File.WriteAllText(Path.Join(_outside, name), "x");

        private string Inside(string path) => Path.Join(SandboxPath, path.Replace('/', Path.DirectorySeparatorChar));
    }
}
