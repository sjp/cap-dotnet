using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Making, moving and removing things through a handle.
/// </summary>
/// <remarks>
/// <para>
/// These are the operations that change the filesystem, so they are the ones where getting
/// containment wrong does lasting damage rather than merely disclosing something. Two
/// properties are worth insisting on throughout, and most of what is here is one or the
/// other.
/// </para>
/// <para>
/// The first is that a path which tries to leave is refused by every one of them, in the
/// same way and with the same distinguishable exception. A refusal that held for opening and
/// not for deleting would be worse than no refusal at all, because the containment would
/// look tested.
/// </para>
/// <para>
/// The second is that each acts on the name it was given and not on what the name points at.
/// A symbolic link is removed as a link, moved as a link and linked to as a link, and its
/// target is never reached — which is what makes it safe to hand a subtree of untrusted
/// content to something whose job is to clean it up.
/// </para>
/// <para>
/// Set-up uses the ambient filesystem deliberately: the scenarios being attacked have to be
/// built by something that is not the code under test, or a bug in that code would build a
/// scenario it is happy with.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirMutationTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    // --- creating directories ---------------------------------------------------------------

    /// <summary>A created directory is there afterwards, and the handle refers to it.</summary>
    [Fact]
    public void A_created_directory_exists_and_the_handle_opens_it()
    {
        using Dir root = OpenRoot();
        using Dir made = root.CreateDir("fresh");

        Assert.True(Directory.Exists(Host("fresh")));

        using Dir reopened = root.OpenDir("fresh");
        Assert.True(made.TryClone(out Dir? copy));
        copy!.Dispose();
    }

    /// <summary>Creation resolves the components ahead of the last one.</summary>
    [Fact]
    public void A_nested_name_is_created_beneath_directories_that_already_exist()
    {
        Directory.CreateDirectory(Host("a", "b"));

        using Dir root = OpenRoot();
        using Dir made = root.CreateDir("a/b/c");

        Assert.True(Directory.Exists(Host("a", "b", "c")));
    }

    /// <summary>A directory above the new one has to exist already.</summary>
    [Fact]
    public void A_missing_directory_above_the_new_one_is_reported_as_missing()
    {
        using Dir root = OpenRoot();

        Assert.Throws<DirectoryNotFoundException>(() => root.CreateDir("absent/child"));
    }

    /// <summary>The exclusive form refuses a name that is taken, whatever holds it.</summary>
    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    public void Creating_refuses_a_name_that_is_already_taken(string kind)
    {
        if (kind == "directory")
        {
            Directory.CreateDirectory(Host("taken"));
        }
        else
        {
            File.WriteAllText(Host("taken"), "contents");
        }

        using Dir root = OpenRoot();

        Exception thrown = Assert.ThrowsAny<IOException>(() => root.CreateDir("taken"));
        Assert.IsNotType<SandboxEscapeException>(thrown);
        Assert.False(root.TryCreateDir("taken", out Dir? dir));
        Assert.Null(dir);
    }

    /// <summary>The tolerant form accepts a directory that is already there.</summary>
    [Fact]
    public void Opening_or_creating_accepts_a_directory_that_already_exists()
    {
        Directory.CreateDirectory(Host("present"));

        using Dir root = OpenRoot();
        using Dir opened = root.OpenOrCreateDir("present");
        using Dir created = root.OpenOrCreateDir("other");

        Assert.True(Directory.Exists(Host("other")));
    }

    /// <summary>It does not accept a name held by something that is not a directory.</summary>
    [Fact]
    public void Opening_or_creating_refuses_a_name_held_by_a_file()
    {
        File.WriteAllText(Host("file"), "contents");

        using Dir root = OpenRoot();

        _ = Assert.ThrowsAny<IOException>(() => root.OpenOrCreateDir("file"));
    }

    /// <summary>
    /// A symbolic link standing where the directory should be is not accepted as one.
    /// </summary>
    /// <remarks>
    /// The open that follows the creation refuses to follow a link, so a link planted at the
    /// name cannot decide where the caller's directory really is — which is the whole reason
    /// the tolerant form is not simply "create, ignoring failure, then open".
    /// </remarks>
    [Fact]
    public void Opening_or_creating_refuses_a_symbolic_link_standing_in_for_the_directory()
    {
        Directory.CreateDirectory(Host("real"));
        Directory.CreateSymbolicLink(Host("link"), "real");

        using Dir root = OpenRoot();

        _ = Assert.ThrowsAny<IOException>(() => root.OpenOrCreateDir("link"));
    }

    // --- removing -----------------------------------------------------------------------------

    /// <summary>Removing a name removes it.</summary>
    [Fact]
    public void A_file_is_removed()
    {
        File.WriteAllText(Host("doomed"), "contents");

        using Dir root = OpenRoot();
        root.DeleteFile("doomed");

        Assert.False(File.Exists(Host("doomed")));
    }

    /// <summary>A name that is not there is a failure, and the reporting form says so quietly.</summary>
    [Fact]
    public void Removing_a_name_that_is_not_there_fails()
    {
        using Dir root = OpenRoot();

        _ = Assert.Throws<FileNotFoundException>(() => root.DeleteFile("absent"));
        Assert.False(root.TryDeleteFile("absent"));
    }

    /// <summary>A directory is not removed by the call that removes names.</summary>
    [Fact]
    public void Removing_a_file_refuses_a_directory()
    {
        Directory.CreateDirectory(Host("dir"));

        using Dir root = OpenRoot();

        Exception thrown = Assert.ThrowsAny<IOException>(() => root.DeleteFile("dir"));
        Assert.IsNotType<SandboxEscapeException>(thrown);
        Assert.True(Directory.Exists(Host("dir")));
    }

    /// <summary>
    /// A symbolic link is removed as itself, and whatever it named is left alone.
    /// </summary>
    /// <remarks>
    /// Including a link that points outside the subtree, which is the case that matters: if
    /// removal followed the link, handing a directory of untrusted content to something that
    /// tidies it up would be a way to make that code delete a file elsewhere on the host.
    /// </remarks>
    [Fact]
    public void Removing_a_symbolic_link_removes_the_link_and_not_its_target()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"cap-outside-{Guid.NewGuid():N}");
        File.WriteAllText(outside, "must survive");

        try
        {
            File.CreateSymbolicLink(Host("escaping"), outside);

            using Dir root = OpenRoot();
            root.DeleteFile("escaping");

            Assert.False(Path.Exists(Host("escaping")));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>The strictest link policy does not take link removal away.</summary>
    /// <remarks>
    /// A handle restricted because the subtree's links are not trusted would be useless if
    /// the restriction stopped it clearing them out. Following a link on the way to something
    /// else and acting on a link by name are different questions.
    /// </remarks>
    [Fact]
    public void A_handle_that_refuses_to_follow_links_can_still_remove_one()
    {
        File.CreateSymbolicLink(Host("link"), "target");

        using Dir root = OpenRoot();
        using Dir strict = root.Restrict(SymlinkPolicy.Deny);

        strict.DeleteFile("link");

        Assert.False(Path.Exists(Host("link")));
    }

    /// <summary>An empty directory is removed.</summary>
    [Fact]
    public void An_empty_directory_is_removed()
    {
        Directory.CreateDirectory(Host("empty"));

        using Dir root = OpenRoot();
        root.DeleteDir("empty");

        Assert.False(Directory.Exists(Host("empty")));
    }

    /// <summary>A directory with anything in it is not.</summary>
    [Fact]
    public void A_directory_with_entries_is_not_removed()
    {
        Directory.CreateDirectory(Host("full"));
        File.WriteAllText(Host("full", "entry"), "contents");

        using Dir root = OpenRoot();

        Exception thrown = Assert.ThrowsAny<IOException>(() => root.DeleteDir("full"));
        Assert.IsNotType<SandboxEscapeException>(thrown);
        Assert.True(Directory.Exists(Host("full")));
    }

    /// <summary>A link that points at a directory is not a directory.</summary>
    [Fact]
    public void Removing_a_directory_refuses_a_link_that_points_at_one()
    {
        Directory.CreateDirectory(Host("real"));
        Directory.CreateSymbolicLink(Host("link"), "real");

        using Dir root = OpenRoot();

        _ = Assert.ThrowsAny<IOException>(() => root.DeleteDir("link"));
        Assert.True(Directory.Exists(Host("real")));
        Assert.True(Path.Exists(Host("link")));
    }

    /// <summary>A trailing separator insists on a directory, so it cannot remove a file.</summary>
    [Fact]
    public void A_name_spelled_as_a_directory_does_not_remove_a_file()
    {
        File.WriteAllText(Host("file"), "contents");

        using Dir root = OpenRoot();

        _ = Assert.ThrowsAny<IOException>(() => root.DeleteFile("file/"));
        Assert.True(File.Exists(Host("file")));
    }

    // --- moving -------------------------------------------------------------------------------

    /// <summary>A move puts the entry under its new name.</summary>
    [Fact]
    public void An_entry_is_moved_to_its_new_name()
    {
        File.WriteAllText(Host("before"), "contents");

        using Dir root = OpenRoot();
        root.Rename("before", root, "after");

        Assert.False(File.Exists(Host("before")));
        Assert.Equal("contents", File.ReadAllText(Host("after")));
    }

    /// <summary>The destination handle need not be the source handle.</summary>
    [Fact]
    public void An_entry_is_moved_between_two_handles()
    {
        Directory.CreateDirectory(Host("from"));
        Directory.CreateDirectory(Host("to"));
        File.WriteAllText(Host("from", "entry"), "contents");

        using Dir root = OpenRoot();
        using Dir source = root.OpenDir("from");
        using Dir destination = root.OpenDir("to");

        source.Rename("entry", destination, "entry");

        Assert.False(File.Exists(Host("from", "entry")));
        Assert.Equal("contents", File.ReadAllText(Host("to", "entry")));
    }

    /// <summary>By default a name already in use is not destroyed.</summary>
    [Fact]
    public void A_move_refuses_a_destination_that_is_taken()
    {
        File.WriteAllText(Host("source"), "new");
        File.WriteAllText(Host("destination"), "old");

        using Dir root = OpenRoot();

        Exception thrown = Assert.ThrowsAny<IOException>(() => root.Rename("source", root, "destination"));
        Assert.IsNotType<SandboxEscapeException>(thrown);
        Assert.Equal("old", File.ReadAllText(Host("destination")));
        Assert.True(File.Exists(Host("source")));
    }

    /// <summary>Asking for replacement replaces, which is how a file is published atomically.</summary>
    [Fact]
    public void A_move_replaces_the_destination_when_asked_to()
    {
        File.WriteAllText(Host("source"), "new");
        File.WriteAllText(Host("destination"), "old");

        using Dir root = OpenRoot();
        root.Rename("source", root, "destination", replaceExisting: true);

        Assert.Equal("new", File.ReadAllText(Host("destination")));
        Assert.False(File.Exists(Host("source")));
    }

    /// <summary>A missing source is a missing thing, not an escape.</summary>
    [Fact]
    public void A_move_of_something_that_is_not_there_fails()
    {
        using Dir root = OpenRoot();

        _ = Assert.Throws<FileNotFoundException>(() => root.Rename("absent", root, "elsewhere"));
        Assert.False(root.TryRename("absent", root, "elsewhere"));
    }

    /// <summary>A directory moves as readily as a file, with everything under it.</summary>
    [Fact]
    public void A_directory_is_moved_with_its_contents()
    {
        Directory.CreateDirectory(Host("before", "inner"));
        File.WriteAllText(Host("before", "inner", "entry"), "contents");

        using Dir root = OpenRoot();
        root.Rename("before", root, "after");

        Assert.False(Directory.Exists(Host("before")));
        Assert.Equal("contents", File.ReadAllText(Host("after", "inner", "entry")));
    }

    /// <summary>
    /// A move that would put a directory inside itself is refused as the wrong request, not
    /// as a containment failure or a platform limitation.
    /// </summary>
    /// <remarks>
    /// Both names are inside the subtree, so nothing about it is an escape; the filesystem
    /// simply cannot do it. Worth a test because it is the one ordinary way to provoke the
    /// filesystem's "not a request I can carry out" answer, which would otherwise only be
    /// reached by code paths nobody exercises.
    /// </remarks>
    [Fact]
    public void A_move_of_a_directory_beneath_itself_is_refused()
    {
        Directory.CreateDirectory(Host("outer", "inner"));

        using Dir root = OpenRoot();
        using Dir inner = root.OpenDir("outer/inner");

        Exception thrown = Assert.ThrowsAny<IOException>(() => root.Rename("outer", inner, "swallowed"));
        Assert.IsNotType<SandboxEscapeException>(thrown);
        Assert.True(Directory.Exists(Host("outer", "inner")));
    }

    // --- linking ------------------------------------------------------------------------------

    /// <summary>A symbolic link stores the text it was given, and reads back as that text.</summary>
    [Fact]
    public void A_symbolic_link_stores_its_target_verbatim()
    {
        File.WriteAllText(Host("real"), "contents");

        using Dir root = OpenRoot();
        root.CreateSymlink("alias", "real");

        Assert.Equal("real", root.ReadLink("alias"));
        Assert.Equal("contents", File.ReadAllText(Host("alias")));
    }

    /// <summary>
    /// A target that leaves the subtree is stored, and refused when something follows it.
    /// </summary>
    /// <remarks>
    /// Containment belongs where a link is followed, not where it is made. The stored text is
    /// data; deciding it is unacceptable at creation would refuse links that are perfectly
    /// good — whether a relative target leaves depends on where the link ends up — and would
    /// add nothing, because resolution refuses such a target from the text alone under every
    /// policy.
    /// </remarks>
    [Fact]
    public void A_link_whose_target_leaves_the_subtree_is_stored_and_refused_when_followed()
    {
        using Dir root = OpenRoot();
        root.CreateDirSymlink("escape", "../../..");

        Assert.Equal("../../..", root.ReadLink("escape"));
        _ = Assert.Throws<SandboxEscapeException>(() => root.OpenDir("escape"));
    }

    /// <summary>Reading a link is not following one, so the strictest policy still allows it.</summary>
    [Fact]
    public void A_handle_that_refuses_to_follow_links_can_still_read_one()
    {
        using Dir root = OpenRoot();
        root.CreateSymlink("alias", "wherever");

        using Dir strict = root.Restrict(SymlinkPolicy.Deny);

        Assert.True(strict.TryReadLink("alias", out string? target));
        Assert.Equal("wherever", target);
    }

    /// <summary>Asking a name that is not a link for its target is an ordinary no.</summary>
    [Fact]
    public void A_name_that_is_not_a_link_has_no_target()
    {
        File.WriteAllText(Host("plain"), "contents");

        using Dir root = OpenRoot();

        Assert.False(root.TryReadLink("plain", out string? target));
        Assert.Null(target);
        _ = Assert.ThrowsAny<IOException>(() => root.ReadLink("plain"));
    }

    /// <summary>A hard link is a second name for the same object.</summary>
    [Fact]
    public void A_hard_link_is_a_second_name_for_the_same_object()
    {
        File.WriteAllText(Host("original"), "contents");

        using Dir root = OpenRoot();
        root.CreateHardLink("original", root, "second");

        File.WriteAllText(Host("original"), "changed");
        Assert.Equal("changed", File.ReadAllText(Host("second")));
    }

    /// <summary>It never overwrites: a name in use is a failure.</summary>
    [Fact]
    public void A_hard_link_refuses_a_name_that_is_taken()
    {
        File.WriteAllText(Host("original"), "contents");
        File.WriteAllText(Host("taken"), "other");

        using Dir root = OpenRoot();

        _ = Assert.ThrowsAny<IOException>(() => root.CreateHardLink("original", root, "taken"));
        Assert.Equal("other", File.ReadAllText(Host("taken")));
    }

    // --- asking what is there -------------------------------------------------------------------

    /// <summary>A name that is taken is reported as taken.</summary>
    [Fact]
    public void A_name_that_is_taken_exists()
    {
        File.WriteAllText(Host("file"), "contents");
        Directory.CreateDirectory(Host("dir"));

        using Dir root = OpenRoot();

        Assert.True(root.Exists("file"));
        Assert.True(root.Exists("dir"));
        Assert.False(root.Exists("absent"));
    }

    /// <summary>
    /// A link counts as present whether or not anything is at the other end.
    /// </summary>
    /// <remarks>
    /// The question is about the name, not about what the name leads to. Answering the other
    /// question would make the answer depend on the handle's link policy, so a name would
    /// exist through one handle and not through another.
    /// </remarks>
    [Fact]
    public void A_link_that_points_nowhere_still_holds_its_name()
    {
        using Dir root = OpenRoot();
        root.CreateSymlink("dangling", "nothing-here");
        root.CreateSymlink("escaping", "../../../etc/passwd");

        Assert.True(root.Exists("dangling"));
        Assert.True(root.Exists("escaping"));
    }

    /// <summary>A name spelled as a directory is present only if a directory holds it.</summary>
    [Fact]
    public void A_name_spelled_as_a_directory_exists_only_when_it_is_one()
    {
        File.WriteAllText(Host("file"), "contents");
        Directory.CreateDirectory(Host("dir"));

        using Dir root = OpenRoot();

        Assert.False(root.Exists("file/"));
        Assert.True(root.Exists("dir/"));
    }

    // --- containment --------------------------------------------------------------------------

    /// <summary>
    /// Every operation that changes something refuses a path that tries to leave, in the same
    /// way.
    /// </summary>
    /// <remarks>
    /// Written as one theory over all of them rather than as one test each, because the
    /// failure being guarded against is a member that was added later and never got the
    /// check. A list that has to be extended by hand for each new member is the wrong shape;
    /// what makes this work is that every member routes through one resolution step, and this
    /// asserts that none of them has grown a second route.
    /// </remarks>
    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("/etc/passwd")]
    public void Every_mutating_operation_refuses_a_path_that_leaves(string path)
    {
        Directory.CreateDirectory(Host("a"));
        File.WriteAllText(Host("subject"), "contents");

        using Dir root = OpenRoot();

        _ = Assert.Throws<SandboxEscapeException>(() => root.CreateDir(path));
        _ = Assert.Throws<SandboxEscapeException>(() => root.OpenOrCreateDir(path));
        _ = Assert.Throws<SandboxEscapeException>(() => root.DeleteFile(path));
        _ = Assert.Throws<SandboxEscapeException>(() => root.DeleteDir(path));
        _ = Assert.Throws<SandboxEscapeException>(() => root.ReadLink(path));
        _ = Assert.Throws<SandboxEscapeException>(() => root.CreateSymlink(path, "target"));
        _ = Assert.Throws<SandboxEscapeException>(() => root.CreateDirSymlink(path, "target"));
        _ = Assert.Throws<SandboxEscapeException>(() => root.Rename("subject", root, path));
        _ = Assert.Throws<SandboxEscapeException>(() => root.Rename(path, root, "landing"));
        _ = Assert.Throws<SandboxEscapeException>(() => root.CreateHardLink("subject", root, path));
        _ = Assert.Throws<SandboxEscapeException>(() => root.CreateHardLink(path, root, "landing"));

        Assert.False(root.Exists(path));
    }

    /// <summary>
    /// A refusal names the argument the caller wrote, not a name invented on the way.
    /// </summary>
    /// <remarks>
    /// Worth checking because every one of these routes through the same handful of helpers,
    /// which do not know what their caller calls its parameters. A caller catching the
    /// exception and reporting which of two arguments was wrong would otherwise be told the
    /// same thing whichever it was.
    /// </remarks>
    [Fact]
    public void A_refusal_names_the_argument_it_came_from()
    {
        File.WriteAllText(Host("subject"), "contents");

        using Dir root = OpenRoot();

        Assert.Equal(
            "linkPath",
            Assert.Throws<ArgumentException>(() => root.CreateSymlink("bad\0name", "target")).ParamName);
        Assert.Equal(
            "to",
            Assert.Throws<ArgumentException>(() => root.Rename("subject", root, "bad\0name")).ParamName);
        Assert.Equal(
            "from",
            Assert.Throws<ArgumentException>(() => root.Rename("bad\0name", root, "landing")).ParamName);
    }

    /// <summary>The reporting forms refuse the same paths, without building an exception.</summary>
    [Fact]
    public void The_reporting_forms_refuse_a_path_that_leaves_by_returning_false()
    {
        File.WriteAllText(Host("subject"), "contents");

        using Dir root = OpenRoot();

        Assert.False(root.TryCreateDir("../outside", out _));
        Assert.False(root.TryOpenOrCreateDir("../outside", out _));
        Assert.False(root.TryDeleteFile("../outside"));
        Assert.False(root.TryDeleteDir("../outside"));
        Assert.False(root.TryReadLink("../outside", out _));
        Assert.False(root.TryCreateSymlink("../outside", "target"));
        Assert.False(root.TryCreateDirSymlink("../outside", "target"));
        Assert.False(root.TryRename("subject", root, "../outside"));
        Assert.False(root.TryCreateHardLink("subject", root, "../outside"));
    }

    /// <summary>
    /// A symbolic link in the middle of a path cannot carry an operation out of the subtree.
    /// </summary>
    [Fact]
    public void A_link_used_as_a_directory_component_cannot_carry_a_removal_outside()
    {
        string outside = Directory.CreateTempSubdirectory("cap-outside-").FullName;
        File.WriteAllText(Path.Combine(outside, "victim"), "must survive");

        try
        {
            Directory.CreateSymbolicLink(Host("door"), outside);

            using Dir root = OpenRoot();

            _ = Assert.Throws<SandboxEscapeException>(() => root.DeleteFile("door/victim"));
            Assert.True(File.Exists(Path.Combine(outside, "victim")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>A disposed handle performs nothing.</summary>
    [Fact]
    public void A_disposed_handle_refuses_every_operation()
    {
        Dir root = OpenRoot();
        root.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => root.CreateDir("x"));
        _ = Assert.Throws<ObjectDisposedException>(() => root.DeleteFile("x"));
        _ = Assert.Throws<ObjectDisposedException>(() => root.DeleteDir("x"));
        _ = Assert.Throws<ObjectDisposedException>(() => root.Exists("x"));
        _ = Assert.Throws<ObjectDisposedException>(() => root.ReadLink("x"));
        _ = Assert.Throws<ObjectDisposedException>(() => root.TryDeleteFile("x"));
    }
}
