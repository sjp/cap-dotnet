using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// An open that may create or empty a file refuses a symbolic link at the name it is given,
/// in every mode, on every backend, under both policies — and the link's target is left as it
/// was.
/// </summary>
/// <remarks>
/// <para>
/// The attack is not an escape. The links here all stay inside, so containment would hold
/// either way. What is at stake is which file inside gets written: a component that stores
/// uploads under a client-chosen name, in a directory untrusted code can also write into,
/// would otherwise truncate whatever file a planted link leads to, or create a file wherever a
/// dangling one names.
/// </para>
/// <para>
/// The case table drives only one of these modes, through <c>CreateFile</c>. Each mode reaches
/// the last component by a different native disposition, and on Windows an emptying mode is
/// taken in two halves, so each is driven here on its own.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class FinalLinkWriteTests
{
    private const string FileLink = "file-link";
    private const string DirectoryLink = "directory-link";
    private const string DanglingLink = "dangling-link";
    private const string DanglingTarget = "plain/absent";

    private static readonly FileMode[] WritingModes =
        [FileMode.Create, FileMode.CreateNew, FileMode.Truncate, FileMode.OpenOrCreate, FileMode.Append];

    private static readonly string[] LinkNames = [FileLink, DirectoryLink, DanglingLink];

    public static TheoryData<string, SymlinkPolicy, FileMode, string> ModesAndLinks
    {
        get
        {
            TheoryData<string, SymlinkPolicy, FileMode, string> rows = [];
            foreach (string backend in Backends.OnThisHost)
            {
                foreach (SymlinkPolicy policy in new[] { SymlinkPolicy.FollowWithinSandbox, SymlinkPolicy.Deny })
                {
                    foreach (FileMode mode in WritingModes)
                    {
                        foreach (string link in LinkNames)
                        {
                            rows.Add(backend, policy, mode, link);
                        }
                    }
                }
            }

            return rows;
        }
    }

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

    public static TheoryData<string> OnThisHost => [.. Backends.OnThisHost];

    /// <summary>
    /// Each mode that may create or empty a file refuses a link at the name, and nothing
    /// beneath the root changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModesAndLinks))]
    [Defends("S15")]
    public void A_mode_that_creates_or_truncates_refuses_a_link_at_the_name(
        string backend, SymlinkPolicy policy, FileMode mode, string link)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();
        string before = arena.SnapshotSandbox();
        string outside = arena.SnapshotOutside();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        Assert.IsType<CapIOException>(
            Assert.ThrowsAny<IOException>(() => root.OpenFile(link, mode, FileAccess.Write)),
            exactMatch: true);

        Assert.Equal(before, arena.SnapshotSandbox());
        Assert.Equal(outside, arena.SnapshotOutside());
        Assert.False(arena.ExistsInside(DanglingTarget));
    }

    /// <summary>
    /// The writes that create or empty a file by their nature refuse a link at the name the
    /// same way.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("S15")]
    public async Task The_whole_file_writes_refuse_a_link_at_the_name(string backend, SymlinkPolicy policy)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();
        string before = arena.SnapshotSandbox();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        foreach (string link in LinkNames)
        {
            Assert.IsType<CapIOException>(
                Assert.ThrowsAny<IOException>(() => root.CreateFile(link)), exactMatch: true);
            Assert.IsType<CapIOException>(
                Assert.ThrowsAny<IOException>(() => root.CreateNewFile(link)), exactMatch: true);
            Assert.IsType<CapIOException>(
                Assert.ThrowsAny<IOException>(() => root.WriteAllBytes(link, "replaced"u8)), exactMatch: true);
            Assert.IsType<CapIOException>(
                await Assert.ThrowsAnyAsync<IOException>(
                    () => root.WriteAllBytesAsync(link, "replaced"u8.ToArray(), TestContext.Current.CancellationToken)),
                exactMatch: true);
            Assert.False(root.TryCreateFile(link, out _));
        }

        Assert.Equal(before, arena.SnapshotSandbox());
        Assert.False(arena.ExistsInside(DanglingTarget));
    }

    /// <summary>
    /// An open of an existing file for writing still follows a link at the name that stays
    /// inside: it neither creates nor empties anything, so there is nothing to steer.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void Opening_an_existing_file_to_write_still_follows_a_link_at_the_name(string backend)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire());

        using (CapFile file = root.OpenFile(FileLink, FileMode.Open, FileAccess.Write))
        {
            file.Write("I"u8, 0);
        }

        Assert.Equal("Inside", root.ReadAllText($"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}"));
        Assert.Equal("plain/marker", root.ReadLink(FileLink).Replace('\\', '/'));
    }

    /// <summary>
    /// A link before the last component is still followed by a write, under the policy that
    /// follows links inside: only the name itself is held to the stricter rule.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void A_write_still_passes_through_a_link_before_the_name(string backend)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire());

        root.WriteAllBytes($"{DirectoryLink}/written", "through"u8);

        Assert.Equal("through", HostFile.ReadAllText(Path.Join(arena.SandboxPath, EscapeCorpus.PlainDirectory, "written")));
    }

    private static Arena PlantLinks()
    {
        Arena arena = new();
        arena.Plant(
        [
            new(SetupKind.FileLink, FileLink, $"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}"),
            new(SetupKind.DirectoryLink, DirectoryLink, EscapeCorpus.PlainDirectory),
            new(SetupKind.FileLink, DanglingLink, DanglingTarget),
        ]);
        return arena;
    }

    private static void RequireSymlinks()
    {
        if ((HostFeatures.Current & HostFeature.Symlinks) == 0)
        {
            Assert.Skip("This host or volume cannot hold symbolic links.");
        }
    }
}
