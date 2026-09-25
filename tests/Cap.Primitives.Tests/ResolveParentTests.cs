using Cap.Primitives.Interop;
using Cap.Std.Testing;
using Cap.Tests.Fakes;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// Stopping one component short, which is what every operation that acts on a name needs.
/// </summary>
/// <remarks>
/// <para>
/// Creating, removing, renaming, linking and reading a link cannot be expressed as an open:
/// a create has to fail when the name is taken, a removal has to unlink the link rather than
/// its target. Each therefore resolves everything ahead of the last component and then makes
/// exactly one call of its own against the directory that produced.
/// </para>
/// <para>
/// The two backends get there differently and must arrive at the same place. Where the
/// kernel resolves a whole path in one confined operation, the text is divided first and the
/// prefix goes to the kernel; everywhere else the prefix is walked a component at a time.
/// Running every case against both is the point of this class — a difference between them
/// would be a path that can be deleted on one platform and not on another, or worse, one
/// that escapes on one and not the other.
/// </para>
/// <para>
/// The last component is deliberately never looked at, so these cases include names that do
/// not exist. That is not an edge case; it is what a create is.
/// </para>
/// </remarks>
public sealed class ResolveParentTests
{
    /// <summary>A single name is looked up in the directory the caller already holds.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_single_name_belongs_to_the_root_itself(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        MemoryNode root = fs.Find("sandbox")!;

        Run(fs, (ops, handle) =>
        {
            using ResolvedParent parent = Resolve(handle, "name");

            Assert.Equal("name", parent.Name);
            AssertIs(ops, root, parent.Directory);
        });
    }

    /// <summary>A longer path resolves to the directory holding its last component.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_nested_path_resolves_to_the_directory_holding_the_last_name(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        MemoryNode holder = fs.AddDirectory("sandbox/a/b");

        Run(fs, (ops, handle) =>
        {
            using ResolvedParent parent = Resolve(handle, "a/b/entry");

            Assert.Equal("entry", parent.Name);
            AssertIs(ops, holder, parent.Directory);
        });
    }

    /// <summary>
    /// The last component is not looked up, so it need not be there.
    /// </summary>
    /// <remarks>
    /// The property everything that creates something depends on. A resolution that checked
    /// would both cost a lookup on every create and answer a question that is already stale
    /// by the time the create runs.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_last_component_need_not_exist(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        MemoryNode holder = fs.AddDirectory("sandbox/a");

        Run(fs, (ops, handle) =>
        {
            using ResolvedParent parent = Resolve(handle, "a/nothing-here");

            Assert.Equal("nothing-here", parent.Name);
            AssertIs(ops, holder, parent.Directory);
        });
    }

    /// <summary>A trailing separator does not change which component is the last one.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_trailing_separator_does_not_move_the_last_name(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        MemoryNode holder = fs.AddDirectory("sandbox/a");

        Run(fs, (ops, handle) =>
        {
            using ResolvedParent parent = Resolve(handle, "a/entry/");

            Assert.Equal("entry", parent.Name);
            AssertIs(ops, holder, parent.Directory);
        });
    }

    /// <summary>Redundant separators and <c>.</c> components are dropped, as they are anywhere.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Redundant_separators_and_dot_components_are_ignored(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        MemoryNode holder = fs.AddDirectory("sandbox/a/b");

        Run(fs, (ops, handle) =>
        {
            using ResolvedParent parent = Resolve(handle, "./a//./b/entry");

            Assert.Equal("entry", parent.Name);
            AssertIs(ops, holder, parent.Directory);
        });
    }

    /// <summary>
    /// A link in the middle of the path is followed, and the name lands where the link points.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_link_used_as_a_directory_component_is_followed(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        MemoryNode holder = fs.AddDirectory("sandbox/real");
        _ = fs.AddSymbolicLink("sandbox/door", "real");

        Run(fs, (ops, handle) =>
        {
            using ResolvedParent parent = Resolve(handle, "door/entry");

            Assert.Equal("entry", parent.Name);
            AssertIs(ops, holder, parent.Directory);
        });
    }

    /// <summary>
    /// A link in the middle that leaves the subtree carries nothing out of it.
    /// </summary>
    /// <remarks>
    /// The case the whole thing exists for. If a name-acting operation resolved its prefix
    /// less carefully than an open does, a link planted inside the sandbox would be enough to
    /// aim a deletion at a file outside it.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_link_that_leaves_the_subtree_is_refused(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        _ = fs.AddDirectory("secret");
        _ = fs.AddSymbolicLink("sandbox/door", "/secret");

        Run(fs, (ops, handle) => AssertFails(CapErrorCategory.Escaped, handle, "door/victim"));
    }

    /// <summary>A prefix that climbs above the root is refused rather than clamped to it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_prefix_that_climbs_above_the_root_is_refused(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        _ = fs.AddDirectory("sandbox/a");
        _ = fs.AddDirectory("secret");

        Run(fs, (ops, handle) => AssertFails(CapErrorCategory.Escaped, handle, "a/../../secret/victim"));
    }

    /// <summary>A missing directory above the name is reported as missing.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_missing_directory_above_the_name_is_reported(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);

        Run(fs, (ops, handle) => AssertFails(CapErrorCategory.NotFound, handle, "absent/entry"));
    }

    /// <summary>
    /// The strictest link policy refuses a link in the prefix, and still permits the name.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_refused_link_in_the_prefix_stops_the_resolution(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        _ = fs.AddDirectory("sandbox/real");
        _ = fs.AddSymbolicLink("sandbox/door", "real");

        Run(fs, (ops, handle) => AssertFails(
            CapErrorCategory.SymbolicLinkLoop,
            handle,
            "door/entry",
            ConfinedResolveOptions.RefuseSymlinks));
    }

    /// <summary>
    /// A path that ends by stepping up names nothing an operation could act on.
    /// </summary>
    /// <remarks>
    /// Both backends have to say so, and they arrive at it differently: one divides the text
    /// and sees what the last component is, the other walks until nothing is pending and
    /// finds itself standing on a directory with no final name. A disagreement here would be
    /// a path that can be deleted through on one platform and not on another.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_path_that_ends_by_stepping_up_names_nothing_to_act_on(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        _ = fs.AddDirectory("sandbox/a");

        Run(fs, (ops, handle) => AssertFails(CapErrorCategory.InvalidArgument, handle, "a/.."));
    }

    /// <summary>
    /// The directory handed back is the resolution's own, and closing it leaves the root
    /// working.
    /// </summary>
    /// <remarks>
    /// A single-component path is the case worth checking: the directory is the caller's own
    /// root, and handing back the caller's handle rather than a copy of it would mean the
    /// first operation to finish closed the capability it was performed through.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disposing_the_resolution_leaves_the_root_usable(bool atomic)
    {
        FakeFileSystem fs = Sandbox(atomic);
        MemoryNode root = fs.Find("sandbox")!;

        Run(fs, (ops, handle) =>
        {
            Resolve(handle, "name").Dispose();

            using ResolvedParent again = Resolve(handle, "other");
            AssertIs(ops, root, again.Directory);
        });
    }

    private static FakeFileSystem Sandbox(bool atomic)
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = atomic };
        _ = fs.AddDirectory("sandbox");
        return fs;
    }

    /// <summary>
    /// Parses under POSIX rules on every platform, because the simulated filesystem is not
    /// any platform's, and preserving <c>..</c> so that a prefix which climbs can be put to
    /// the resolver rather than refused by the parser first.
    /// </summary>
    private static CapPath Parse(string raw)
    {
        Assert.True(
            CapPath.TryParse(raw, CapPathSyntax.Unix, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError error),
            $"'{raw}' did not parse: {error}");
        return path;
    }

    private static void Run(FakeFileSystem fs, Action<FakePlatformOps, SafeDirHandle> body)
    {
        FakePlatformOps ops = new(fs);
        CapResult<SafeDirHandle> root = ops.OpenAmbientDirectory("sandbox", CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);

        using SafeDirHandle handle = root.Value!;
        body(ops, handle);
    }

    private static ResolvedParent Resolve(
        SafeDirHandle root,
        string path,
        ConfinedResolveOptions options = ConfinedResolveOptions.None)
    {
        CapResult<ResolvedParent> result = Resolver.ResolveParent(root, Parse(path), options);
        Assert.True(result.IsSuccess, $"'{path}': {result.Error.FailureDescription}");
        return result.Value!;
    }

    private static void AssertFails(
        CapErrorCategory expected,
        SafeDirHandle root,
        string path,
        ConfinedResolveOptions options = ConfinedResolveOptions.None)
    {
        CapResult<ResolvedParent> result = Resolver.ResolveParent(root, Parse(path), options);

        Assert.False(result.IsSuccess, $"'{path}' resolved when it should not have.");
        Assert.Equal(expected, result.Error.Category);
    }

    private static void AssertIs(FakePlatformOps ops, MemoryNode expected, SafeDirHandle handle)
    {
        Assert.True(ops.StatHandle(handle, out CapNodeInfo info).IsSuccess);
        Assert.True(info.IsSameNodeAs(expected.Info), "The resolution reached a different object.");
    }
}
