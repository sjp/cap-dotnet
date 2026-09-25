using System.IO.Abstractions;
using Cap.Std;
using Microsoft.Win32.SafeHandles;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// Where <see cref="DirFileSystem"/> parts from <c>System.IO</c> and <c>MockFileSystem</c> on
/// purpose. Each of these is listed in the package documentation.
/// </summary>
public abstract class DifferenceTests : IDisposable
{
    private readonly IFileSystemFixture _fixture;

    protected DifferenceTests(IFileSystemFixture fixture)
    {
        _fixture = fixture;
        Fs = fixture.FileSystem;
    }

    protected IFileSystem Fs { get; }

    private string Root => Fs.Path.GetPathRoot(Fs.Directory.GetCurrentDirectory())!;

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_write_refuses_a_link_at_the_final_name()
    {
        Assert.SkipUnless(_fixture.SupportsLinks, "This process cannot create symbolic links here.");
        Fs.File.WriteAllText("a.txt", "kept");
        Fs.File.CreateSymbolicLink("link.txt", "a.txt");

        CapIOException refused = Assert.Throws<CapIOException>(() => Fs.File.WriteAllText("link.txt", "replaced"));

        Assert.Contains(refused.Kind, new[] { CapErrorKind.SymbolicLink, CapErrorKind.LinkNotFollowed });
        Assert.Equal("kept", Fs.File.ReadAllText("a.txt"));
    }

    [Fact]
    public void A_link_with_a_rooted_target_is_refused()
    {
        Fs.File.WriteAllText("a.txt", "one");

        Assert.Throws<SandboxEscapeException>(() => Fs.File.CreateSymbolicLink("link.txt", Fs.Path.Combine(Root, "a.txt")));
    }

    [Fact]
    public void The_namespace_is_rooted_at_the_directory()
    {
        Assert.Equal(Root, Fs.Directory.GetCurrentDirectory());
        Assert.Equal([Root], Fs.Directory.GetLogicalDrives());
        Assert.Equal(Fs.Path.Combine(Root, "a"), Fs.Path.GetFullPath("a"));
        Assert.True(Fs.Path.IsPathFullyQualified(Root));
        Assert.True(Fs.Directory.Exists(Root));
    }

    [Fact]
    public void The_root_cannot_be_removed()
    {
        Assert.ThrowsAny<IOException>(() => Fs.Directory.Delete(Root));
        Assert.ThrowsAny<IOException>(() => Fs.Directory.Delete(Root, recursive: true));
    }

    [Fact]
    public void Scratch_space_is_a_directory_beneath_the_root()
    {
        string temp = Fs.Path.GetTempPath();
#pragma warning disable CS0618 // The adapter's own GetTempFileName is what is under test.
        string file = Fs.Path.GetTempFileName();
#pragma warning restore CS0618
        IDirectoryInfo made = Fs.Directory.CreateTempSubdirectory("job-");

        Assert.Equal(Fs.Path.Combine(Root, ".tmp") + Fs.Path.DirectorySeparatorChar, temp);
        Assert.True(Fs.Directory.Exists(temp));
        Assert.StartsWith(temp, file, StringComparison.Ordinal);
        Assert.Empty(Fs.File.ReadAllBytes(file));
        Assert.StartsWith("job-", made.Name, StringComparison.Ordinal);
        Assert.True(made.Exists);
        Assert.Equal(Fs.Path.TrimEndingDirectorySeparator(temp), made.Parent!.FullName);
    }

    [Fact]
    public void A_search_pattern_names_entries_in_one_directory()
    {
        Fs.Directory.CreateDirectory("d");

        Assert.Throws<ArgumentException>(() => Fs.Directory.GetFiles(Root, Fs.Path.Combine("d", "*")));
    }

    // Every member is called on every platform on purpose: the adapter refuses it before any
    // platform has a say.
#pragma warning disable CA1416
    [Fact]
    public void Members_with_no_capability_meaning_say_so()
    {
        Fs.File.WriteAllText("a.txt", "one");
        using SafeFileHandle handle = new();

        Assert.Throws<NotSupportedException>(() => Fs.Path.GetRandomFileName());
        Assert.Throws<NotSupportedException>(() => Fs.DriveInfo.GetDrives());
        Assert.Throws<NotSupportedException>(() => Fs.DriveInfo.New("C"));
        Assert.Throws<NotSupportedException>(() => Fs.FileSystemWatcher.New());
        Assert.Throws<NotSupportedException>(() => Fs.FileVersionInfo.GetVersionInfo("a.txt"));
        Assert.Throws<NotSupportedException>(() => Fs.FileInfo.Wrap(new FileInfo("a.txt")));
        Assert.Throws<NotSupportedException>(() => Fs.DirectoryInfo.Wrap(new DirectoryInfo("d")));
        Assert.Throws<NotSupportedException>(() => Fs.FileStream.New(handle, FileAccess.Read));
        Assert.Throws<NotSupportedException>(() => Fs.File.GetAttributes(handle));
        Assert.Throws<NotSupportedException>(() => Fs.File.SetAttributes("a.txt", FileAttributes.Hidden));
        Assert.Throws<NotSupportedException>(() => Fs.File.SetUnixFileMode("a.txt", UnixFileMode.UserRead));
        Assert.Throws<NotSupportedException>(() => Fs.File.SetCreationTimeUtc("a.txt", DateTime.UtcNow));
        Assert.Throws<NotSupportedException>(() => Fs.File.Encrypt("a.txt"));
        Assert.Throws<NotSupportedException>(() => Fs.FileInfo.New("a.txt").IsReadOnly = true);
        Assert.Throws<NotSupportedException>(() => ((IFileSystemAclSupport)Fs.FileInfo.New("a.txt")).GetAccessControl());
    }

    [Fact]
    public void A_creation_mode_is_refused_rather_than_ignored()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix creation modes are not offered on Windows.");

        Assert.Throws<NotSupportedException>(() => Fs.Directory.CreateDirectory("d", UnixFileMode.UserRead));
        Assert.Throws<NotSupportedException>(() => Fs.File.Open(
            "a.txt",
            new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead }));
        Assert.False(Fs.File.Exists("a.txt"));
    }

#pragma warning restore CA1416

    [Fact]
    public void The_file_system_does_not_own_the_directory()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(DirFileSystem)));
    }
}

public sealed class OnDiskDifferenceTests() : DifferenceTests(new DiskFixture());

public sealed class InMemoryDifferenceTests() : DifferenceTests(new MemoryFixture());
