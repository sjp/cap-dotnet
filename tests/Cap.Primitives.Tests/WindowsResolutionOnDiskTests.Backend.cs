using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;
using Cap.Tests;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The Windows backend's own members against a real volume: the calls whose correctness
/// depends on what NTFS does with them, which nothing but NTFS can answer.
/// </summary>
public sealed partial class WindowsResolutionOnDiskTests
{
    // --- opening a name as whatever it holds -------------------------------------------------

    /// <summary>
    /// A file opened without saying its kind comes back with the sharing and the overlapped
    /// option the caller asked for, and nothing of the first, query-only open is left.
    /// </summary>
    /// <remarks>
    /// The kind-agnostic open looks at the name once with no data rights, then opens the
    /// object again through that handle. Everything the caller asked for is carried by the
    /// second open alone, so a second open that dropped the share mode or the option would
    /// still hand back a handle that reads, and nothing but another opener would notice.
    /// </remarks>
    [Fact]
    public void An_open_of_either_kind_gives_a_file_the_sharing_and_options_asked_for()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The two-step open exists only on Windows.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "plain.txt");
        File.WriteAllText(path, "contents");

        using SafeDirHandle root = OpenSandbox();

        FileOpenRequest exclusive = new(FileMode.Open, FileAccess.Read, FileShare.None, FileOptions.Asynchronous, 0);
        using (OpenedNode node = OpenNode(root, "plain.txt", in exclusive))
        {
            Assert.Null(node.Directory);
            Assert.NotNull(node.File);
            Assert.True(node.File.IsAsync, "the overlapped option was not carried to the second open.");
            Assert.Equal("contents", System.Text.Encoding.UTF8.GetString(ReadAll(node.File)));

            Assert.Throws<IOException>(
                () => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }

        FileOpenRequest readersOnly = new(FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0);
        using (OpenedNode node = OpenNode(root, "plain.txt", in readersOnly))
        {
            Assert.NotNull(node.File);
            Assert.False(node.File.IsAsync);

            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose();
            Assert.Throws<IOException>(
                () => File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        }

        // Nothing held on to the file once the node was closed: the query-only handle went
        // with it, and an open that shares nothing is granted.
        File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
    }

    /// <summary>
    /// A directory opened without saying its kind comes back as a directory, and a junction
    /// is reported as the link it is rather than opened.
    /// </summary>
    [Fact]
    public void An_open_of_either_kind_opens_a_directory_and_refuses_a_junction()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The two-step open exists only on Windows.");
            return;
        }

        Directory.CreateDirectory(Path.Join(Sandbox, "folder"));
        CreateJunction(Path.Join(Sandbox, "jn"), Path.Join(Sandbox, "folder"));

        using SafeDirHandle root = OpenSandbox();

        FileOpenRequest request = FileOpenRequest.Existing(FileAccess.Read);
        using (OpenedNode node = OpenNode(root, "folder", in request))
        {
            Assert.Null(node.File);
            Assert.NotNull(node.Directory);

            CapError error = PlatformOps.Host.StatHandle(node.Directory, out CapNodeInfo info);
            Assert.True(error.IsSuccess, error.FailureDescription);
            Assert.Equal(CapNodeType.Directory, info.Type);
        }

        AssertNodeFails(CapErrorCategory.SymbolicLink, root, "jn");
    }

    /// <summary>
    /// A symbolic link to a file, met by an open that did not say its kind, is reported as a
    /// link rather than followed.
    /// </summary>
    [Fact]
    public void An_open_of_either_kind_refuses_a_file_link()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The two-step open exists only on Windows.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        File.WriteAllText(Path.Join(Sandbox, "plain.txt"), "contents");

        using SafeDirHandle root = OpenSandbox();

        CapError created = PlatformOps.Host.CreateChildSymbolicLink(root, "flink", "plain.txt", false);
        ExpectedHostFeatures.Require(
            HostFeature.Symlinks,
            created.Category != CapErrorCategory.PermissionDenied,
            "A file link cannot be offered to the open without one.");
        Assert.True(created.IsSuccess, created.FailureDescription);

        AssertNodeFails(CapErrorCategory.SymbolicLink, root, "flink");
    }

    // --- renaming and removing names that are open elsewhere ---------------------------------

    /// <summary>
    /// A rename that replaces a file another handle holds open succeeds, and the holder keeps
    /// reading what it opened.
    /// </summary>
    /// <remarks>
    /// Windows' own convention refuses this: a file that is open cannot be renamed over. The
    /// replacement asks for POSIX semantics so that an atomic write works here as it does on
    /// Unix, where a reader of the old file is never in the way of publishing the new one.
    /// The holder shares deletion, which is what the framework's own readers do and what the
    /// semantics need from them.
    /// </remarks>
    [Fact]
    public void A_replacing_rename_succeeds_over_a_file_held_open_elsewhere()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX rename semantics are a Windows request.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string source = Path.Join(Sandbox, "incoming");
        string destination = Path.Join(Sandbox, "published");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "old");

        using SafeDirHandle root = OpenSandbox();
        using SafeFileHandle holder = File.OpenHandle(
            destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        CapError renamed = PlatformOps.Host.RenameChild(root, "incoming", root, "published", replaceExisting: true);
        Assert.True(renamed.IsSuccess, renamed.FailureDescription);

        Assert.False(File.Exists(source));
        Assert.Equal("new", File.ReadAllText(destination));
        Assert.Equal("old", System.Text.Encoding.UTF8.GetString(ReadAll(holder)));
    }

    /// <summary>
    /// The older rename class, which <see cref="IPlatformOps.RenameChild"/> falls back to where
    /// the newer is not implemented, refuses an existing destination with a clear flag and
    /// replaces it with a set one.
    /// </summary>
    /// <remarks>
    /// NTFS implements the newer class, so the fallback is never taken on the volumes the tests
    /// run on; the older class is written here directly instead. What this pins down is the
    /// part that differs between the two: the older class reads its first byte as a lone
    /// "replace" flag, and a request built for it that meant "do not replace" must not
    /// replace.
    /// </remarks>
    [Fact]
    public void The_older_rename_class_refuses_or_replaces_as_its_flag_says()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The rename classes are Windows ones.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string source = Path.Join(Sandbox, "incoming");
        string destination = Path.Join(Sandbox, "published");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "old");

        using SafeDirHandle root = OpenSandbox();
        using SafeFileHandle moving = OpenForRenaming(source);

        CapError refused = WindowsPlatformOps.SetDestinationName(
            moving.DangerousGetHandle(), root, "published", NtConstants.FileRenameInformationClass, 0);
        Assert.Equal(CapErrorCategory.AlreadyExists, refused.Category);
        Assert.Equal("old", File.ReadAllText(destination));
        Assert.True(File.Exists(source));

        CapError replaced = WindowsPlatformOps.SetDestinationName(
            moving.DangerousGetHandle(), root, "published", NtConstants.FileRenameInformationClass, 1);
        Assert.True(replaced.IsSuccess, replaced.FailureDescription);
        Assert.Equal("new", ReadSharingDelete(destination));
        Assert.False(File.Exists(source));

        // A one-character destination, which fits in fewer bytes than the declared structure
        // and is only accepted because the request is padded out to it.
        CapError shortName = WindowsPlatformOps.SetDestinationName(
            moving.DangerousGetHandle(), root, "p", NtConstants.FileRenameInformationClass, 0);
        Assert.True(shortName.IsSuccess, shortName.FailureDescription);
        Assert.Equal("new", ReadSharingDelete(Path.Join(Sandbox, "p")));
    }

    /// <summary>
    /// Reads a file's text alongside a handle that holds delete access to it.
    /// </summary>
    /// <remarks>
    /// <see cref="File.ReadAllText(string)"/> does not share delete, and Windows refuses any
    /// open that does not while another handle on the file has delete access, as the handle
    /// a rename goes through does.
    /// </remarks>
    private static string ReadSharingDelete(string path)
    {
        using SafeFileHandle file = File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return System.Text.Encoding.UTF8.GetString(ReadAll(file));
    }

    /// <summary>
    /// Removing a file another handle holds open succeeds, and the name is free at once.
    /// </summary>
    /// <remarks>
    /// Windows' own convention leaves a removed name in place, unusable, until the last
    /// handle closes. The removal asks for POSIX semantics so that the name goes now, as it
    /// does on Unix; a caller that removes a file and creates another under its name would
    /// otherwise fail for as long as some reader held the old one.
    /// </remarks>
    [Fact]
    public void Removing_a_file_held_open_elsewhere_frees_the_name_at_once()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX removal semantics are a Windows request.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "victim");
        File.WriteAllText(path, "old");

        using SafeDirHandle root = OpenSandbox();
        using SafeFileHandle holder = File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        CapError removed = PlatformOps.Host.RemoveChildFile(root, "victim");
        Assert.True(removed.IsSuccess, removed.FailureDescription);

        Assert.False(File.Exists(path), "the removed name lingered while a handle was open.");
        File.WriteAllText(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", System.Text.Encoding.UTF8.GetString(ReadAll(holder)));
    }

    /// <summary>
    /// A read-only file cannot be removed until its removal block is cleared, and then can.
    /// </summary>
    [Fact]
    public void A_read_only_file_is_removed_once_its_block_is_cleared()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The read-only attribute blocks removal only on Windows.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "locked");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Archive);

        using SafeDirHandle root = OpenSandbox();

        CapError blocked = PlatformOps.Host.RemoveChildFile(root, "locked");
        Assert.Equal(CapErrorCategory.PermissionDenied, blocked.Category);
        Assert.True(File.Exists(path));

        CapError cleared = PlatformOps.Host.ClearChildRemovalBlock(root, "locked");
        Assert.True(cleared.IsSuccess, cleared.FailureDescription);
        Assert.Equal(FileAttributes.Archive, File.GetAttributes(path) & (FileAttributes.ReadOnly | FileAttributes.Archive));

        CapError removed = PlatformOps.Host.RemoveChildFile(root, "locked");
        Assert.True(removed.IsSuccess, removed.FailureDescription);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Clearing the removal block of a read-only link clears the link's own flag and leaves
    /// what it points at as it was.
    /// </summary>
    /// <remarks>
    /// The target is read-only as well, so that a clear which followed the link would show:
    /// it would clear the target's flag, and leave the link's own, which is the one standing
    /// in the way of the removal.
    /// </remarks>
    [Fact]
    public void Clearing_a_links_removal_block_leaves_its_target_alone()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The read-only attribute blocks removal only on Windows.");
            return;
        }

        string target = Path.Join(Sandbox, "target");
        string link = Path.Join(Sandbox, "jn");
        Directory.CreateDirectory(target);
        CreateJunction(link, target);

        try
        {
            File.SetAttributes(target, FileAttributes.ReadOnly);
            SetOwnAttributes(link, FileAttributes.ReadOnly);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReadOnly) != 0, "the link was not made read-only.");

            using SafeDirHandle root = OpenSandbox();

            CapError cleared = PlatformOps.Host.ClearChildRemovalBlock(root, "jn");
            Assert.True(cleared.IsSuccess, cleared.FailureDescription);

            Assert.Equal(0, (int)(File.GetAttributes(link) & FileAttributes.ReadOnly));
            Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(link) & FileAttributes.ReparsePoint);
            Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(target) & FileAttributes.ReadOnly);

            CapError removed = PlatformOps.Host.RemoveChildFile(root, "jn");
            Assert.True(removed.IsSuccess, removed.FailureDescription);
            Assert.False(Path.Exists(link));
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// Removing a directory by a name that holds a link is refused as not a directory whatever
    /// kind the link was made as, and the link is then removed as a file, leaving its target.
    /// </summary>
    /// <remarks>
    /// The file kind is the case that needs saying. Asked for a directory, the open of a
    /// file-kind link is refused before the link is visible, and the walk explains that
    /// refusal as the link so it can follow it; a removal ends at the name, so it reports what
    /// Unix reports for every link there, and a caller that falls back to removing a file on
    /// that answer does so on every platform.
    /// </remarks>
    [Theory]
    [InlineData("junction")]
    [InlineData("directory link")]
    [InlineData("file link")]
    public void Removing_a_link_as_a_directory_is_refused_as_not_a_directory(string kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The kinds of link are Windows ones.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string link = Path.Join(Sandbox, "link");
        string target = Path.Join(Sandbox, "target");
        if (kind == "file link")
        {
            File.WriteAllText(target, "x");
        }
        else
        {
            Directory.CreateDirectory(target);
        }

        if (kind == "junction")
        {
            CreateJunction(link, target);
        }
        else if (!(kind == "file link" ? TryCreateFileLink(link, target) : TryCreateDirectoryLink(link, target)))
        {
            Assert.Skip("This host will not create symbolic links for this process.");
        }

        using SafeDirHandle root = OpenSandbox();

        CapError refused = PlatformOps.Host.RemoveChildDirectory(root, "link");
        Assert.Equal(CapErrorCategory.NotADirectory, refused.Category);
        Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(link) & FileAttributes.ReparsePoint);

        CapError removed = PlatformOps.Host.RemoveChildFile(root, "link");
        Assert.True(removed.IsSuccess, removed.FailureDescription);
        Assert.False(Path.Exists(link));
        Assert.True(Path.Exists(target));
    }

    // --- aliases, times and appending ----------------------------------------------------------

    /// <summary>
    /// A generated short name does not reach the file it was derived from, whether the open
    /// would read it or could have created it.
    /// </summary>
    /// <remarks>
    /// The directory case is asserted with the rest of the alias cases; a file is opened by
    /// another path through the backend, the one that can also create, and the check there
    /// has to run whatever the disposition — an open that may create takes the existing
    /// file when the alias names one, and must refuse it rather than open it or make another.
    /// </remarks>
    [Fact]
    public void A_short_name_alias_does_not_reach_the_file_it_aliases()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Short names exist only on Windows.");
            return;
        }

        const string LongName = "a long file name.txt";
        Directory.CreateDirectory(Sandbox);
        File.WriteAllText(Path.Join(Sandbox, LongName), "original");

        string? alias = ShortNameOf(Path.Join(Sandbox, LongName));
        if (alias is null || alias.Equals(LongName, StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(
                Environment.GetEnvironmentVariable("CAPDOTNET_EXPECT_SHORT_NAMES") == "1",
                $"no short name was generated for '{LongName}', but this host was set up to generate them.");

            Assert.Skip("This volume does not generate short names, so there is no alias to refuse.");
            return;
        }

        using SafeDirHandle root = OpenSandbox();

        CapResult<SafeFileHandle> byOwnName = PortableResolver.OpenFile(
            root, Parse(LongName), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        Assert.True(byOwnName.IsSuccess, byOwnName.Error.FailureDescription);
        byOwnName.Value!.Dispose();

        CapResult<SafeFileHandle> read = PortableResolver.OpenFile(
            root, Parse(alias), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        AssertFileFails(CapErrorCategory.AliasedName, read);

        FileOpenRequest creating = new(
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0);
        CapResult<SafeFileHandle> created = PlatformOps.Host.OpenChildFile(root, alias, in creating);
        AssertFileFails(CapErrorCategory.AliasedName, created);

        Assert.Single(Directory.EnumerateFileSystemEntries(Sandbox));
        Assert.Equal("original", File.ReadAllText(Path.Join(Sandbox, LongName)));
    }

    /// <summary>
    /// Setting the times of a link sets the link's own, and leaves what it points at alone.
    /// </summary>
    /// <remarks>
    /// Read back through the framework, which reports a link's own entry, rather than through
    /// this library, so that a writer and a reader that agreed on the wrong object could not
    /// pass together.
    /// </remarks>
    [Fact]
    public void Setting_a_links_times_leaves_its_target_alone()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("A junction exists only on Windows.");
            return;
        }

        string target = Path.Join(Sandbox, "target");
        string link = Path.Join(Sandbox, "jn");
        Directory.CreateDirectory(target);
        CreateJunction(link, target);
        DateTime targetBefore = Directory.GetLastWriteTimeUtc(target);

        DateTime wanted = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        using SafeDirHandle root = OpenSandbox();

        CapError error = PlatformOps.Host.SetChildTimes(
            root, "jn", CapFileTime.Unchanged, CapFileTime.At(new DateTimeOffset(wanted)));
        Assert.True(error.IsSuccess, error.FailureDescription);

        Assert.Equal(wanted, new DirectoryInfo(link).LastWriteTimeUtc);
        Assert.Equal(targetBefore, Directory.GetLastWriteTimeUtc(target));
    }

    /// <summary>
    /// A write through the appending copy of a handle lands at the end of the file, whatever
    /// offset it names.
    /// </summary>
    /// <remarks>
    /// The copy is the original without the right to write at an offset, which is how
    /// appending is expressed on a handle here, and the system is what puts each write at the
    /// end. The writes are made with the framework's positional write rather than this
    /// library's appending one, because it is the system's placement being relied on.
    /// </remarks>
    [Fact]
    public void A_write_through_the_appending_copy_lands_at_the_end()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Appending by withheld rights is a Windows mechanism.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "log");
        File.WriteAllText(path, "abc");

        using SafeDirHandle root = OpenSandbox();

        FileOpenRequest request = new(
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0);
        CapResult<SafeFileHandle> opened = PlatformOps.Host.OpenChildFile(root, "log", in request);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using (SafeFileHandle original = opened.Value!)
        {
            CapResult<SafeFileHandle> copied = PlatformOps.Host.DuplicateAppendingFile(original);
            Assert.True(copied.IsSuccess, copied.Error.FailureDescription);

            using SafeFileHandle appending = copied.Value!;
            RandomAccess.Write(appending, "XYZ"u8, fileOffset: 0);
            RandomAccess.Write(appending, "!!"u8, fileOffset: 1);
        }

        Assert.Equal("abcXYZ!!", File.ReadAllText(path));
    }

    /// <summary>
    /// The library's appending write works on a handle opened for overlapped operations,
    /// including one the thread pool has already been bound to.
    /// </summary>
    /// <remarks>
    /// The binding is what makes this worth asserting. Once a handle belongs to the thread
    /// pool, every completion on it is delivered there unless the write says otherwise, and a
    /// completion the pool never asked for is one it may treat as its own.
    /// </remarks>
    [Fact]
    public async Task An_appending_write_works_on_an_overlapped_handle()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The appending write's overlapped path is Windows-only.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "log");
        File.WriteAllText(path, string.Empty);

        using SafeDirHandle root = OpenSandbox();

        FileOpenRequest request = new(
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous, 0);
        CapResult<SafeFileHandle> opened = PlatformOps.Host.OpenChildFile(root, "log", in request);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using (SafeFileHandle file = opened.Value!)
        {
            Assert.True(file.IsAsync);

            // Binds the handle to the thread pool, as any asynchronous use of it would.
            await RandomAccess.WriteAsync(file, "abc"u8.ToArray(), fileOffset: 0, TestContext.Current.CancellationToken);

            CapError first = PlatformOps.Host.WriteAppending(file, "XYZ"u8, fileOffset: 0);
            Assert.True(first.IsSuccess, first.FailureDescription);
            CapError second = PlatformOps.Host.WriteAppending(file, "!!"u8, fileOffset: 0);
            Assert.True(second.IsSuccess, second.FailureDescription);
        }

        Assert.Equal("abcXYZ!!", File.ReadAllText(path));
    }

    // --- attributes ------------------------------------------------------------------------------

    /// <summary>
    /// Setting attributes through a handle replaces the settable set as a whole, and a bit the
    /// filesystem owns is ignored rather than failing the call.
    /// </summary>
    /// <remarks>
    /// Read back through the framework rather than through this library, so the answer is the
    /// system's. The second write carries <see cref="FileAttributes.Compressed"/> because the
    /// system refuses the whole call over it: succeeding at all is what shows it was masked.
    /// </remarks>
    [Fact]
    public void Setting_attributes_replaces_the_settable_set_and_ignores_the_filesystems_own()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("File attributes are what Windows records as permissions.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "file");
        File.WriteAllText(path, "abc");
        File.SetAttributes(path, FileAttributes.Hidden);

        using SafeDirHandle root = OpenSandbox();

        CapResult<SafeFileHandle> opened = PlatformOps.Host.OpenChildFile(
            root, "file", FileOpenRequest.Existing(FileAccess.ReadWrite));
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using SafeFileHandle file = opened.Value!;

        CapError readOnly = PlatformOps.Host.SetHandlePermissions(file, null, FileAttributes.ReadOnly);
        Assert.True(readOnly.IsSuccess, readOnly.FailureDescription);

        FileAttributes afterReadOnly = File.GetAttributes(path);
        Assert.True((afterReadOnly & FileAttributes.ReadOnly) != 0, $"{afterReadOnly}");
        Assert.True((afterReadOnly & FileAttributes.Hidden) == 0, $"{afterReadOnly}");

        CapError compressed = PlatformOps.Host.SetHandlePermissions(
            file, null, FileAttributes.Compressed | FileAttributes.ReadOnly);
        Assert.True(compressed.IsSuccess, compressed.FailureDescription);

        FileAttributes afterCompressed = File.GetAttributes(path);
        Assert.True((afterCompressed & FileAttributes.ReadOnly) != 0, $"{afterCompressed}");
        Assert.True((afterCompressed & FileAttributes.Compressed) == 0, $"{afterCompressed}");
    }

    // --- what a handle says it is called ---------------------------------------------------------

    /// <summary>
    /// A handle's path comes back in the extended-length form and names the directory itself,
    /// including when the root was opened through a junction.
    /// </summary>
    /// <remarks>
    /// Compared by what the reply reaches rather than by its spelling: the temporary directory
    /// on a build agent is often named through a short-name component, which the normalised
    /// reply spells out in full.
    /// </remarks>
    [Fact]
    public void A_handles_path_names_the_directory_it_holds()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The extended-length form is a Windows one.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        File.WriteAllText(Path.Join(Sandbox, "marker"), "x");
        string via = Path.Join(_root, "via");
        CreateJunction(via, Sandbox);

        using (SafeDirHandle root = OpenSandbox())
        {
            AssertNamesSandbox(PlatformOps.Host.GetHandlePath(root));
        }

        CapResult<SafeDirHandle> throughJunction = PlatformOps.Host.OpenAmbientDirectory(via, CapAccess.Read);
        Assert.True(throughJunction.IsSuccess, throughJunction.Error.FailureDescription);
        using (SafeDirHandle root = throughJunction.Value!)
        {
            AssertNamesSandbox(PlatformOps.Host.GetHandlePath(root));
        }

        static void AssertNamesSandbox(CapResult<string> path)
        {
            Assert.True(path.IsSuccess, path.Error.FailureDescription);
            Assert.StartsWith(@"\\?\", path.Value, StringComparison.Ordinal);
            Assert.EndsWith(@"\sandbox", path.Value, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(path.Value, "marker")), $"'{path.Value}' does not hold the marker.");
        }
    }

    // --- a directory that matches case exactly ---------------------------------------------------

    /// <summary>
    /// Beneath a directory made case-sensitive, two names differing only in case are two
    /// objects, and resolution reaches each by its own name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exception to the case rule the rest of this class asserts: every open here asks to
    /// match case-insensitively, and a case-sensitive directory matches exactly regardless.
    /// Nothing about containment depends on it, but a caller told that <c>a</c> and
    /// <c>A</c> are one file would be wrong here, and the alias check, which compares a name
    /// with what the object says it is called, must not start refusing names because two
    /// spellings now coexist.
    /// </para>
    /// <para>
    /// Stands aside where the system will not make the directory case-sensitive: older builds,
    /// volumes other than NTFS, and some editions without the component that implements it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Beneath_a_case_sensitive_directory_case_is_significant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Per-directory case sensitivity is a Windows setting.");
            return;
        }

        string sensitive = Path.Join(Sandbox, "sensitive");
        Directory.CreateDirectory(sensitive);

        string? refusal = TryMakeCaseSensitive(sensitive);
        if (refusal is not null)
        {
            Assert.Skip($"This system would not make a directory case-sensitive: {refusal}");
            return;
        }

        File.WriteAllText(Path.Join(sensitive, "a"), "lower");
        File.WriteAllText(Path.Join(sensitive, "A"), "upper");
        Directory.CreateDirectory(Path.Join(sensitive, "d"));
        Directory.CreateDirectory(Path.Join(sensitive, "D"));
        Directory.CreateDirectory(Path.Join(sensitive, "plain~1"));
        Directory.CreateDirectory(Path.Join(sensitive, "Only"));

        using SafeDirHandle root = OpenSandbox();

        Assert.Equal("lower", ReadThrough(root, "sensitive/a"));
        Assert.Equal("upper", ReadThrough(root, "sensitive/A"));

        CapNodeInfo lower = Identify(root, "sensitive/d");
        CapNodeInfo upper = Identify(root, "sensitive/D");
        Assert.NotEqual(lower.NodeId, upper.NodeId);

        OpenDirectory(root, "sensitive/plain~1").Dispose();
        OpenDirectory(root, "sensitive/Only").Dispose();
        AssertFails(CapErrorCategory.NotFound, root, "sensitive/only");
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static OpenedNode OpenNode(SafeDirHandle root, string name, in FileOpenRequest request)
    {
        CapResult<OpenedNode> opened = PlatformOps.Host.OpenChildNode(root, name, in request);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        return opened.Value!;
    }

    private static void AssertNodeFails(CapErrorCategory expected, SafeDirHandle root, string name)
    {
        CapResult<OpenedNode> opened = PlatformOps.Host.OpenChildNode(root, name, FileOpenRequest.Existing(FileAccess.Read));
        if (opened.IsSuccess)
        {
            opened.Value!.Dispose();
            Assert.Fail($"'{name}' was opened when it should not have been.");
        }

        Assert.Equal(expected, opened.Error.Category);
    }

    private static void AssertFileFails(CapErrorCategory expected, CapResult<SafeFileHandle> opened)
    {
        if (opened.IsSuccess)
        {
            opened.Value!.Dispose();
            Assert.Fail("the file was opened when it should not have been.");
        }

        Assert.Equal(expected, opened.Error.Category);
    }

    private static string ReadThrough(SafeDirHandle root, string path)
    {
        CapResult<SafeFileHandle> opened = PortableResolver.OpenFile(
            root, Parse(path), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using SafeFileHandle file = opened.Value!;
        return System.Text.Encoding.UTF8.GetString(ReadAll(file));
    }

    /// <summary>
    /// Opens a file with the right to rename it and nothing else, as the backend's own rename
    /// does, for the tests that write a rename request directly.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenForRenaming(string path)
    {
        const uint OpenReparsePoint = 0x00200000;

        SafeFileHandle handle = CreateFile(
            path,
            NtConstants.DELETE | NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE,
            NtConstants.FILE_SHARE_ALL,
            0,
            NtConstants.OPEN_EXISTING,
            OpenReparsePoint | NtConstants.FILE_FLAG_BACKUP_SEMANTICS,
            0);

        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        return handle;
    }

    /// <summary>
    /// Sets the attributes of a name's own entry, without following it if it is a link.
    /// </summary>
    /// <remarks>
    /// Through a handle on the reparse point itself rather than the framework, so what the test
    /// arranges does not depend on whether the framework's call follows links. Only settable
    /// bits may be passed: the directory and reparse-point bits make the call fail.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static unsafe void SetOwnAttributes(string path, FileAttributes attributes)
    {
        const uint OpenReparsePoint = 0x00200000;
        const uint WriteAttributes = 0x0100;
        const int FileBasicInfo = 0;

        using SafeFileHandle handle = CreateFile(
            path,
            WriteAttributes | NtConstants.SYNCHRONIZE,
            NtConstants.FILE_SHARE_ALL,
            0,
            NtConstants.OPEN_EXISTING,
            OpenReparsePoint | NtConstants.FILE_FLAG_BACKUP_SEMANTICS,
            0);

        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        FileBasicInformation basic = new() { FileAttributes = (uint)attributes };
        if (!SetFileInformationByHandle(handle, FileBasicInfo, &basic, (uint)FileBasicInformation.StructSize))
        {
            throw new System.ComponentModel.Win32Exception();
        }
    }

    /// <summary>
    /// Makes an empty directory case-sensitive, returning why not where the system refuses.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryMakeCaseSensitive(string directory)
    {
        using Process? process = Process.Start(new ProcessStartInfo("fsutil.exe")
        {
            Arguments = $"file setCaseSensitiveInfo \"{directory}\" enable",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            return "fsutil could not be started.";
        }

        (string output, string errors) = ChildProcessWait.Finish(process, "fsutil", ToolTimeout);

        return process.ExitCode == 0 ? null : $"exit code {process.ExitCode}: {errors}{output}".Trim();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string path,
        uint access,
        uint share,
        nint securityAttributes,
        uint disposition,
        uint flags,
        nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(
        SafeFileHandle file, int informationClass, void* information, uint length);
}
