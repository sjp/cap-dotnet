using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;
using Cap.Tests.Fakes;

namespace Cap.Primitives.Tests;

/// <summary>
/// The policy table, driven through both resolution strategies against a simulated
/// filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Both strategies, on one machine, from one list of cases. That is the whole purpose: the
/// kernel-atomic backend exists only on Linux and the walk is what everything else runs, so
/// without a simulation each would only ever be exercised on the platforms that have it, and
/// a disagreement between them would be invisible until it reached a deployment.
/// </para>
/// <para>
/// A simulation can also hold shapes a kernel will not let a test create — in particular a
/// reparse point whose tag is not a filesystem link, which needs interfaces and privileges no
/// test has. Those cases are unreachable on disk, and unreachable is not the same as
/// untested.
/// </para>
/// <para>
/// What it cannot do is notice this library asking the operating system for something the
/// operating system does not mean the same way: a flag that is quietly ignored, an error
/// arriving as a different code, a link an open follows after all. That is what the on-disk
/// half of the corpus is for.
/// </para>
/// </remarks>
public sealed class SymlinkPolicyTests
{
    /// <summary>Every case in the table, one theory case each.</summary>
    public static TheoryData<string> Cases => [.. SymlinkPolicyCorpus.Names];

    /// <summary>The component-at-a-time walk answers the table.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_walk_answers_the_policy_table(string caseName) =>
        Run(SymlinkPolicyBackend.Walk, caseName);

    /// <summary>
    /// The confined, kernel-atomic open answers the table identically.
    /// </summary>
    /// <remarks>
    /// Identically is the assertion, and it is made by both theories reading the same
    /// expected value rather than by comparing the two runs against each other. Comparing
    /// them would pass when both were wrong in the same direction, which is the failure a
    /// shared implementation detail produces.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_confined_open_answers_the_policy_table(string caseName) =>
        Run(SymlinkPolicyBackend.ConfinedOpen, caseName);

    /// <summary>
    /// Refusing to follow links does not stop an operation that acts on a link by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are separate axes and this is where that is asserted. Under a policy refusing
    /// every link, resolving <em>through</em> a link is refused — but resolving <em>up to</em>
    /// it, which is what removing a name, reading a link, or asking what a name refers to
    /// without following it all do, still has to work. A handle that could not remove a
    /// symbolic link would be unable to clean up the very entries the policy was chosen to
    /// distrust.
    /// </para>
    /// <para>
    /// Conflating the axes is the classic bug in this area, in both directions: an operation
    /// that follows the last component deletes a link's target instead of the link, and a
    /// policy that stops the last component from being named at all makes the stricter
    /// setting unusable.
    /// </para>
    /// </remarks>
    [Fact]
    public void Denying_links_still_lets_an_operation_act_on_a_link_by_name()
    {
        FakeFileSystem fs = BuildTree(SymlinkTreeFeature.NonLinkReparsePoint, confined: false);
        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenSandbox(ops);

        // Through the link: refused, because the policy refuses to follow one.
        CapResult<SafeDirHandle> followed = PortableResolver.OpenDirectory(
            root, Parse("inside-dir"), CapAccess.Read, SymlinkPolicy.Deny.ToResolveOptions());
        Assert.False(followed.IsSuccess);
        Assert.Equal(CapErrorCategory.SymbolicLinkLoop, followed.Error.Category);

        // Up to the link: allowed, and it hands back the directory the name lives in
        // together with the name, which is exactly what unlinking or reading it needs.
        CapResult<ResolvedParent> named = PortableResolver.ResolveParent(
            root, Parse("inside-dir"), SymlinkPolicy.Deny.ToResolveOptions());
        Assert.True(named.IsSuccess, named.Error.FailureDescription);
        using ResolvedParent resolved = named.Value!;
        Assert.Equal("inside-dir", resolved.Name);

        // And the link is still there to be read, unfollowed, under the same policy.
        CapResult<string> target = ops.ReadChildLink(resolved.Directory, "inside-dir");
        Assert.True(target.IsSuccess, target.Error.FailureDescription);
        Assert.Equal("plain", target.Value);
    }

    /// <summary>
    /// A link that escapes and a link that dangles are told apart, but a link that escapes
    /// is not told apart from another link that escapes to somewhere that does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves matter. Reporting a containment refusal distinctly is what lets an
    /// application log and alert on the paths that tried to leave without matching on message
    /// text, and it reveals nothing a holder of the handle could not find out anyway: the
    /// link is inside the sandbox, so its target could simply be read.
    /// </para>
    /// <para>
    /// What must not be revealed is whether the place it pointed at exists. The refusal is
    /// therefore decided from the target as stored, before anything outside is looked up, so
    /// the answer for a link aimed at a real file outside and one aimed at nothing at all is
    /// the same answer — reached without either lookup happening.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_escaping_link_never_reveals_whether_its_target_exists_to_the_walk() =>
        AssertNoExistenceLeak(SymlinkPolicyBackend.Walk);

    /// <inheritdoc cref="An_escaping_link_never_reveals_whether_its_target_exists_to_the_walk"/>
    [Fact]
    public void An_escaping_link_never_reveals_whether_its_target_exists_to_the_confined_open() =>
        AssertNoExistenceLeak(SymlinkPolicyBackend.ConfinedOpen);

    private static void AssertNoExistenceLeak(SymlinkPolicyBackend backend)
    {
        FakeFileSystem fs = BuildTree(
            SymlinkTreeFeature.NonLinkReparsePoint,
            confined: backend == SymlinkPolicyBackend.ConfinedOpen);

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenSandbox(ops);

        CapError existing = Resolve(backend, root, "escape-via-parent-link-target-exists");
        CapError absent = Resolve(backend, root, "escape-via-parent-link-target-absent");
        Assert.Equal(existing, absent);

        CapError absoluteExisting = Resolve(backend, root, "escape-via-absolute-link-target-exists");
        CapError absoluteAbsent = Resolve(backend, root, "escape-via-absolute-link-target-absent");
        Assert.Equal(absoluteExisting, absoluteAbsent);

        // Distinct from a link that points at nothing inside, which is an ordinary miss.
        CapError dangling = Resolve(backend, root, "dangling-link-inside");
        Assert.NotEqual(existing.Category, dangling.Category);
    }

    private static CapError Resolve(SymlinkPolicyBackend backend, SafeDirHandle root, string caseName) =>
        SymlinkPolicyCorpus.Resolve(backend, root, SymlinkPolicyCorpus.Named(caseName));

    private static void Run(SymlinkPolicyBackend backend, string caseName)
    {
        SymlinkPolicyCase entry = SymlinkPolicyCorpus.Named(caseName);

        if ((entry.Requires & ~SymlinkTreeFeature.NonLinkReparsePoint) != SymlinkTreeFeature.None)
        {
            Assert.Skip(
                $"'{entry.Name}' needs {entry.Requires}, which a simulated filesystem cannot " +
                "stand in for: the point of the case is what the real thing does.");
        }

        FakeFileSystem fs = BuildTree(
            SymlinkTreeFeature.NonLinkReparsePoint,
            confined: backend == SymlinkPolicyBackend.ConfinedOpen);

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenSandbox(ops);

        CapError error = SymlinkPolicyCorpus.Resolve(backend, root, entry);
        Assert.Equal(entry.Expected, error.Category);

        // Only the sandbox root should still be open. Every case here is a resolution a
        // caller controls, so a handle left behind by one of them is a way to spend the
        // process's descriptors from outside -- and the opens that then start failing are
        // somewhere else entirely.
        Assert.Equal(1, ops.OpenHandleCount);
    }

    private static FakeFileSystem BuildTree(SymlinkTreeFeature features, bool confined)
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = confined };
        SymlinkPolicyCorpus.Build(new SimulatedSymlinkTree(fs, features));
        return fs;
    }

    private static SafeDirHandle OpenSandbox(FakePlatformOps ops)
    {
        CapResult<SafeDirHandle> root = ops.OpenAmbientDirectory(SimulatedSymlinkTree.SandboxPath, CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);
        return root.Value!;
    }

    private static CapPath Parse(string raw)
    {
        Assert.True(
            CapPath.TryParse(
                raw, CapPath.HostSyntax, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError error),
            $"'{raw}' did not parse: {error}");
        return path;
    }

    /// <summary>The corpus laid out in a simulated filesystem.</summary>
    /// <remarks>
    /// The sandbox is a subdirectory of the simulated root, so that there is somewhere above
    /// it for an escape to aim at. A root with nothing outside it cannot tell a refusal from
    /// a miss.
    /// </remarks>
    private sealed class SimulatedSymlinkTree : ISymlinkTree
    {
        /// <summary>Where the sandbox root sits in the simulated filesystem.</summary>
        public const string SandboxPath = "sandbox";

        private readonly FakeFileSystem _fileSystem;

        public SimulatedSymlinkTree(FakeFileSystem fileSystem, SymlinkTreeFeature features)
        {
            _fileSystem = fileSystem;
            Features = features;
            _ = fileSystem.AddDirectory(SandboxPath);
            _ = fileSystem.AddDirectory(SymlinkPolicyCorpus.OutsideDirectoryName);
        }

        public SymlinkTreeFeature Features { get; }

        public string AbsoluteOutsideDirectory => $"/{SymlinkPolicyCorpus.OutsideDirectoryName}";

        public void AddDirectory(string path) => _ = _fileSystem.AddDirectory(Inside(path));

        public void AddFile(string path) => _ = _fileSystem.AddFile(Inside(path));

        public void AddLink(string path, string target) => _ = _fileSystem.AddSymbolicLink(Inside(path), target);

        public void AddNonLinkReparsePoint(string path) =>
            _ = _fileSystem.AddOpaqueReparsePoint(Inside(path), ReparseTags.AppExecLink);

        public void AddOutsideFile(string name) =>
            _ = _fileSystem.AddFile($"{SymlinkPolicyCorpus.OutsideDirectoryName}/{name}");

        private static string Inside(string path) => $"{SandboxPath}/{path}";
    }
}
