using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// Opening a name without saying which kind it holds reaches the object a file open or a
/// directory open of the same name would reach, reports its kind truly, and follows or refuses
/// links exactly as they do — on every backend, under both policies.
/// </summary>
/// <remarks>
/// The case table drives every path through this open and holds it to the same containment as
/// the others. What is left for here is what the table does not see: that the object reached
/// is the same one, not merely some object that happens to succeed; that a directory opened
/// this way carries the policy of the handle it came from and can list; and that a file opened
/// this way reads.
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class OpenAnyTests
{
    private const string FileLink = "file-link";
    private const string DirectoryLink = "directory-link";
    private const string ClimbingOut = "climbing-out";

    private static readonly string Target = $"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}";

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
    /// A file and a directory are each opened as what they are, and are the objects the
    /// dedicated opens reach.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    public void Each_kind_is_opened_as_what_it_is_and_is_the_same_object(string backend, SymlinkPolicy policy)
    {
        using Arena arena = new();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        using (CapOpened opened = root.OpenAny(Target))
        {
            Assert.False(opened.IsDirectory);
            using CapFile file = opened.TakeFile();
            using CapFile direct = root.OpenFile(Target);

            Assert.Equal(FileAccess.Read, file.Access);
            Assert.Equal(direct.GetMetadata().FileId, file.GetMetadata().FileId);

            byte[] buffer = new byte[64];
            Assert.Equal(direct.Read(buffer, 0), file.Read(new byte[64], 0));
        }

        using (CapOpened opened = root.OpenAny(EscapeCorpus.PlainDirectory))
        {
            Assert.True(opened.IsDirectory);
            using Dir directory = opened.TakeDir();

            Assert.Equal(policy, directory.SymlinkPolicy);
            Assert.Equal(root.GetMetadata(EscapeCorpus.PlainDirectory).FileId, directory.GetMetadata().FileId);
            Assert.Contains(directory.EnumerateEntries(), entry => entry.Name == EscapeCorpus.PlainFile);
        }

        Assert.True(root.TryOpenAny($"{EscapeCorpus.PlainDirectory}/", out CapOpened? spelled));
        using (spelled)
        {
            Assert.True(spelled.IsDirectory);
        }

        Assert.Equal(
            CapErrorKind.NotADirectory,
            Assert.IsType<CapIOException>(
                Assert.ThrowsAny<IOException>(() => root.OpenAny($"{Target}/")), exactMatch: true).Kind);
        Assert.Throws<FileNotFoundException>(() => root.OpenAny("absent"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenAny($"../{EscapeCorpus.OutsideDirectory}"));
        Assert.False(root.TryOpenAny("absent", out _));
    }

    /// <summary>
    /// A final link is followed to what it leads to as far as the policy allows, is refused on
    /// request under either policy, and one that leads out is refused as an escape.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("S17")]
    public void A_final_link_is_followed_or_refused_as_the_dedicated_opens_do(string backend, SymlinkPolicy policy)
    {
        if ((HostFeatures.Current & HostFeature.Symlinks) == 0)
        {
            Assert.Skip("This host or volume cannot hold symbolic links.");
        }

        using Arena arena = new();
        arena.Plant(
        [
            new(SetupKind.FileLink, FileLink, Target),
            new(SetupKind.DirectoryLink, DirectoryLink, EscapeCorpus.PlainDirectory),
            new(SetupKind.FileLink, ClimbingOut, $"../{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideFile}"),
        ]);

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        if (policy == SymlinkPolicy.FollowWithinSandbox)
        {
            using (CapOpened toFile = root.OpenAny(FileLink))
            {
                Assert.False(toFile.IsDirectory);
                using CapFile file = toFile.TakeFile();
                Assert.Equal(root.GetMetadata(Target).FileId, file.GetMetadata().FileId);
            }

            using (CapOpened toDirectory = root.OpenAny(DirectoryLink))
            {
                Assert.True(toDirectory.IsDirectory);
                using Dir directory = toDirectory.TakeDir();
                Assert.Equal(root.GetMetadata(EscapeCorpus.PlainDirectory).FileId, directory.GetMetadata().FileId);
            }

            Assert.Throws<SandboxEscapeException>(() => root.OpenAny(ClimbingOut));
        }
        else
        {
            foreach (string link in new[] { FileLink, DirectoryLink, ClimbingOut })
            {
                AssertLinkNotFollowed(() => root.OpenAny(link).Dispose());
            }
        }

        foreach (string link in new[] { FileLink, DirectoryLink, ClimbingOut })
        {
            AssertLinkNotFollowed(() => root.OpenAny(link, noFollow: true).Dispose());
            Assert.False(root.TryOpenAny(link, noFollow: true, out _));
        }
    }

    private static void AssertLinkNotFollowed(Action action)
    {
        CapIOException refused = Assert.IsType<CapIOException>(Assert.ThrowsAny<IOException>(action), exactMatch: true);
        Assert.Equal(CapErrorKind.LinkNotFollowed, refused.Kind);
    }
}
