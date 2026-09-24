using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// A caller may ask, per call, for a final symbolic link to be followed where the operation
/// would act on the link, or to be refused where the operation would follow it — on every
/// backend, under both policies — and neither request reaches anything a link on the way could
/// not, or changes the policy of any handle.
/// </summary>
/// <remarks>
/// <para>
/// The case table drives every path through these forms as well, and holds them to the same
/// containment. What is left for here is what the table does not see: that following reaches
/// the object a link leads to rather than some other object that happens to succeed, through a
/// chain and through a target that climbs with <c>..</c>; that a following change of times
/// leaves the link's own times alone; that refusing a final link leaves links on the way
/// followed; and that a path ending in a separator still asks for what a final link leads to.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class PerCallFinalLinkTests
{
    private const string FileLink = "file-link";
    private const string DirectoryLink = "directory-link";
    private const string Chain = "chain";
    private const string ClimbingInside = "nested/up-link";
    private const string DanglingLink = "dangling-link";
    private const string ClimbingOut = "climbing-out";
    private const string RootedOut = "rooted-out";
    private const string InnerLink = "inner-link";

    private static readonly string Target = $"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}";

    public static TheoryData<string> OnThisHost => [.. Backends.OnThisHost];

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
    /// Describing, setting the times of and hard-linking a final link on request act on what
    /// the link leads to, through a chain and through a target that climbs back up.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    [Defends("S17")]
    public void Following_a_final_link_on_request_acts_on_what_it_leads_to(string backend)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire());

        CapFileId target = root.GetMetadata(Target).FileId;
        Assert.Equal(CapFileType.Symlink, root.GetMetadata(FileLink).Type);

        foreach (string link in new[] { FileLink, Chain, ClimbingInside })
        {
            CapMetadata followed = root.GetMetadata(link, followLink: true);
            Assert.Equal(CapFileType.File, followed.Type);
            Assert.Equal(target, followed.FileId);
            Assert.True(root.TryGetMetadata(link, followLink: true, out CapMetadata tried));
            Assert.Equal(target, tried.FileId);
        }

        Assert.Equal(
            root.GetMetadata(EscapeCorpus.PlainDirectory).FileId,
            root.GetMetadata(DirectoryLink, followLink: true).FileId);

        DateTimeOffset linkWritten = root.GetMetadata(Chain).LastWriteTime;
        root.SetTimes(Chain, lastWrite: CapFileTime.At(EscapeCorpus.PlantedTime), followLink: true);
        Assert.Equal(EscapeCorpus.PlantedTime, root.GetMetadata(Target).LastWriteTime);
        Assert.Equal(linkWritten, root.GetMetadata(Chain).LastWriteTime);

        Assert.Throws<FileNotFoundException>(() => root.GetMetadata(DanglingLink, followLink: true));
        Assert.False(root.TrySetTimes(DanglingLink, lastWrite: CapFileTime.Now, followLink: true));

        if ((HostFeatures.Current & HostFeature.HardLinks) != 0)
        {
            root.CreateHardLink(Chain, root, EscapeCorpus.LandingName, followLink: true);
            CapMetadata landed = root.GetMetadata(EscapeCorpus.LandingName);
            Assert.Equal(CapFileType.File, landed.Type);
            Assert.Equal(target, landed.FileId);

            Assert.IsType<CapIOException>(
                Assert.ThrowsAny<IOException>(
                    () => root.CreateHardLink(DirectoryLink, root, "directory-landing", followLink: true)),
                exactMatch: true);
        }

        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, root.SymlinkPolicy);
    }

    /// <summary>
    /// A final link followed on request that leads out of the sandbox is refused as an escape,
    /// and nothing outside is described, retimed or linked.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    [Defends("S17")]
    public void Following_a_final_link_that_leads_out_is_refused_as_an_escape(string backend)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();
        string outside = arena.SnapshotOutside();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire());

        foreach (string link in new[] { ClimbingOut, RootedOut })
        {
            Assert.Throws<SandboxEscapeException>(() => root.GetMetadata(link, followLink: true));
            Assert.Throws<SandboxEscapeException>(
                () => root.SetTimes(link, lastWrite: CapFileTime.At(EscapeCorpus.PlantedTime), followLink: true));
            Assert.Throws<SandboxEscapeException>(
                () => root.CreateHardLink(link, root, EscapeCorpus.LandingName, followLink: true));
            Assert.False(root.TryGetMetadata(link, followLink: true, out _));
        }

        Assert.Equal(outside, arena.SnapshotOutside());
        Assert.False(arena.ExistsInside(EscapeCorpus.LandingName));
    }

    /// <summary>
    /// A handle that refuses every link refuses a final one that a call asks to follow, and
    /// says so as a link it would not follow rather than as an escape: asking per call cannot
    /// widen the handle's policy.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    [Defends("S17")]
    public void A_handle_that_refuses_links_refuses_a_final_one_a_call_asks_to_follow(string backend)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();
        string before = arena.SnapshotSandbox();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), SymlinkPolicy.Deny);

        foreach (string link in new[] { FileLink, ClimbingOut })
        {
            AssertLinkNotFollowed(() => root.GetMetadata(link, followLink: true));
            AssertLinkNotFollowed(
                () => root.SetTimes(link, lastWrite: CapFileTime.At(EscapeCorpus.PlantedTime), followLink: true));
            AssertLinkNotFollowed(() => root.CreateHardLink(link, root, EscapeCorpus.LandingName, followLink: true));
        }

        Assert.Equal(before, arena.SnapshotSandbox());
        Assert.Equal(SymlinkPolicy.Deny, root.SymlinkPolicy);
    }

    /// <summary>
    /// Opening without following refuses a link at the name, whatever it leads to and under
    /// either policy, and nothing else.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("S17")]
    public void Opening_without_following_refuses_a_link_at_the_name(string backend, SymlinkPolicy policy)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        foreach (string link in new[] { FileLink, DirectoryLink, DanglingLink, ClimbingOut })
        {
            AssertLinkNotFollowed(() => root.OpenFile(link, noFollow: true).Dispose());
            AssertLinkNotFollowed(() => root.OpenDir(link, noFollow: true).Dispose());
            Assert.False(root.TryOpenDir(link, noFollow: true, out _));
            Assert.False(root.TryOpenFile(
                link, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0,
                append: false, noFollow: true, out _));
        }

        // A plain file and a plain directory open as they always do, and each is refused as the
        // wrong kind rather than as a link.
        using (root.OpenFile(Target, noFollow: true))
        using (Dir plain = root.OpenDir(EscapeCorpus.PlainDirectory, noFollow: true))
        {
            Assert.Equal(policy, plain.SymlinkPolicy);
        }

        Assert.Equal(
            CapErrorKind.NotADirectory,
            Assert.IsType<CapIOException>(
                Assert.ThrowsAny<IOException>(() => root.OpenDir(Target, noFollow: true)), exactMatch: true).Kind);
        // The same call without the request follows the link exactly as far as the policy lets it.
        bool opened = root.TryOpenDir(DirectoryLink, out Dir? viaLink);
        viaLink?.Dispose();
        Assert.Equal(policy == SymlinkPolicy.FollowWithinSandbox, opened);
    }

    /// <summary>
    /// Refusing a final link leaves a link on the way followed, leaves the handle opened free
    /// to follow links beneath it, and still follows a final link in a path that ends in a
    /// separator, which asks for what the link leads to.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    [Defends("S17")]
    public void Opening_without_following_still_follows_links_on_the_way(string backend)
    {
        RequireSymlinks();

        using Arena arena = PlantLinks();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire());

        using (CapFile file = root.OpenFile($"{DirectoryLink}/{EscapeCorpus.PlainFile}", noFollow: true))
        {
            Assert.Equal(root.GetMetadata(Target).FileId, file.GetMetadata().FileId);
        }

        using (Dir through = root.OpenDir($"{DirectoryLink}/", noFollow: true))
        {
            Assert.Equal(root.GetMetadata(EscapeCorpus.PlainDirectory).FileId, through.GetMetadata().FileId);
        }

        using Dir plain = root.OpenDir(EscapeCorpus.PlainDirectory, noFollow: true);
        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, plain.SymlinkPolicy);
        using CapFile inner = plain.OpenFile(InnerLink);
        Assert.Equal(root.GetMetadata(Target).FileId, inner.GetMetadata().FileId);
    }

    private static void AssertLinkNotFollowed(Action action)
    {
        CapIOException refused = Assert.IsType<CapIOException>(Assert.ThrowsAny<IOException>(action), exactMatch: true);
        Assert.Equal(CapErrorKind.LinkNotFollowed, refused.Kind);
    }

    private static Arena PlantLinks()
    {
        Arena arena = new();
        arena.Plant(
        [
            new(SetupKind.FileLink, FileLink, Target),
            new(SetupKind.DirectoryLink, DirectoryLink, EscapeCorpus.PlainDirectory),
            new(SetupKind.FileLink, Chain, FileLink),
            new(SetupKind.Directory, "nested"),
            new(SetupKind.FileLink, ClimbingInside, $"../{Target}"),
            new(SetupKind.FileLink, DanglingLink, $"{EscapeCorpus.PlainDirectory}/absent"),
            new(SetupKind.FileLink, ClimbingOut, $"../{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideFile}"),
            new(SetupKind.FileLink, RootedOut, $"{{outside}}/{EscapeCorpus.OutsideFile}"),
            new(SetupKind.FileLink, $"{EscapeCorpus.PlainDirectory}/{InnerLink}", EscapeCorpus.PlainFile),
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
