using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Scratch files against a real filesystem.
/// </summary>
/// <remarks>
/// <para>
/// A scratch file is either unreachable because nothing can name it, or unreachable because
/// nobody can guess the name it has. The first is strictly better and is what the anonymous
/// form asks for; the second is what it falls back to, and the tests here are written to hold
/// for both — the suite runs on systems that offer the first and on systems that do not, and
/// a test that assumed either would be asserting the platform rather than the code.
/// </para>
/// <para>
/// The scratch space these run in is itself a scratch directory, which is the arrangement
/// this library recommends: authority over a private subtree, cleaned up by disposal, with
/// no path ever assembled to reach anything in it.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class CapTempFileTests : IDisposable
{
    private readonly CapTempDir _root = CapTempDir.New(AmbientAuthority.Acquire());

    public void Dispose() => _root.Dispose();

    /// <summary>A named scratch file is there under the name it reports.</summary>
    [Fact]
    public void A_named_scratch_file_is_created_under_the_name_it_reports()
    {
        using CapTempFile temp = CapTempFile.New(_root.Directory);

        Assert.True(temp.HasName);
        Assert.True(_root.Directory.Exists(temp.Name!));
        Assert.StartsWith("cap-", temp.Name!, StringComparison.Ordinal);
        Assert.Equal(28, temp.Name!.Length);
    }

    /// <summary>Two scratch files never share a name.</summary>
    [Fact]
    public void Names_are_not_repeated()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<CapTempFile> files = [];

        try
        {
            for (int i = 0; i < 32; i++)
            {
                CapTempFile temp = CapTempFile.New(_root.Directory);
                files.Add(temp);
                Assert.True(seen.Add(temp.Name!));
            }
        }
        finally
        {
            foreach (CapTempFile file in files)
            {
                file.Dispose();
            }
        }
    }

    /// <summary>What is written to it can be read back through the same handle.</summary>
    [Fact]
    public void A_scratch_file_holds_what_is_written_to_it()
    {
        using CapTempFile temp = CapTempFile.New(_root.Directory);

        temp.File.Write("contents"u8, fileOffset: 0);

        byte[] read = new byte["contents"u8.Length];
        Assert.Equal(read.Length, temp.File.Read(read, fileOffset: 0));
        Assert.Equal("contents"u8.ToArray(), read);
    }

    /// <summary>Disposal removes the name.</summary>
    [Fact]
    public void Disposal_removes_a_named_scratch_file()
    {
        CapTempFile temp = CapTempFile.New(_root.Directory);
        string name = temp.Name!;

        temp.Dispose();

        Assert.False(_root.Directory.Exists(name));
    }

    /// <summary>Keeping it leaves the file behind, under its name.</summary>
    [Fact]
    public void Keeping_a_scratch_file_leaves_it_behind()
    {
        CapTempFile temp = CapTempFile.New(_root.Directory);
        string name = temp.Name!;
        temp.File.Write("evidence"u8, fileOffset: 0);

        temp.Keep();
        temp.Dispose();

        Assert.True(_root.Directory.Exists(name));
        Assert.Equal("evidence"u8.ToArray(), _root.Directory.ReadAllBytes(name));
    }

    /// <summary>Disposing twice does nothing the second time.</summary>
    [Fact]
    public void Disposing_twice_is_harmless()
    {
        CapTempFile temp = CapTempFile.New(_root.Directory);
        string name = temp.Name!;

        temp.Dispose();
        temp.Dispose();

        Assert.False(_root.Directory.Exists(name));
    }

    /// <summary>
    /// The anonymous form works whether or not the system can give it a file with no name.
    /// </summary>
    /// <remarks>
    /// The two outcomes differ in exactly one observable way — whether the directory holds an
    /// entry for it — and in nothing else, which is the property that makes the fallback
    /// usable without a caller branching on the platform.
    /// </remarks>
    [Fact]
    public void An_anonymous_scratch_file_reports_which_kind_it_is_and_works_either_way()
    {
        using CapTempFile temp = CapTempFile.NewAnonymous(_root.Directory);

        temp.File.Write("contents"u8, fileOffset: 0);
        byte[] read = new byte["contents"u8.Length];
        Assert.Equal(read.Length, temp.File.Read(read, fileOffset: 0));
        Assert.Equal("contents"u8.ToArray(), read);

        if (temp.HasName)
        {
            Assert.True(_root.Directory.Exists(temp.Name!));
        }
        else
        {
            Assert.Null(temp.Name);
            Assert.Empty(_root.Directory.EnumerateEntries());
        }
    }

    /// <summary>Whichever kind it is, disposal leaves nothing behind.</summary>
    [Fact]
    public void Disposing_an_anonymous_scratch_file_leaves_nothing()
    {
        CapTempFile temp = CapTempFile.NewAnonymous(_root.Directory);

        temp.Dispose();

        Assert.Empty(_root.Directory.EnumerateEntries());
    }

    /// <summary>A file with no name cannot be kept, because there is nothing to keep it under.</summary>
    [Fact]
    public void Keeping_a_file_with_no_name_keeps_nothing()
    {
        CapTempFile temp = CapTempFile.NewAnonymous(_root.Directory);

        if (temp.HasName)
        {
            temp.Dispose();
            Assert.Skip("This filesystem has no file without a name, so there is nothing to assert.");
            return;
        }

        temp.Keep();
        temp.Dispose();

        Assert.Empty(_root.Directory.EnumerateEntries());
    }

    /// <summary>Closing the handle it was made in does not stop it cleaning up.</summary>
    [Fact]
    public void Closing_the_enclosing_handle_does_not_stop_the_cleanup()
    {
        Dir handed = _root.Directory.Clone();
        CapTempFile temp = CapTempFile.New(handed);
        string name = temp.Name!;
        handed.Dispose();

        temp.Dispose();

        Assert.False(_root.Directory.Exists(name));
    }

    /// <summary>A null directory is refused rather than dereferenced.</summary>
    [Fact]
    public void A_missing_directory_is_refused()
    {
        _ = Assert.Throws<ArgumentNullException>(() => CapTempFile.New(null!));
        _ = Assert.Throws<ArgumentNullException>(() => CapTempFile.NewAnonymous(null!));
    }
}
