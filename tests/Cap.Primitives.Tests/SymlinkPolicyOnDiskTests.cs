using Cap.Primitives.Interop;

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
        if (!PlatformOps.Current.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip("This kernel has no confined, atomic open; the walk theory covers the host.");
        }

        Run(SymlinkPolicyBackend.ConfinedOpen, caseName);
    }

    private void Run(SymlinkPolicyBackend backend, string caseName)
    {
        SymlinkPolicyCase entry = SymlinkPolicyCorpus.Named(caseName);

        if (!CanCreateSymbolicLinks())
        {
            Assert.Skip(
                "Symbolic links cannot be created here, so the tree these cases attack " +
                "cannot be built. On Windows that needs either developer mode or an " +
                "elevated token.");
        }

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
            PlatformOps.Current.OpenAmbientDirectory(tree.SandboxPath, CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);
        return root.Value!;
    }

    /// <summary>
    /// Whether this host lets the test process create a symbolic link, established by
    /// creating one.
    /// </summary>
    /// <remarks>
    /// Asked of the filesystem rather than derived from the platform. Windows needs either
    /// developer mode or an elevated token, and which of those a machine has is not something
    /// a platform check can answer — a check that assumed the answer would keep skipping the
    /// heart of the corpus on a machine perfectly able to run it.
    /// </remarks>
    private bool CanCreateSymbolicLinks()
    {
        string probe = Path.Join(_root, "link-probe");

        try
        {
            File.CreateSymbolicLink(probe, "target");
            File.Delete(probe);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

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

            // Created as a file link rather than a directory link even where the target is a
            // directory. On Unix there is no difference; on Windows there is, and the cases
            // that care about the difference are the junction cases, which live with the rest
            // of the Windows reparse tests.
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
