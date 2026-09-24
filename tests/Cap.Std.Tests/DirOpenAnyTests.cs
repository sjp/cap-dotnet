using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Opening whatever a name holds, and the handing over of what was opened.
/// </summary>
/// <remarks>
/// Containment, links and backends are covered with the escape corpus. What is here is the
/// shape of the result: that it holds one handle of the right kind, gives it up once, and
/// closes it if nobody takes it.
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirOpenAnyTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    /// <summary>A file comes back as a file, open to read, and a directory as a directory.</summary>
    [Fact]
    public void Each_kind_comes_back_as_what_it_is()
    {
        File.WriteAllText(Host("data"), "contents");
        Directory.CreateDirectory(Host("sub"));
        File.WriteAllText(Host("sub", "inner"), "");

        using Dir root = OpenRoot();

        using (CapOpened opened = root.OpenAny("data"))
        {
            Assert.False(opened.IsDirectory);
            using CapFile file = opened.TakeFile();
            Assert.Equal(FileAccess.Read, file.Access);
            Assert.Equal(8, file.Length);
        }

        using (CapOpened opened = root.OpenAny("sub"))
        {
            Assert.True(opened.IsDirectory);
            using Dir sub = opened.TakeDir();
            Assert.Equal(["inner"], sub.EnumerateEntries().Select(entry => entry.Name));
        }
    }

    /// <summary>
    /// The handle is given up once; asking for it again, or for the other kind, is a mistake
    /// reported as one.
    /// </summary>
    [Fact]
    public void The_handle_is_taken_once_and_only_as_its_own_kind()
    {
        File.WriteAllText(Host("data"), "contents");
        Directory.CreateDirectory(Host("sub"));

        using Dir root = OpenRoot();

        using (CapOpened file = root.OpenAny("data"))
        {
            Assert.Throws<InvalidOperationException>(() => file.TakeDir());
            using CapFile taken = file.TakeFile();
            Assert.Throws<InvalidOperationException>(() => file.TakeFile());
        }

        using (CapOpened directory = root.OpenAny("sub"))
        {
            Assert.Throws<InvalidOperationException>(() => directory.TakeFile());
            using Dir taken = directory.TakeDir();
            Assert.Throws<InvalidOperationException>(() => directory.TakeDir());
        }
    }

    /// <summary>
    /// Disposing leaves a handle already taken open, and afterwards hands nothing out, while
    /// still saying what was opened.
    /// </summary>
    [Fact]
    public void Disposing_leaves_a_taken_handle_open_and_hands_nothing_out_after()
    {
        File.WriteAllText(Host("data"), "contents");
        Directory.CreateDirectory(Host("sub"));

        using Dir root = OpenRoot();

        CapOpened taken = root.OpenAny("data");
        using CapFile file = taken.TakeFile();
        taken.Dispose();
        Assert.Equal(8, file.Length);

        CapOpened untaken = root.OpenAny("sub");
        untaken.Dispose();
        Assert.True(untaken.IsDirectory);
        Assert.Throws<ObjectDisposedException>(() => untaken.TakeDir());
    }

    /// <summary>
    /// A sharing request that could never mean anything is refused as an argument, as it is
    /// by a file open.
    /// </summary>
    [Fact]
    public void A_meaningless_request_is_refused_as_an_argument()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();

        Assert.Throws<ArgumentException>(() => root.OpenAny("data", FileShare.Inheritable));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.OpenAny("data", options: (FileOptions)0x1));
        Assert.Throws<ArgumentNullException>(() => root.OpenAny(null!));
    }

    /// <summary>
    /// A path naming nothing is refused as an argument, and a rooted one as an escape, before
    /// anything is looked up.
    /// </summary>
    [Fact]
    public void An_unusable_path_is_refused()
    {
        using Dir root = OpenRoot();

        Assert.ThrowsAny<ArgumentException>(() => root.OpenAny(""));
        Assert.Throws<SandboxEscapeException>(() => root.OpenAny(_tree.HostPath));
        Assert.False(root.TryOpenAny(_tree.HostPath, out _));
    }
}
