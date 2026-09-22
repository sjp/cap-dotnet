using System.Runtime.InteropServices;
using Cap.Primitives.Interop;

namespace Cap.Primitives.Tests;

/// <summary>
/// The symbolic-link policy, written down once as a table of cases.
/// </summary>
/// <remarks>
/// <para>
/// The policy is the part of this library most likely to be got subtly wrong, because it is
/// the part where the three resolution strategies could each be individually reasonable and
/// still disagree. A tree that resolves on the kernel-atomic backend and is refused by the
/// walk — or, far worse, the other way round — is a difference nobody discovers until a
/// deployment lands on the other one, and then it is discovered either as an outage or as an
/// escape. So the cases live here rather than in any one test class, every backend is driven
/// through the same list, and each asserts the same answer.
/// </para>
/// <para>
/// A case names a path, the policy in force, whether the path is being opened as a directory
/// or as a file, and the one category resolution must report. Nothing is asserted about the
/// platform code underneath, only about the reading this library takes of it, because that
/// reading is what a caller sees and the raw codes legitimately differ.
/// </para>
/// <para>
/// The tree the cases run against is built through <see cref="ISymlinkTree"/>, so the same
/// list can be laid out in a simulated filesystem or on a real disk. The two are not
/// alternatives: the simulation can express shapes no kernel will let a test create, and the
/// disk is the only thing that can catch this library asking the operating system for
/// something the operating system does not mean the same way.
/// </para>
/// </remarks>
internal static class SymlinkPolicyCorpus
{
    /// <summary>
    /// How long the chain that must exceed every budget is.
    /// </summary>
    /// <remarks>
    /// Longer than the forty links this library will follow, and longer than the limits the
    /// kernels apply to their own resolution, so that every backend refuses it for its own
    /// reasons and the table still gets one answer. A chain sized to just one budget would
    /// pass on the backend it was written for and follow the chain on the others.
    /// </remarks>
    private const int OverlongChainLength = 45;

    /// <summary>The name of the directory beside the sandbox that nothing inside may reach.</summary>
    public const string OutsideDirectoryName = "outside";

    /// <summary>A file that exists in that directory.</summary>
    public const string OutsideExistingFile = "secret";

    /// <summary>A name that does not exist in that directory.</summary>
    public const string OutsideAbsentFile = "absent";

    /// <summary>
    /// Builds the tree every case resolves against.
    /// </summary>
    /// <remarks>
    /// One tree for the whole table rather than one per case. A case that built its own
    /// would be testing a path in isolation, and several of the interesting refusals are
    /// about what else is present — a link is only interesting when there is somewhere
    /// outside for it to point at, and "the target exists" is only distinguishable from "the
    /// target does not" when both are in the same tree.
    /// </remarks>
    public static void Build(ISymlinkTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Somewhere outside the sandbox to aim at, with one name that exists and one that
        // does not. A test whose root has nothing outside it cannot tell a refusal from a
        // miss.
        tree.AddOutsideFile(OutsideExistingFile);

        tree.AddDirectory("plain");
        tree.AddFile("plain/marker");
        tree.AddDirectory("deep/inner");
        tree.AddFile("deep/inner/marker");

        // Links that stay inside, in each of the shapes that reach a different part of the
        // resolver: the last component, a component in the middle, a link to a file rather
        // than to a directory, and a target that climbs before descending again.
        tree.AddLink("inside-dir", "plain");
        tree.AddLink("inside-file", "plain/marker");
        tree.AddLink("deep/up", "../plain");

        tree.AddLink("chain-a", "chain-b");
        tree.AddLink("chain-b", "chain-c");
        tree.AddLink("chain-c", "plain");

        for (int i = 0; i < OverlongChainLength; i++)
        {
            tree.AddLink(
                OverlongChainStep(i),
                i == OverlongChainLength - 1 ? "plain" : OverlongChainStep(i + 1));
        }

        tree.AddLink("self-cycle", "self-cycle");
        tree.AddLink("cycle-a", "cycle-b");
        tree.AddLink("cycle-b", "cycle-a");

        // The same escape spelled relatively and absolutely, and in each spelling aimed once
        // at something that exists outside and once at something that does not. The pair is
        // the point: the two must be indistinguishable, or the refusal has become a way to
        // ask what is out there.
        tree.AddLink("escape-dir", $"../{OutsideDirectoryName}");
        tree.AddLink("escape-existing", $"../{OutsideDirectoryName}/{OutsideExistingFile}");
        tree.AddLink("escape-absent", $"../{OutsideDirectoryName}/{OutsideAbsentFile}");
        tree.AddLink("absolute-dir", tree.AbsoluteOutsideDirectory);
        tree.AddLink("absolute-existing", $"{tree.AbsoluteOutsideDirectory}/{OutsideExistingFile}");
        tree.AddLink("absolute-absent", $"{tree.AbsoluteOutsideDirectory}/{OutsideAbsentFile}");

        tree.AddLink("dangling", "no-such-entry");

        if ((tree.Features & SymlinkTreeFeature.NonLinkReparsePoint) != 0)
        {
            tree.AddNonLinkReparsePoint("not-a-link");
        }

        if ((tree.Features & SymlinkTreeFeature.ProcessFilesystem) != 0)
        {
            // The kernel's own synthetic links, which are not filesystem links at all: they
            // jump straight to an open file, a process root or a namespace. They are only
            // nameable absolutely, which is what puts them out of reach here.
            tree.AddLink("magic", "/proc/self/root");
            tree.AddLink("magic-fd", "/proc/self/fd/0");
        }
    }

    /// <summary>The name of one step of the chain that must exceed every budget.</summary>
    public static string OverlongChainStep(int index) => $"overlong-{index:D2}";

    /// <summary>
    /// The table. Each entry must produce its category on every backend.
    /// </summary>
    public static IReadOnlyList<SymlinkPolicyCase> Cases { get; } = BuildCases();

    /// <summary>Finds a case by name, for a theory that is parameterised by name.</summary>
    /// <remarks>
    /// A theory takes the name rather than the case itself because the test framework has to
    /// be able to write its parameters down in order to report and re-run one. A record
    /// carrying a delegate cannot be written down; a string can, and it is what appears in
    /// the failure.
    /// </remarks>
    public static SymlinkPolicyCase Named(string name)
    {
        foreach (SymlinkPolicyCase entry in Cases)
        {
            if (entry.Name == name)
            {
                return entry;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "No such case in the corpus.");
    }

    /// <summary>Every case name, in table order.</summary>
    public static IEnumerable<string> Names
    {
        get
        {
            foreach (SymlinkPolicyCase entry in Cases)
            {
                yield return entry.Name;
            }
        }
    }

    /// <summary>
    /// Resolves one case through one backend, and reports what it made of it.
    /// </summary>
    /// <remarks>
    /// The handle a successful resolution produces is closed immediately. Nothing in the
    /// table asserts anything about what was opened beyond the fact that it opened — the
    /// cases that care which object was reached are the containment tests, which compare
    /// identities rather than categories.
    /// </remarks>
    public static CapError Resolve(SymlinkPolicyBackend backend, SafeDirHandle root, SymlinkPolicyCase entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        ConfinedResolveOptions options = entry.Policy.ToResolveOptions();

        return backend switch
        {
            SymlinkPolicyBackend.ConfinedOpen => entry.Kind == ResolvedKind.Directory
                ? Close(PlatformOps.Current.OpenConfinedDirectory(root, entry.Path, CapAccess.Read, options))
                : Close(PlatformOps.Current.OpenConfinedFile(root, entry.Path, CapAccess.Read, options)),

            _ => ResolveByWalk(root, entry, options),
        };
    }

    private static CapError ResolveByWalk(
        SafeDirHandle root,
        SymlinkPolicyCase entry,
        ConfinedResolveOptions options)
    {
        // Parsed with `..` preserved rather than refused, because the walk is the layer that
        // can take an upward step for real. A caller's path never arrives here that way --
        // the public surface refuses one -- but a link's target can, and the walk has to be
        // driven the same way the library drives it.
        if (!CapPath.TryParse(
                entry.Path,
                CapPath.HostSyntax,
                ParentLinkPolicy.Preserve,
                out CapPath path,
                out CapPathError pathError))
        {
            throw new InvalidOperationException(
                $"The corpus path '{entry.Path}' did not parse ({pathError}). Cases name paths " +
                "the parser accepts; a path that has to be refused before resolution belongs " +
                "in the parser's own tests.");
        }

        return entry.Kind == ResolvedKind.Directory
            ? Close(PortableResolver.OpenDirectory(root, in path, CapAccess.Read, options))
            : Close(PortableResolver.OpenFile(root, in path, CapAccess.Read, options));
    }

    private static CapError Close<T>(CapResult<T> result)
        where T : SafeHandle
    {
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        result.Value!.Dispose();
        return CapError.Success;
    }

    private static List<SymlinkPolicyCase> BuildCases()
    {
        List<SymlinkPolicyCase> cases =
        [
            // A tree with no link in it resolves, which is the control the rest of the table
            // is read against: a refusal is only evidence of policy if the same walk without
            // a link succeeds.
            new("plain-directory", "plain", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),
            new("plain-file", "deep/inner/marker", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),

            // Followed: a link whose resolution stays inside, in each shape.
            new("link-to-directory", "inside-dir", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),
            new("link-to-file", "inside-file", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),
            new("link-as-middle-component", "inside-dir/marker", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),
            new("link-target-climbs-but-stays-inside", "deep/up/marker", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),
            new("chain-within-budget", "chain-a", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),
            new("chain-within-budget-continues", "chain-a/marker", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.None),

            // Refused as a loop: a chain no backend will follow to the end, and the two
            // cycles. A cycle is not distinguishable from an honestly long chain without
            // walking it, which is what the budget exists to avoid.
            new("chain-beyond-budget", OverlongChainStep(0), ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.SymbolicLinkLoop),
            new("self-cycle", "self-cycle", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.SymbolicLinkLoop),
            new("mutual-cycle", "cycle-a", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.SymbolicLinkLoop),

            // Refused as an escape: a target that leaves, relatively or absolutely. Each
            // spelling appears twice, aimed once at a name that exists outside and once at
            // one that does not, and the two must answer identically -- otherwise the
            // refusal has become a way to ask what is out there.
            new("escape-via-parent-link", "escape-dir", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped),
            new("escape-via-parent-link-target-exists", "escape-existing", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped),
            new("escape-via-parent-link-target-absent", "escape-absent", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped),
            new("escape-via-absolute-link", "absolute-dir", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped),
            new("escape-via-absolute-link-target-exists", "absolute-existing", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped),
            new("escape-via-absolute-link-target-absent", "absolute-absent", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped),
            new("escape-through-a-link-then-onwards", "escape-dir/secret", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped),

            // A link pointing at a name that does not exist inside is an ordinary miss.
            new("dangling-link-inside", "dangling", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.NotFound),
            new("dangling-link-inside-as-directory", "dangling", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.NotFound),

            // Denied: every link, wherever it points. A real directory still resolves, which
            // is what makes this a policy about links rather than a broken walk.
            new("denied-plain-directory", "plain", ResolvedKind.Directory,
                SymlinkPolicy.Deny, CapErrorCategory.None),
            new("denied-plain-file", "deep/inner/marker", ResolvedKind.File,
                SymlinkPolicy.Deny, CapErrorCategory.None),
            new("denied-link-to-directory", "inside-dir", ResolvedKind.Directory,
                SymlinkPolicy.Deny, CapErrorCategory.SymbolicLinkLoop),
            new("denied-link-to-file", "inside-file", ResolvedKind.File,
                SymlinkPolicy.Deny, CapErrorCategory.SymbolicLinkLoop),
            new("denied-link-as-middle-component", "inside-dir/marker", ResolvedKind.File,
                SymlinkPolicy.Deny, CapErrorCategory.SymbolicLinkLoop),
            new("denied-chain", "chain-a", ResolvedKind.Directory,
                SymlinkPolicy.Deny, CapErrorCategory.SymbolicLinkLoop),

            // Under a policy that refuses every link, a link that would have escaped is
            // refused as a link and not as an escape. The link is never read, so where it
            // pointed is never learned -- and so a caller cannot use the refusal to tell an
            // escaping link from an ordinary one.
            new("denied-escaping-link", "escape-dir", ResolvedKind.Directory,
                SymlinkPolicy.Deny, CapErrorCategory.SymbolicLinkLoop),
            new("denied-absolute-link", "absolute-dir", ResolvedKind.Directory,
                SymlinkPolicy.Deny, CapErrorCategory.SymbolicLinkLoop),
            new("denied-dangling-link", "dangling", ResolvedKind.File,
                SymlinkPolicy.Deny, CapErrorCategory.SymbolicLinkLoop),

            // Not a filesystem link, so not a policy question: refused under both values.
            new("non-link-reparse-point", "not-a-link", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Reparse,
                SymlinkTreeFeature.NonLinkReparsePoint),
            new("non-link-reparse-point-as-middle-component", "not-a-link/beyond", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Reparse,
                SymlinkTreeFeature.NonLinkReparsePoint),
            new("non-link-reparse-point-denied", "not-a-link", ResolvedKind.Directory,
                SymlinkPolicy.Deny, CapErrorCategory.Reparse,
                SymlinkTreeFeature.NonLinkReparsePoint),

            // Nor are the kernel's synthetic links, which is the published escape route out
            // of directory sandboxes on Linux.
            new("magic-link-to-a-process-root", "magic", ResolvedKind.Directory,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped,
                SymlinkTreeFeature.ProcessFilesystem),
            new("magic-link-to-an-open-descriptor", "magic-fd", ResolvedKind.File,
                SymlinkPolicy.FollowWithinSandbox, CapErrorCategory.Escaped,
                SymlinkTreeFeature.ProcessFilesystem),
        ];

        return cases;
    }
}

/// <summary>
/// One row of the policy table.
/// </summary>
/// <param name="Name">How the case is identified in a failure, and how a theory selects it.</param>
/// <param name="Path">The path resolved beneath the sandbox root.</param>
/// <param name="Kind">Whether the path is opened as a directory or as a file.</param>
/// <param name="Policy">The policy in force.</param>
/// <param name="Expected">
/// The single category every backend must report. <see cref="CapErrorCategory.None"/> means
/// the path must resolve.
/// </param>
/// <param name="Requires">
/// What the tree has to be able to express for the case to mean anything. A tree that cannot
/// skips it rather than passing it vacuously.
/// </param>
internal sealed record SymlinkPolicyCase(
    string Name,
    string Path,
    ResolvedKind Kind,
    SymlinkPolicy Policy,
    CapErrorCategory Expected,
    SymlinkTreeFeature Requires = SymlinkTreeFeature.None);

/// <summary>Which of the two tails of a resolution a case asks for.</summary>
internal enum ResolvedKind
{
    /// <summary>Open the last component as a directory.</summary>
    Directory,

    /// <summary>Open the last component as a file.</summary>
    File,
}

/// <summary>Which strategy a case is being driven through.</summary>
internal enum SymlinkPolicyBackend
{
    /// <summary>
    /// The component-at-a-time walk. Used on macOS, on Linux without the confined open, and
    /// -- with its own per-step native call -- on Windows.
    /// </summary>
    Walk,

    /// <summary>The kernel-atomic confined open, where the platform has one.</summary>
    ConfinedOpen,
}

/// <summary>
/// Shapes a tree has to be able to contain for a case to be meaningful.
/// </summary>
/// <remarks>
/// Declared per case and answered per tree, rather than tested with a platform check inside
/// the case, so that a case which cannot run says which capability it wanted. A platform
/// check would eventually be wrong in the quiet direction: a host that grows the ability
/// would keep skipping.
/// </remarks>
[Flags]
internal enum SymlinkTreeFeature
{
    /// <summary>Nothing beyond directories, files and symbolic links.</summary>
    None = 0,

    /// <summary>
    /// A reparse point whose tag is not a filesystem link. Creating one needs privileges and
    /// interfaces no test has, so only a simulated filesystem offers this.
    /// </summary>
    NonLinkReparsePoint = 1,

    /// <summary>
    /// A real process filesystem, whose entries are the kernel's synthetic links. Only
    /// meaningful where following one would actually go somewhere.
    /// </summary>
    ProcessFilesystem = 2,
}

/// <summary>
/// Somewhere for the corpus to be laid out, whether in memory or on a disk.
/// </summary>
/// <remarks>
/// Paths are relative to the sandbox root and always use <c>/</c>, on every platform. They
/// are scaffolding rather than input to anything under test — what the cases resolve is
/// separate, and goes through the real parser.
/// </remarks>
internal interface ISymlinkTree
{
    /// <summary>What this tree can express.</summary>
    SymlinkTreeFeature Features { get; }

    /// <summary>
    /// The directory beside the sandbox, spelled the way an absolute path is spelled here.
    /// </summary>
    /// <remarks>
    /// Taken from the tree because the spelling is what is under test: a link stores a string
    /// and the platform decides what it means, so the corpus must plant the spelling the
    /// platform would actually produce rather than one chosen for convenience.
    /// </remarks>
    string AbsoluteOutsideDirectory { get; }

    /// <summary>Creates a directory beneath the sandbox, and any missing directories above it.</summary>
    void AddDirectory(string path);

    /// <summary>Creates a file beneath the sandbox.</summary>
    void AddFile(string path);

    /// <summary>Creates a symbolic link beneath the sandbox with the given stored target.</summary>
    void AddLink(string path, string target);

    /// <summary>Creates a reparse point beneath the sandbox whose tag is not a filesystem link.</summary>
    void AddNonLinkReparsePoint(string path);

    /// <summary>Creates a file in the directory beside the sandbox.</summary>
    void AddOutsideFile(string name);
}
