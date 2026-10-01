using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Tests.Fakes;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The platform layer against a real filesystem.
/// </summary>
/// <remarks>
/// These run on whichever backend the host provides, which is the point: the contract every
/// resolution backend is written against has to hold identically on all three, and a suite
/// that only ever checked the simulated implementation would be checking that the simulation
/// agrees with itself.
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed partial class PlatformOpsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-interop-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IPlatformOps Ops => PlatformOps.Host;

    /// <summary>
    /// Every host has a backend, and it is one this library knows how to drive.
    /// </summary>
    [Fact]
    public void The_host_reports_a_backend()
    {
        PlatformCapabilities capabilities = Ops.Capabilities;
        Assert.NotEqual(ResolutionBackend.None, capabilities.Backend);
        Assert.Equal(
            capabilities.Backend == ResolutionBackend.ConfinedOpen,
            capabilities.SupportsConfinedOpen);
    }

    /// <summary>The host can be replaced, and puts itself back.</summary>
    [Fact]
    public void Substituting_the_host_is_scoped()
    {
        IPlatformOps real = PlatformOps.Host;

        FakeFileSystem fs = new();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            Assert.NotSame(real, PlatformOps.Host);
        }

        Assert.Same(real, PlatformOps.Host);
    }

    /// <summary>
    /// A handle keeps working through the backend that issued it while the host is replaced.
    /// </summary>
    /// <remarks>
    /// Replacing the host changes where the next root opened by path comes from, and nothing
    /// else. A handle that went on consulting the host would start sending a descriptor
    /// number to the simulation, where it names nothing or something unrelated.
    /// </remarks>
    [Fact]
    public void A_handle_keeps_its_backend_when_the_host_is_replaced()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        using SafeDirHandle root = OpenRoot();

        FakeFileSystem fs = new();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            Assert.True(CapPath.TryParse("child", out CapPath path, out _));
            CapResult<SafeDirHandle> child =
                Resolver.OpenDirectory(root, in path, CapAccess.Read, ConfinedResolveOptions.None);

            Assert.True(child.IsSuccess, child.Error.FailureDescription);
            Assert.Same(root.Backend, child.Value.Backend);
            child.Value.Dispose();
        }
    }

    /// <summary>A directory opened by an ordinary path becomes a handle.</summary>
    [Fact]
    public void An_ambient_path_opens_the_first_handle()
    {
        using SafeDirHandle root = OpenRoot();
        Assert.False(root.IsInvalid);
    }

    /// <summary>A path that names nothing fails, and says so as a missing name.</summary>
    [Fact]
    public void An_ambient_path_that_names_nothing_is_reported_as_missing()
    {
        CapResult<SafeDirHandle> result =
            Ops.OpenAmbientDirectory(Path.Combine(_root, "absent"), CapAccess.Read);
        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.NotFound, result.Error.Category);
    }

    /// <summary>A directory beneath the handle opens, and a name that is not there does not.</summary>
    [Fact]
    public void A_child_directory_opens_relative_to_its_parent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        using SafeDirHandle root = OpenRoot();
        CapResult<SafeDirHandle> child = Ops.OpenChildDirectory(root, "child", CapAccess.Read);
        Assert.True(child.IsSuccess, child.Error.FailureDescription);
        child.Value.Dispose();

        CapResult<SafeDirHandle> missing = Ops.OpenChildDirectory(root, "absent", CapAccess.Read);
        Assert.False(missing.IsSuccess);
        Assert.Equal(CapErrorCategory.NotFound, missing.Error.Category);
    }

    /// <summary>A file cannot be opened as a directory, and the failure says which mistake was made.</summary>
    [Fact]
    public void A_file_opened_as_a_directory_is_refused()
    {
        File.WriteAllText(Path.Combine(_root, "plain.txt"), "content");

        using SafeDirHandle root = OpenRoot();
        CapResult<SafeDirHandle> result = Ops.OpenChildDirectory(root, "plain.txt", CapAccess.Read);

        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.NotADirectory, result.Error.Category);
    }

    /// <summary>A file beneath the handle opens as a handle the framework's own IO accepts.</summary>
    [Fact]
    public void A_child_file_opens_as_a_usable_file_handle()
    {
        File.WriteAllText(Path.Combine(_root, "plain.txt"), "content");

        using SafeDirHandle root = OpenRoot();
        CapResult<SafeFileHandle> result = Ops.OpenChildFile(root, "plain.txt", FileOpenRequest.Existing(FileAccess.Read));
        Assert.True(result.IsSuccess, result.Error.FailureDescription);

        using SafeFileHandle file = result.Value;
        byte[] buffer = new byte[7];
        int read = RandomAccess.Read(file, buffer, 0);
        Assert.Equal("content", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
    }

    /// <summary>
    /// An open of a name that is a symbolic link stops rather than following it, and reports
    /// which of the two link outcomes happened.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole reason a walk can exist. "This is a link" means read it
    /// and decide; "too many links" means stop. On both Unix platforms the kernel reports
    /// them with the same code, and only the flags the open was issued with separate them —
    /// so a backend that dropped the flag would report every link as a terminal loop and no
    /// link inside a sandbox would ever be followed.
    /// </remarks>
    [Fact]
    public void A_symbolic_link_is_reported_rather_than_followed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        if (!TryCreateDirectoryLink(Path.Combine(_root, "link"), "real"))
        {
            return;
        }

        using SafeDirHandle root = OpenRoot();
        CapResult<SafeDirHandle> result = Ops.OpenChildDirectory(root, "link", CapAccess.Read);

        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.SymbolicLink, result.Error.Category);
    }

    /// <summary>A link's stored target is readable, exactly as it was written.</summary>
    [Fact]
    public void A_link_target_is_read_back_as_stored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        if (!TryCreateDirectoryLink(Path.Combine(_root, "link"), "real"))
        {
            return;
        }

        using SafeDirHandle root = OpenRoot();
        CapResult<string> target = Ops.ReadChildLink(root, "link");

        Assert.True(target.IsSuccess, target.Error.FailureDescription);
        Assert.Contains("real", target.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A target longer than the first buffer is read back whole, not cut at the buffer's
    /// size.
    /// </summary>
    /// <remarks>
    /// The kernel truncates a target that does not fit rather than saying so, and a truncated
    /// target is a different path, not a shorter one. A thousand bytes is past the first
    /// buffer and well inside the four-kilobyte limit Linux puts on a stored target.
    /// </remarks>
    [Fact]
    public void A_long_link_target_is_read_back_whole()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A dangling thousand-character link target is a Unix case.");
            return;
        }

        string longTarget = new('a', 1000);
        File.CreateSymbolicLink(Path.Combine(_root, "link"), longTarget);

        using SafeDirHandle root = OpenRoot();
        CapResult<string> target = Ops.ReadChildLink(root, "link");

        Assert.True(target.IsSuccess, target.Error.FailureDescription);
        Assert.Equal(longTarget, target.Value);
    }

    /// <summary>
    /// A target that does not fit the largest buffer is refused as too long, never returned
    /// cut short.
    /// </summary>
    /// <remarks>
    /// The real ceiling is far above anything Linux will store, so the read is given a
    /// smaller one: the target fills the first buffer and the doubled one, and the answer
    /// must be that it is too long.
    /// </remarks>
    [Fact]
    public void A_link_target_past_the_buffer_ceiling_is_refused_as_too_long()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The growing link read is the Linux backend's.");
            return;
        }

        string link = Path.Combine(_root, "link");
        File.CreateSymbolicLink(link, new string('a', 1000));
        byte[] path = Encoding.UTF8.GetBytes(link + "\0");

        CapResult<string> grown = LinuxPlatformOps.ReadLinkText(
            LinuxConstants.AT_FDCWD, path, initialCapacity: 256, maxCapacity: 1024);
        Assert.True(grown.IsSuccess, grown.Error.FailureDescription);
        Assert.Equal(1000, grown.Value.Length);

        CapResult<string> capped = LinuxPlatformOps.ReadLinkText(
            LinuxConstants.AT_FDCWD, path, initialCapacity: 256, maxCapacity: 512);
        Assert.False(capped.IsSuccess);
        Assert.Equal(CapErrorCategory.NameTooLong, capped.Error.Category);
    }

    /// <summary>Asking about a name reports what it is, without following it.</summary>
    [Fact]
    public void A_name_is_described_without_being_followed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        File.WriteAllText(Path.Combine(_root, "plain.txt"), "x");

        using SafeDirHandle root = OpenRoot();

        Assert.True(Ops.StatChild(root, "child", out CapNodeInfo directory).IsSuccess);
        Assert.Equal(CapNodeType.Directory, directory.Type);

        Assert.True(Ops.StatChild(root, "plain.txt", out CapNodeInfo file).IsSuccess);
        Assert.Equal(CapNodeType.File, file.Type);

        // Two different objects on one filesystem: same volume, different identity.
        Assert.False(directory.CrossesVolumeBoundaryFrom(file));
        Assert.False(directory.IsSameNodeAs(file));
    }

    /// <summary>
    /// A name and the handle opened from it describe the same object, which is what makes the
    /// comparison worth doing at all.
    /// </summary>
    [Fact]
    public void A_handle_describes_the_object_its_name_described()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        using SafeDirHandle root = OpenRoot();
        Assert.True(Ops.StatChild(root, "child", out CapNodeInfo byName).IsSuccess);

        CapResult<SafeDirHandle> opened = Ops.OpenChildDirectory(root, "child", CapAccess.Read);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using SafeDirHandle child = opened.Value;
        Assert.True(Ops.StatHandle(child, out CapNodeInfo byHandle).IsSuccess);
        Assert.True(byName.IsSameNodeAs(byHandle));
    }

    /// <summary>A duplicated handle is independent, and refers to the same directory.</summary>
    [Fact]
    public void A_duplicated_handle_outlives_the_original()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        using SafeDirHandle root = OpenRoot();
        CapResult<SafeDirHandle> copyResult = Ops.DuplicateDirectory(root);
        Assert.True(copyResult.IsSuccess, copyResult.Error.FailureDescription);

        using SafeDirHandle copy = copyResult.Value;
        Assert.True(Ops.StatHandle(root, out CapNodeInfo original).IsSuccess);
        Assert.True(Ops.StatHandle(copy, out CapNodeInfo duplicate).IsSuccess);
        Assert.True(original.IsSameNodeAs(duplicate));

        root.Dispose();

        // Closing one must not close the other: the copy is still a capability.
        CapResult<SafeDirHandle> child = Ops.OpenChildDirectory(copy, "child", CapAccess.Read);
        Assert.True(child.IsSuccess, child.Error.FailureDescription);
        child.Value.Dispose();
    }

    /// <summary>
    /// On macOS, relabelling a directory handle's access duplicates it rather than resolving
    /// a name, so it works on a directory removed since it was opened.
    /// </summary>
    /// <remarks>
    /// Every directory descriptor there is opened for reading whatever access was asked for,
    /// so a re-open would grant nothing a duplicate does not. An open of <c>.</c> would also
    /// fail on a removed directory, which is what shows the duplicate is what ran. On Linux
    /// the re-open is a real change of descriptor mode and has to resolve <c>.</c>.
    /// </remarks>
    [Fact]
    public void Relabelling_a_directory_on_macOS_resolves_no_name()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Only macOS directory access is a label alone.");

        string path = Path.Combine(_root, "removed");
        Directory.CreateDirectory(path);

        CapResult<SafeDirHandle> opened = Ops.OpenAmbientDirectory(path, CapAccess.None);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        using SafeDirHandle traversal = opened.Value;
        Directory.Delete(path);

        CapResult<SafeDirHandle> relabelled = Ops.ReopenDirectory(traversal, CapAccess.Read);
        Assert.True(relabelled.IsSuccess, relabelled.Error.FailureDescription);

        using SafeDirHandle readable = relabelled.Value;
        Assert.Equal(CapAccess.Read, readable.Access);
        Assert.True(Ops.StatHandle(traversal, out CapNodeInfo original).IsSuccess);
        Assert.True(Ops.StatHandle(readable, out CapNodeInfo copy).IsSuccess);
        Assert.True(original.IsSameNodeAs(copy));
    }

    /// <summary>
    /// A rename told not to replace its destination either refuses it in the same call or says
    /// it cannot, and never replaces it.
    /// </summary>
    /// <remarks>
    /// The categories are the ones <see cref="IPlatformOps.RenameChild"/> documents: a taken
    /// destination where the filesystem can refuse atomically, and otherwise
    /// <see cref="CapErrorCategory.NotSupported"/> or, where the filesystem answers with the
    /// code for a malformed request, <see cref="CapErrorCategory.InvalidArgument"/>. A
    /// fallback that looked the destination up and then renamed would pass on an idle tree
    /// and race on a busy one; the unchanged contents are what would show it had been added.
    /// </remarks>
    [Fact]
    public void A_no_replace_rename_on_this_filesystem_is_refused_atomically_or_says_it_cannot_be()
    {
        File.WriteAllText(Path.Combine(_root, "a"), "source");
        File.WriteAllText(Path.Combine(_root, "b"), "destination");

        using SafeDirHandle root = OpenRoot();
        CapError error = Ops.RenameChild(root, "a", root, "b", replaceExisting: false);

        Assert.True(error.IsFailure);
        Assert.Contains(
            error.Category,
            new[] { CapErrorCategory.AlreadyExists, CapErrorCategory.NotSupported, CapErrorCategory.InvalidArgument });
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "a")));
        Assert.Equal("destination", File.ReadAllText(Path.Combine(_root, "b")));
    }

    /// <summary>A closed handle is refused rather than used, and cannot reach a recycled object.</summary>
    [Fact]
    public void A_closed_handle_cannot_be_used()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        SafeDirHandle root = OpenRoot();
        root.Dispose();

        CapResult<SafeDirHandle> result = Ops.OpenChildDirectory(root, "child", CapAccess.Read);
        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// A nameless file cannot be linked into a directory through its descriptor.
    /// </summary>
    /// <remarks>
    /// The empty-path form of the link. Before Linux 6.10 an unprivileged caller is refused
    /// it whatever the file, so on those kernels this case cannot tell the difference and
    /// the one through <c>/proc</c> below is the one that can.
    /// </remarks>
    [Fact]
    public void An_anonymous_file_cannot_be_given_a_name_through_its_descriptor()
    {
        using SafeFileHandle file = OpenAnonymousFileOrSkip();
        int fd = (int)file.DangerousGetHandle();

        int result = LinkAt(fd, string.Empty, AtFdCwd, Path.Combine(_root, "named"), AtEmptyPath);

        AssertLinkRefused(result);
    }

    /// <summary>
    /// A nameless file cannot be linked into a directory through its entry under
    /// <c>/proc</c>.
    /// </summary>
    /// <remarks>
    /// Following the descriptor's magic link needs no privilege, so this is the route any
    /// holder of the descriptor has, on every kernel.
    /// </remarks>
    [Fact]
    public void An_anonymous_file_cannot_be_given_a_name_through_proc()
    {
        if (!Directory.Exists("/proc/self/fd"))
        {
            Assert.Skip("/proc is not mounted, so there is no descriptor entry to link from.");
        }

        using SafeFileHandle file = OpenAnonymousFileOrSkip();
        string entry = "/proc/self/fd/" + file.DangerousGetHandle().ToString(CultureInfo.InvariantCulture);

        int result = LinkAt(AtFdCwd, entry, AtFdCwd, Path.Combine(_root, "named"), AtSymlinkFollow);

        AssertLinkRefused(result);
    }

    private SafeFileHandle OpenAnonymousFileOrSkip()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linking a file in by its descriptor is a Linux facility.");
        }

        using SafeDirHandle root = OpenRoot();
        CapResult<SafeFileHandle> result = Ops.OpenAnonymousChildFile(root, FileAccess.ReadWrite);
        if (!result.IsSuccess && result.Error.Category == CapErrorCategory.NotSupported)
        {
            Assert.Skip("The filesystem under the temporary directory has no nameless files.");
        }

        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value;
    }

    private void AssertLinkRefused(int result)
    {
        int errno = Marshal.GetLastPInvokeError();
        Assert.Equal(-1, result);
        Assert.Equal(ENOENT, errno);
        Assert.False(File.Exists(Path.Combine(_root, "named")));
    }

    private const int AtFdCwd = -100;
    private const int AtSymlinkFollow = 0x400;
    private const int AtEmptyPath = 0x1000;
    private const int ENOENT = 2;

    [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkAt(int oldDirFd, string oldPath, int newDirFd, string newPath, int flags);

    private SafeDirHandle OpenRoot()
    {
        CapResult<SafeDirHandle> result = Ops.OpenAmbientDirectory(_root, CapAccess.Read);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value;
    }

    /// <summary>
    /// Creates a directory symbolic link, or reports that this host will not allow one.
    /// </summary>
    /// <remarks>
    /// Windows refuses to create symbolic links without either an elevated token or developer
    /// mode, and the test process is required not to hold privileges that would let it
    /// sidestep the permission system. A host that refuses is not a failure; it means the
    /// link cases run on the platforms that can express them.
    /// </remarks>
    private static bool TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
