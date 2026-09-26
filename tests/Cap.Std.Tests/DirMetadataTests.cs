using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Describing what a handle refers to, and what a name beneath it holds.
/// </summary>
/// <remarks>
/// <para>
/// Three properties carry most of the weight here.
/// </para>
/// <para>
/// The first is that a snapshot describes a name and never what the name leads to. A
/// symbolic link is reported as a link, with its own length and its own times, under every
/// policy a handle can carry — so the answer does not change depending on which handle the
/// question was asked through.
/// </para>
/// <para>
/// The second is that asking an open handle does not involve a name at all. A file whose name
/// has been taken by something else in the meantime still answers about itself, which is the
/// whole difference between a handle and a path.
/// </para>
/// <para>
/// The third is that two names can lead to one object, and that this is detectable without
/// comparing anything about the names. Hard links are the case that matters: nothing about
/// either name says the other exists.
/// </para>
/// <para>
/// Set-up uses the ambient filesystem deliberately, because the tree being described has to
/// be built by something that is not the code under test.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirMetadataTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    // --- what a snapshot says -----------------------------------------------------------------

    /// <summary>A file reports itself as a file, with the length it was written with.</summary>
    [Fact]
    public void A_file_reports_its_kind_and_its_length()
    {
        HostFile.WriteAllBytes(Host("payload"), new byte[1234]);

        using Dir root = OpenRoot();
        CapMetadata metadata = root.GetMetadata("payload");

        Assert.Equal(CapFileType.File, metadata.Type);
        Assert.Equal(1234, metadata.Length);
    }

    /// <summary>A directory reports itself as a directory.</summary>
    [Fact]
    public void A_directory_reports_itself_as_a_directory()
    {
        HostDirectory.CreateDirectory(Host("inner"));

        using Dir root = OpenRoot();

        Assert.Equal(CapFileType.Directory, root.GetMetadata("inner").Type);
    }

    /// <summary>A path of several components describes the thing at the end of it.</summary>
    [Fact]
    public void A_nested_name_is_described()
    {
        HostDirectory.CreateDirectory(Host("a", "b"));
        HostFile.WriteAllText(Host("a", "b", "leaf"), "12345");

        using Dir root = OpenRoot();
        CapMetadata metadata = root.GetMetadata("a/b/leaf");

        Assert.Equal(CapFileType.File, metadata.Type);
        Assert.Equal(5, metadata.Length);
    }

    /// <summary>
    /// The times are the times, within the slack a filesystem's own clock and resolution
    /// leave.
    /// </summary>
    /// <remarks>
    /// Asserted as a window rather than as a value, because resolution differs by filesystem
    /// — a second on some, a nanosecond on others — and a test that demanded the exact
    /// instant would fail on whichever agent happened to have the coarser one. What is worth
    /// asserting is that the field holds a time from the right era at all: a conversion that
    /// dropped a factor or started from the wrong epoch lands centuries away, not
    /// milliseconds.
    /// </remarks>
    [Fact]
    public void The_times_are_from_around_now()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddMinutes(-5);
        HostFile.WriteAllText(Host("fresh"), "x");
        DateTimeOffset after = DateTimeOffset.UtcNow.AddMinutes(5);

        using Dir root = OpenRoot();
        CapMetadata metadata = root.GetMetadata("fresh");

        Assert.InRange(metadata.LastWriteTime, before, after);
        Assert.InRange(metadata.LastAccessTime, before, after);

        // Absent is a legitimate answer: several filesystems do not record a creation time
        // at all, and saying so is the point of the field being nullable. What is not
        // legitimate is a creation time from an unrelated century, which is what a wrong
        // epoch produces.
        if (metadata.CreationTime is { } created)
        {
            Assert.InRange(created, before, after);
        }
    }

    /// <summary>
    /// The change time is from around now, and it is its own clock rather than the
    /// last-write time under another name.
    /// </summary>
    /// <remarks>
    /// Putting the last-write time back is itself a change to the object, so the change time
    /// stays in the present while the last-write time goes to the date it was given. A field
    /// read from the wrong offset, or filled from the last-write time, follows it into the
    /// past.
    /// </remarks>
    [Fact]
    public void The_change_time_records_that_the_write_time_was_put_back()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddMinutes(-5);
        HostFile.WriteAllText(Host("backdated"), "x");
        HostFile.SetLastWriteTimeUtc(Host("backdated"), new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        DateTimeOffset after = DateTimeOffset.UtcNow.AddMinutes(5);

        using Dir root = OpenRoot();
        CapMetadata metadata = root.GetMetadata("backdated");

        Assert.Equal(2001, metadata.LastWriteTime.Year);

        // Absent only where the filesystem keeps no such clock, which no Unix filesystem
        // does and which on Windows is the FAT family, never the volume a test runs on.
        Assert.NotNull(metadata.ChangeTime);
        Assert.InRange(metadata.ChangeTime.Value, before, after);
    }

    /// <summary>A file with one name reports one link, and a second name raises the count.</summary>
    [Fact]
    public void The_link_count_follows_the_names_a_file_has()
    {
        HostFile.WriteAllText(Host("counted"), "x");

        using Dir root = OpenRoot();

        Assert.Equal(1, root.GetMetadata("counted").LinkCount);

        if (!root.TryCreateHardLink("counted", root, "counted-again"))
        {
            Assert.Skip("A second name for one file could not be created here.");
        }

        Assert.Equal(2, root.GetMetadata("counted").LinkCount);
        Assert.Equal(2, root.GetMetadata("counted-again").LinkCount);

        root.DeleteFile("counted");

        Assert.Equal(1, root.GetMetadata("counted-again").LinkCount);
    }

    /// <summary>
    /// An open file whose last name is removed reports no links, and still describes itself.
    /// </summary>
    /// <remarks>
    /// Unix only, because it is the only family on which a name can be removed while the file
    /// is open without the opener having agreed to it in advance.
    /// </remarks>
    [Fact]
    public void An_open_file_whose_last_name_is_gone_reports_no_links()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("An open file's name cannot be removed out from under it here.");
        }

        HostFile.WriteAllText(Host("orphan"), "x");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("orphan");
        root.DeleteFile("orphan");

        Assert.Equal(0, file.GetMetadata().LinkCount);
    }

    /// <summary>
    /// The permissions are the ones this platform records, and the other platform's
    /// accessor says so rather than inventing a value.
    /// </summary>
    [Fact]
    public void Permissions_describe_this_platform_and_never_the_other()
    {
        HostFile.WriteAllText(Host("subject"), "x");

        using Dir root = OpenRoot();
        CapPermissions permissions = root.GetMetadata("subject").Permissions;

        if (OperatingSystem.IsWindows())
        {
            Assert.True(permissions.TryGetWindowsAttributes(out FileAttributes attributes));
            Assert.NotEqual(default, attributes);
            Assert.False(permissions.TryGetUnixMode(out _));
        }
        else
        {
            Assert.True(permissions.TryGetUnixMode(out UnixFileMode mode));
            Assert.True(mode.HasFlag(UnixFileMode.UserRead));
            Assert.False(permissions.TryGetWindowsAttributes(out _));
        }
    }

    /// <summary>The mode bits reported are the mode bits set, and carry no type bits.</summary>
    [Fact]
    public void The_mode_reported_is_the_mode_that_was_set()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This platform records no mode bits.");
            return;
        }

        const UnixFileMode Wanted =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

        HostFile.WriteAllText(Host("restricted"), "x");
        HostFile.SetUnixFileMode(Host("restricted"), Wanted);

        using Dir root = OpenRoot();

        Assert.True(root.GetMetadata("restricted").Permissions.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(Wanted, mode);
    }

    // --- the name, never what the name leads to ------------------------------------------------

    /// <summary>A symbolic link is described as a link, not as the file it points at.</summary>
    [Fact]
    public void A_symbolic_link_is_described_as_a_link()
    {
        RequireSymbolicLinks();

        HostFile.WriteAllBytes(Host("target"), new byte[4096]);
        HostFile.CreateSymbolicLink(Host("link"), "target");

        using Dir root = OpenRoot();
        CapMetadata metadata = root.GetMetadata("link");

        Assert.Equal(CapFileType.Symlink, metadata.Type);
        Assert.NotEqual(4096, metadata.Length);
        Assert.NotEqual(root.GetMetadata("target").FileId, metadata.FileId);
    }

    /// <summary>
    /// A link to a directory is still described as a link, which is the case a caller
    /// cleaning up an untrusted subtree depends on.
    /// </summary>
    [Fact]
    public void A_link_to_a_directory_is_described_as_a_link()
    {
        RequireSymbolicLinks();

        HostDirectory.CreateDirectory(Host("real"));
        HostDirectory.CreateSymbolicLink(Host("pointer"), "real");

        using Dir root = OpenRoot();

        Assert.Equal(CapFileType.Symlink, root.GetMetadata("pointer").Type);
    }

    /// <summary>
    /// The answer does not depend on the symbolic-link policy the handle carries.
    /// </summary>
    /// <remarks>
    /// The reason the last component is never followed. A policy that decided whether a link
    /// was described or resolved would make the same name describe two different things
    /// through two handles that differ only in a rule about resolution — and would make the
    /// strictest handle, which is the one used to audit a subtree, the one least able to say
    /// what is in it.
    /// </remarks>
    [Fact]
    public void The_answer_is_the_same_under_every_policy()
    {
        RequireSymbolicLinks();

        HostFile.WriteAllText(Host("pointee"), "content");
        HostFile.CreateSymbolicLink(Host("indirect"), "pointee");

        using Dir permissive = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), SymlinkPolicy.FollowWithinSandbox);
        using Dir strict = permissive.Restrict(SymlinkPolicy.Deny);

        Assert.Equal(CapFileType.Symlink, permissive.GetMetadata("indirect").Type);
        Assert.Equal(CapFileType.Symlink, strict.GetMetadata("indirect").Type);
        Assert.Equal(permissive.GetMetadata("indirect").FileId, strict.GetMetadata("indirect").FileId);
    }

    /// <summary>A link met on the way to the name is still subject to the handle's policy.</summary>
    /// <remarks>
    /// The last component is what is described; everything before it is resolved under the
    /// ordinary rules, so a handle that refuses links refuses a path that passes through one.
    /// </remarks>
    [Fact]
    public void A_link_in_the_middle_of_the_path_obeys_the_policy()
    {
        RequireSymbolicLinks();

        HostDirectory.CreateDirectory(Host("actual"));
        HostFile.WriteAllText(Host("actual", "leaf"), "x");
        HostDirectory.CreateSymbolicLink(Host("hop"), "actual");

        using Dir permissive = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), SymlinkPolicy.FollowWithinSandbox);
        using Dir strict = permissive.Restrict(SymlinkPolicy.Deny);

        Assert.Equal(CapFileType.File, permissive.GetMetadata("hop/leaf").Type);
        Assert.ThrowsAny<IOException>(() => strict.GetMetadata("hop/leaf"));
    }

    // --- asking a handle rather than a name ----------------------------------------------------

    /// <summary>A handle describes the directory it was opened on.</summary>
    [Fact]
    public void A_handle_describes_the_directory_it_was_opened_on()
    {
        HostDirectory.CreateDirectory(Host("branch"));

        using Dir root = OpenRoot();
        using Dir branch = root.OpenDir("branch");

        CapMetadata fromHandle = branch.GetMetadata();

        Assert.Equal(CapFileType.Directory, fromHandle.Type);
        Assert.True(fromHandle.IsSameFileAs(root.GetMetadata("branch")));
    }

    /// <summary>
    /// Committing a directory's entries succeeds where the platform can do it, and says it did
    /// nothing where it cannot, rather than failing.
    /// </summary>
    /// <remarks>
    /// The sequence is the one a caller building its own durable publish goes through, and
    /// the published name is checked afterwards so a flush that disturbed the directory would
    /// show. Only Windows lacks the request, so every other platform must report a commit.
    /// </remarks>
    [Fact]
    public void A_directory_commits_its_entries_where_the_platform_can()
    {
        using Dir root = OpenRoot();
        using (CapFile file = root.CreateFile("draft"))
        {
            file.Write("contents"u8, 0);
            file.Flush(toDisk: true);
        }

        root.Rename("draft", root, "final", replaceExisting: true);

        Assert.Equal(!OperatingSystem.IsWindows(), root.Flush(toDisk: true));
        Assert.Equal("contents", HostFile.ReadAllText(Host("final")));
    }

    /// <summary>Not asking to wait does nothing, and reports nothing left undone.</summary>
    [Fact]
    public void A_directory_flush_that_does_not_wait_reports_success_everywhere()
    {
        using Dir root = OpenRoot();

        Assert.True(root.Flush(toDisk: false));
    }

    /// <summary>A disposed handle refuses to flush, whether or not it would have waited.</summary>
    [Fact]
    public void A_disposed_directory_refuses_to_flush()
    {
        Dir root = OpenRoot();
        root.Dispose();

        Assert.Throws<ObjectDisposedException>(() => root.Flush(toDisk: true));
        Assert.Throws<ObjectDisposedException>(() => root.Flush(toDisk: false));
    }

    /// <summary>An open file describes itself.</summary>
    [Fact]
    public void An_open_file_describes_itself()
    {
        HostFile.WriteAllBytes(Host("sized"), new byte[77]);

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("sized");

        CapMetadata metadata = file.GetMetadata();

        Assert.Equal(CapFileType.File, metadata.Type);
        Assert.Equal(77, metadata.Length);
        Assert.Equal(file.Length, metadata.Length);
    }

    /// <summary>
    /// A handle keeps describing the object it was opened on after the name it was opened by
    /// has been given to something else.
    /// </summary>
    /// <remarks>
    /// This is the property the whole design rests on, stated as a test: nothing about
    /// describing an open handle involves a name, so nothing a rename does can redirect it.
    /// An implementation that answered by re-resolving the path it was opened by — the
    /// natural mistake, and what the framework's own path-based types do — would describe the
    /// impostor instead, and would do so silently.
    /// </remarks>
    [Fact]
    public void An_open_handle_is_unaffected_by_the_name_being_reassigned()
    {
        HostFile.WriteAllBytes(Host("original"), new byte[10]);

        using Dir root = OpenRoot();

        // Shared for deletion, which on Windows is what lets the name be moved while the file
        // is open; without it the rename below is refused before the property can be tested.
        using CapFile file = root.OpenFile(
            "original", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        CapFileId before = file.GetMetadata().FileId;

        HostFile.Move(Host("original"), Host("moved"));
        HostFile.WriteAllBytes(Host("original"), new byte[9999]);

        CapMetadata after = file.GetMetadata();

        Assert.Equal(before, after.FileId);
        Assert.Equal(10, after.Length);
        Assert.NotEqual(before, root.GetMetadata("original").FileId);
        Assert.Equal(before, root.GetMetadata("moved").FileId);
    }

    // --- identity ------------------------------------------------------------------------------

    /// <summary>Two names for one file report one identity.</summary>
    [Fact]
    public void Hard_links_to_one_file_are_reported_as_the_same_file()
    {
        HostFile.WriteAllText(Host("first"), "shared");

        using Dir root = OpenRoot();

        if (!root.TryCreateHardLink("first", root, "second"))
        {
            Assert.Skip("A second name for one file could not be created here.");
        }

        CapMetadata first = root.GetMetadata("first");
        CapMetadata second = root.GetMetadata("second");

        Assert.True(first.IsSameFileAs(second));
        Assert.Equal(first.FileId, second.FileId);
    }

    /// <summary>
    /// Two separate files that look identical in every other field are not the same file.
    /// </summary>
    /// <remarks>
    /// The negative half of the same property, and the one that catches an implementation
    /// that compared the wrong things. Length and timestamps are made to match deliberately,
    /// so a comparison built on those agrees where it must not.
    /// </remarks>
    [Fact]
    public void Two_distinct_files_that_look_alike_are_not_the_same_file()
    {
        HostFile.WriteAllText(Host("twin-a"), "identical");
        HostFile.WriteAllText(Host("twin-b"), "identical");

        DateTime stamp = new(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        HostFile.SetLastWriteTimeUtc(Host("twin-a"), stamp);
        HostFile.SetLastWriteTimeUtc(Host("twin-b"), stamp);
        HostFile.SetLastAccessTimeUtc(Host("twin-a"), stamp);
        HostFile.SetLastAccessTimeUtc(Host("twin-b"), stamp);

        using Dir root = OpenRoot();

        CapMetadata a = root.GetMetadata("twin-a");
        CapMetadata b = root.GetMetadata("twin-b");

        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a.LastWriteTime, b.LastWriteTime);

        Assert.False(a.IsSameFileAs(b));
        Assert.NotEqual(a.FileId, b.FileId);
    }

    /// <summary>An identity survives the file being renamed, because it is not a name.</summary>
    [Fact]
    public void An_identity_survives_a_rename()
    {
        HostFile.WriteAllText(Host("before"), "x");

        using Dir root = OpenRoot();
        CapFileId identity = root.GetMetadata("before").FileId;

        root.Rename("before", root, "after");

        Assert.Equal(identity, root.GetMetadata("after").FileId);
    }

    // --- entries -------------------------------------------------------------------------------

    /// <summary>An entry from an enumeration describes the object the entry names.</summary>
    [Fact]
    public void An_entry_describes_what_it_names()
    {
        HostFile.WriteAllBytes(Host("listed"), new byte[64]);

        using Dir root = OpenRoot();
        DirEntry entry = root.EnumerateEntries().Single(candidate => candidate.Name == "listed");

        CapMetadata metadata = entry.GetMetadata();

        Assert.Equal(CapFileType.File, metadata.Type);
        Assert.Equal(64, metadata.Length);
        Assert.True(metadata.IsSameFileAs(root.GetMetadata("listed")));
    }

    /// <summary>An entry that has gone is reported as gone rather than thrown about.</summary>
    [Fact]
    public void An_entry_that_has_gone_reports_failure()
    {
        HostFile.WriteAllText(Host("fleeting"), "x");

        using Dir root = OpenRoot();
        DirEntry entry = root.EnumerateEntries().Single(candidate => candidate.Name == "fleeting");

        HostFile.Delete(Host("fleeting"));

        Assert.False(entry.TryGetMetadata(out CapMetadata metadata));
        Assert.Equal(CapFileType.Unknown, metadata.Type);
    }

    // --- refusals ------------------------------------------------------------------------------

    /// <summary>A name that is not there is reported as a missing file.</summary>
    [Fact]
    public void A_missing_name_is_reported_as_a_missing_file()
    {
        using Dir root = OpenRoot();

        Assert.Throws<FileNotFoundException>(() => root.GetMetadata("absent"));
        Assert.False(root.TryGetMetadata("absent", out _));
    }

    /// <summary>A path that leaves the subtree is refused rather than described.</summary>
    [Fact]
    public void A_path_that_climbs_out_is_refused()
    {
        using Dir root = OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.GetMetadata("../outside"));
        Assert.Throws<SandboxEscapeException>(
            () => root.GetMetadata(OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc"));
    }

    /// <summary>
    /// A path spelled so that it has to name a directory does not describe a file.
    /// </summary>
    [Fact]
    public void A_path_spelled_as_a_directory_does_not_describe_a_file()
    {
        HostFile.WriteAllText(Host("plain"), "x");
        HostDirectory.CreateDirectory(Host("folder"));

        using Dir root = OpenRoot();

        Assert.Equal(CapFileType.Directory, root.GetMetadata("folder/").Type);
        Assert.False(root.TryGetMetadata("plain/", out _));
    }

    /// <summary>A disposed handle answers nothing.</summary>
    [Fact]
    public void A_disposed_handle_answers_nothing()
    {
        HostFile.WriteAllText(Host("anything"), "x");

        Dir root = OpenRoot();
        root.Dispose();

        Assert.Throws<ObjectDisposedException>(() => root.GetMetadata());
        Assert.Throws<ObjectDisposedException>(() => root.GetMetadata("anything"));
    }

    /// <summary>A null path is a mistake in the caller and is reported as one.</summary>
    [Fact]
    public void A_null_path_is_rejected()
    {
        using Dir root = OpenRoot();

        Assert.Throws<ArgumentNullException>(() => root.GetMetadata(null!));
        Assert.Throws<ArgumentNullException>(() => root.TryGetMetadata(null!, out _));
    }

    // --- helpers -------------------------------------------------------------------------------

    private void RequireSymbolicLinks()
    {
        string probe = Host("link-probe");

        try
        {
            HostFile.CreateSymbolicLink(probe, "target");
            HostFile.Delete(probe);
        }
        catch (Exception thrown) when (
            thrown is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic links cannot be created here, so these cases cannot be built: {thrown.Message}");
        }
    }
}
