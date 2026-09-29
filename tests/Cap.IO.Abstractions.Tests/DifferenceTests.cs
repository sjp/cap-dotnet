using System.IO.Abstractions;
using Cap.Std;
using Microsoft.Win32.SafeHandles;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// Where <see cref="DirFileSystem"/> parts from <c>System.IO</c> and <c>MockFileSystem</c> on
/// purpose. Each of these is listed in the package documentation, and each test carries the
/// row it proves as a <see cref="Difference"/> trait.
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
    [Trait(Difference.Name, Difference.WriteThroughLink)]
    public void A_write_refuses_a_link_at_the_final_name()
    {
        TestLinks.Require(_fixture.SupportsLinks);
        Fs.File.WriteAllText("a.txt", "kept");
        Fs.File.CreateSymbolicLink("link.txt", "a.txt");
        Fs.File.CreateSymbolicLink("dangling.txt", "missing.txt");

        CapIOException refused = Assert.Throws<CapIOException>(() => Fs.File.WriteAllText("link.txt", "replaced"));

        Assert.Contains(refused.Kind, new[] { CapErrorKind.SymbolicLink, CapErrorKind.LinkNotFollowed });
        Assert.Throws<CapIOException>(() => Fs.File.AppendAllText("link.txt", "added"));
        Assert.Throws<CapIOException>(() => Fs.File.WriteAllText("dangling.txt", "created"));
        Assert.Equal("kept", Fs.File.ReadAllText("a.txt"));
        Assert.False(Fs.File.Exists("missing.txt"));
    }

    [Fact]
    [Trait(Difference.Name, Difference.LinkTargets)]
    public void A_link_with_a_rooted_target_is_refused()
    {
        Fs.File.WriteAllText("a.txt", "one");

        Assert.Throws<SandboxEscapeException>(() => Fs.File.CreateSymbolicLink("link.txt", Fs.Path.Combine(Root, "a.txt")));
    }

    [Fact]
    [Trait(Difference.Name, Difference.ResolveRootedLink)]
    public void Resolving_a_link_with_a_rooted_target_is_refused()
    {
        TestLinks.Require(_fixture.SupportsLinks);
        Fs.File.WriteAllText("a.txt", "one");
        _fixture.AddRootedLink("link.txt");

        Assert.Throws<SandboxEscapeException>(() => Fs.File.ResolveLinkTarget("link.txt", returnFinalTarget: false));
        Assert.Throws<SandboxEscapeException>(() => Fs.File.ResolveLinkTarget("link.txt", returnFinalTarget: true));
    }

    [Fact]
    [Trait(Difference.Name, Difference.DoubleSeparator)]
    public void A_path_beginning_with_two_separators_is_refused()
    {
        Fs.File.WriteAllText("x", "one");

        Assert.Throws<SandboxEscapeException>(() => Fs.File.ReadAllText("//x"));
        Assert.False(Fs.File.Exists("//x"));
    }

    [Fact]
    [Trait(Difference.Name, Difference.FullNames)]
    [Trait(Difference.Name, Difference.LogicalDrives)]
    public void The_namespace_is_rooted_at_the_directory()
    {
        Assert.Equal(Root, Fs.Directory.GetCurrentDirectory());
        Assert.Equal([Root], Fs.Directory.GetLogicalDrives());
        Assert.Equal(Fs.Path.Combine(Root, "a"), Fs.Path.GetFullPath("a"));
        Assert.Equal(Fs.Path.Combine(Root, "a"), Fs.Path.GetFullPath(Fs.Path.Combine("..", "..", "a")));
        Assert.Equal(Root, Fs.Directory.GetParent(Fs.Path.Combine("..", "a"))!.FullName);
        Assert.Equal(Root, Fs.Directory.GetDirectoryRoot(Fs.Path.Combine("..", "a")));
        Assert.True(Fs.Path.IsPathFullyQualified(Root));
        Assert.True(Fs.Directory.Exists(Root));
    }

    [Fact]
    [Trait(Difference.Name, Difference.CurrentDirectory)]
    public void The_current_directory_belongs_to_one_instance()
    {
        string process = Directory.GetCurrentDirectory();
        DirFileSystem other = new(((DirFileSystem)Fs).Dir);
        Fs.Directory.CreateDirectory("d");

        Fs.Directory.SetCurrentDirectory("d");

        Assert.Equal(Fs.Path.Combine(Root, "d"), Fs.Directory.GetCurrentDirectory());
        Assert.Equal(Root, other.Directory.GetCurrentDirectory());
        Assert.Equal(process, Directory.GetCurrentDirectory());
    }

    [Fact]
    [Trait(Difference.Name, Difference.RootFixed)]
    public void The_root_cannot_be_removed_or_moved()
    {
        Fs.Directory.CreateDirectory("d");
        Fs.File.WriteAllText("a.txt", "one");

        Assert.ThrowsAny<IOException>(() => Fs.Directory.Delete(Root));
        Assert.ThrowsAny<IOException>(() => Fs.Directory.Delete(Root, recursive: true));
        Assert.ThrowsAny<IOException>(() => Fs.Directory.Move(Root, "moved"));
        Assert.ThrowsAny<IOException>(() => Fs.Directory.Move("d", Root));
        Assert.ThrowsAny<IOException>(() => Fs.File.Move("a.txt", Root));
        Assert.True(Fs.Directory.Exists("d"));
        Assert.Equal("one", Fs.File.ReadAllText("a.txt"));
        Assert.False(Fs.Directory.Exists("moved"));
    }

    [Fact]
    [Trait(Difference.Name, Difference.MoveIntoMissing)]
    public void Moving_a_directory_into_a_missing_directory_names_the_destination()
    {
        Fs.Directory.CreateDirectory("d");

        DirectoryNotFoundException e = Assert.Throws<DirectoryNotFoundException>(
            () => Fs.Directory.Move("d", Fs.Path.Combine("missing", "e")));

        Assert.Contains(Fs.Path.Combine(Root, "missing", "e"), e.Message, StringComparison.Ordinal);
        Assert.True(Fs.Directory.Exists("d"));
    }

    [Fact]
    [Trait(Difference.Name, Difference.TempPath)]
    [Trait(Difference.Name, Difference.TempFileName)]
    [Trait(Difference.Name, Difference.TempSubdirectory)]
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
    [Trait(Difference.Name, Difference.SearchPatterns)]
    public void A_search_pattern_names_entries_in_one_directory()
    {
        Fs.Directory.CreateDirectory("d");

        Assert.Throws<ArgumentException>(() => Fs.Directory.GetFiles(Root, Fs.Path.Combine("d", "*")));
    }

    [Fact]
    [Trait(Difference.Name, Difference.SearchPatterns)]
    public void Special_directories_are_never_returned()
    {
        Fs.Directory.CreateDirectory("d");
        EnumerationOptions options = new() { ReturnSpecialDirectories = true };

        Assert.Equal([Fs.Path.Combine(Root, "d")], Fs.Directory.GetDirectories(Root, "*", options));
        Assert.Equal([Fs.Path.Combine(Root, "d")], Fs.Directory.GetFileSystemEntries(Root, "*", options));
        Assert.Equal(["d"], Fs.DirectoryInfo.New(Root).GetDirectories("*", options).Select(info => info.Name));
    }

    [Fact]
    [Trait(Difference.Name, Difference.RecursiveEnumeration)]
    public void A_recursive_enumeration_reports_a_link_by_where_it_leads()
    {
        TestLinks.Require(_fixture.SupportsLinks);
        Fs.Directory.CreateDirectory("d");
        Fs.File.WriteAllText(Fs.Path.Combine("d", "f.txt"), "one");
        Fs.Directory.CreateSymbolicLink("in", "d");
        Fs.Directory.CreateSymbolicLink("out", Fs.Path.Combine("..", "elsewhere"));

        string[] directories = Fs.Directory.GetDirectories(Root, "*", SearchOption.AllDirectories);
        string[] files = Fs.Directory.GetFiles(Root, "*", SearchOption.AllDirectories);

        Assert.Equal([Fs.Path.Combine(Root, "d"), Fs.Path.Combine(Root, "in")], directories.Order(StringComparer.Ordinal));
        Assert.Equal([Fs.Path.Combine(Root, "d", "f.txt"), Fs.Path.Combine(Root, "out")], files.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(Difference.Name, Difference.Copy)]
    public void Copying_a_file_copies_its_contents_and_not_its_permissions()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions are what System.IO copies.");
        Fs.File.WriteAllText("a.txt", "one");
        const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        using (CapFile source = ((DirFileSystem)Fs).Dir.OpenFile("a.txt", FileMode.Open, FileAccess.Write))
        {
            source.SetPermissions(CapPermissions.FromUnixMode(executable));
        }

        Fs.File.Copy("a.txt", "b.txt");

        Assert.Equal("one", Fs.File.ReadAllText("b.txt"));
#pragma warning disable CA1416 // Skipped on Windows above.
        Assert.Equal(executable, Fs.File.GetUnixFileMode("a.txt"));
        Assert.False(Fs.File.GetUnixFileMode("b.txt").HasFlag(UnixFileMode.UserExecute));
#pragma warning restore CA1416
    }

    [Fact]
    [Trait(Difference.Name, Difference.CreateSubdirectoryAbove)]
    public void A_subdirectory_may_climb_out_of_its_directory_but_not_the_root()
    {
        Fs.Directory.CreateDirectory(Fs.Path.Combine("d", "e"));
        IDirectoryInfo info = Fs.DirectoryInfo.New(Fs.Path.Combine("d", "e"));

        IDirectoryInfo made = info.CreateSubdirectory(Fs.Path.Combine("..", "x"));

        Assert.Equal(Fs.Path.Combine(Root, "d", "x"), made.FullName);
        Assert.True(Fs.Directory.Exists(Fs.Path.Combine("d", "x")));
        Assert.Throws<SandboxEscapeException>(() => info.CreateSubdirectory(Fs.Path.Combine("..", "..", "..", "x")));
    }

    // Every member is called on every platform on purpose: the adapter refuses it before any
    // platform has a say.
#pragma warning disable CA1416
    [Fact]
    [Trait(Difference.Name, Difference.Permissions)]
    public void Changing_permissions_or_attributes_is_refused_unless_nothing_changes()
    {
        Fs.File.WriteAllText("a.txt", "one");
        Fs.Directory.CreateDirectory("d");
        IFileInfo file = Fs.FileInfo.New("a.txt");
        IDirectoryInfo directory = Fs.DirectoryInfo.New("d");

        Refused(() => Fs.File.SetAttributes("a.txt", FileAttributes.Hidden));
        Refused(() => Fs.File.SetUnixFileMode("a.txt", UnixFileMode.UserRead));
        Refused(() => file.Attributes = FileAttributes.Hidden);
        Refused(() => directory.Attributes = FileAttributes.Hidden);
        Refused(() => file.IsReadOnly = !file.IsReadOnly);
        Refused(() => file.UnixFileMode = UnixFileMode.UserRead);
        Refused(() => directory.UnixFileMode = UnixFileMode.UserRead);

        file.Attributes = file.Attributes;
        directory.Attributes = directory.Attributes;
        file.IsReadOnly = file.IsReadOnly;
    }

    [Fact]
    [Trait(Difference.Name, Difference.CreationMode)]
    public void A_creation_mode_is_refused_rather_than_ignored()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix creation modes are not offered on Windows.");

        Refused(() => Fs.Directory.CreateDirectory("d", UnixFileMode.UserRead));
        Refused(() => Fs.File.Open(
            "a.txt",
            new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead }));
        Assert.False(Fs.File.Exists("a.txt"));
        Assert.False(Fs.Directory.Exists("d"));
    }

    [Fact]
    [Trait(Difference.Name, Difference.CreationTime)]
    public void Setting_a_creation_time_is_refused()
    {
        Fs.File.WriteAllText("a.txt", "one");
        Fs.Directory.CreateDirectory("d");
        IFileInfo file = Fs.FileInfo.New("a.txt");
        IDirectoryInfo directory = Fs.DirectoryInfo.New("d");
        DateTime when = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        Refused(() => Fs.File.SetCreationTime("a.txt", when));
        Refused(() => Fs.File.SetCreationTimeUtc("a.txt", when));
        Refused(() => Fs.Directory.SetCreationTime("d", when));
        Refused(() => Fs.Directory.SetCreationTimeUtc("d", when));
        Refused(() => file.CreationTime = when);
        Refused(() => file.CreationTimeUtc = when);
        Refused(() => directory.CreationTime = when);
        Refused(() => directory.CreationTimeUtc = when);

        Fs.File.SetLastWriteTimeUtc("a.txt", when);
        Fs.Directory.SetLastAccessTimeUtc("d", when);
        Assert.Equal(when, Fs.File.GetLastWriteTimeUtc("a.txt"));
        Assert.Equal(when, Fs.Directory.GetLastAccessTimeUtc("d"));
    }

    [Fact]
    [Trait(Difference.Name, Difference.Handles)]
    public void Members_that_take_a_handle_or_wrap_a_host_object_are_refused()
    {
        using SafeFileHandle handle = new();
        DateTime when = DateTime.UtcNow;
        CancellationToken none = CancellationToken.None;

        Refused(() => Fs.File.GetAttributes(handle));
        Refused(() => Fs.File.SetAttributes(handle, FileAttributes.Normal));
        Refused(() => Fs.File.GetUnixFileMode(handle));
        Refused(() => Fs.File.SetUnixFileMode(handle, UnixFileMode.UserRead));
        Refused(() => Fs.File.GetCreationTime(handle));
        Refused(() => Fs.File.GetCreationTimeUtc(handle));
        Refused(() => Fs.File.GetLastAccessTime(handle));
        Refused(() => Fs.File.GetLastAccessTimeUtc(handle));
        Refused(() => Fs.File.GetLastWriteTime(handle));
        Refused(() => Fs.File.GetLastWriteTimeUtc(handle));
        Refused(() => Fs.File.SetCreationTime(handle, when));
        Refused(() => Fs.File.SetCreationTimeUtc(handle, when));
        Refused(() => Fs.File.SetLastAccessTime(handle, when));
        Refused(() => Fs.File.SetLastAccessTimeUtc(handle, when));
        Refused(() => Fs.File.SetLastWriteTime(handle, when));
        Refused(() => Fs.File.SetLastWriteTimeUtc(handle, when));
        Refused(() => Fs.FileStream.New(handle, FileAccess.Read));
        Refused(() => Fs.FileStream.New(handle, FileAccess.Read, 4096));
        Refused(() => Fs.FileStream.New(handle, FileAccess.Read, 4096, isAsync: false));
        Refused(() => Fs.RandomAccess.FlushToDisk(handle));
        Refused(() => Fs.RandomAccess.GetLength(handle));
        Refused(() => Fs.RandomAccess.Read(handle, Span<byte>.Empty, 0));
        Refused(() => Fs.RandomAccess.Read(handle, Array.Empty<Memory<byte>>(), 0));
        Refused(() => Fs.RandomAccess.ReadAsync(handle, Memory<byte>.Empty, 0, none).AsTask());
        Refused(() => Fs.RandomAccess.ReadAsync(handle, Array.Empty<Memory<byte>>(), 0, none).AsTask());
        Refused(() => Fs.RandomAccess.SetLength(handle, 0));
        Refused(() => Fs.RandomAccess.Write(handle, ReadOnlySpan<byte>.Empty, 0));
        Refused(() => Fs.RandomAccess.Write(handle, Array.Empty<ReadOnlyMemory<byte>>(), 0));
        Refused(() => Fs.RandomAccess.WriteAsync(handle, ReadOnlyMemory<byte>.Empty, 0, none).AsTask());
        Refused(() => Fs.RandomAccess.WriteAsync(handle, Array.Empty<ReadOnlyMemory<byte>>(), 0, none).AsTask());
        Refused(() => Fs.FileInfo.Wrap(new FileInfo("a.txt")));
        Refused(() => Fs.DirectoryInfo.Wrap(new DirectoryInfo("d")));
        Refused(() => Fs.FileStream.Wrap(null!));

        Assert.Null(Fs.FileInfo.Wrap(null));
        Assert.Null(Fs.DirectoryInfo.Wrap(null));
    }

    [Fact]
    [Trait(Difference.Name, Difference.OpenHandle)]
    public void Opening_a_handle_is_refused()
    {
        Fs.File.WriteAllText("a.txt", "one");

        Refused(() => Fs.File.OpenHandle("a.txt"));
        Refused(() => Fs.File.OpenHandle("a.txt", FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0));
    }

    [Fact]
    [Trait(Difference.Name, Difference.HostServices)]
    public void Members_with_no_capability_meaning_say_so()
    {
        Fs.File.WriteAllText("a.txt", "one");
        Fs.Directory.CreateDirectory("d");
        IFileInfo file = Fs.FileInfo.New("a.txt");
        IDirectoryInfo directory = Fs.DirectoryInfo.New("d");

        Refused(() => Fs.DriveInfo.GetDrives());
        Refused(() => Fs.DriveInfo.New("C"));
        Refused(() => Fs.DriveInfo.Wrap(null));
        Refused(() => Fs.FileSystemWatcher.New());
        Refused(() => Fs.FileSystemWatcher.New("d"));
        Refused(() => Fs.FileSystemWatcher.New("d", "*"));
        Refused(() => Fs.FileSystemWatcher.Wrap(null));
        Refused(() => Fs.FileVersionInfo.GetVersionInfo("a.txt"));
        Refused(() => Fs.File.Encrypt("a.txt"));
        Refused(() => Fs.File.Decrypt("a.txt"));
        Refused(() => file.Encrypt());
        Refused(() => file.Decrypt());
        foreach (IFileSystemAclSupport acl in new[] { (IFileSystemAclSupport)file, (IFileSystemAclSupport)directory })
        {
            Refused(() => acl.GetAccessControl());
            Refused(() => acl.GetAccessControl(IFileSystemAclSupport.AccessControlSections.All));
            Refused(() => acl.SetAccessControl(new object()));
        }
    }

#pragma warning restore CA1416

    [Fact]
    public void The_file_system_does_not_own_the_directory()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(DirFileSystem)));
    }

    /// <summary>
    /// Asserts a member is refused as having no capability meaning, with a message that says
    /// why rather than the runtime's bare default.
    /// </summary>
    private static void Refused(Action call)
    {
        NotSupportedException e = Assert.Throws<NotSupportedException>(call);
        Assert.Contains(nameof(DirFileSystem), e.Message, StringComparison.Ordinal);
    }

    private static void Refused(Func<object?> call) => Refused(() => { _ = call(); });
}

public sealed class OnDiskDifferenceTests() : DifferenceTests(new DiskFixture());

public sealed class InMemoryDifferenceTests() : DifferenceTests(new MemoryFixture());
