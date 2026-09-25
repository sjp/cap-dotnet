using System.Runtime.InteropServices;
using Cap.Primitives.Interop;
using Cap.Tests.Fakes;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The walk's open of whatever the last name holds, driven against a filesystem that can be
/// changed between any two of its steps.
/// </summary>
/// <remarks>
/// The property this open exists for is that the last name is looked up once, so the kind it
/// reports is the kind of the object it opened. Against a real kernel a rename between two
/// lookups is a window a few instructions wide; here a hook runs before every lookup, so a
/// second lookup would be seen every time and a swap made after the first is made at exactly
/// the instant that would matter.
/// </remarks>
public sealed class PortableWalkNodeTests
{
    /// <summary>A file is opened as a file, and a directory as a directory.</summary>
    [Fact]
    public void Each_kind_is_opened_as_what_it_is()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode file = fs.AddFile("sandbox/a/data");
        FakeNode directory = fs.AddDirectory("sandbox/a/dir");

        Run(fs, (ops, root) =>
        {
            using (OpenedNode opened = OpenNode(root, "a/data"))
            {
                Assert.Null(opened.Directory);
                AssertIs(ops, file, opened.File!);
            }

            using (OpenedNode opened = OpenNode(root, "a/dir"))
            {
                Assert.Null(opened.File);
                AssertIs(ops, directory, opened.Directory!);
                Assert.Equal(CapAccess.Read, opened.Directory!.Access);
            }
        });
    }

    /// <summary>
    /// The last name is looked up once, so a name swapped for the other kind after that lookup
    /// changes nothing about what is opened or what it is reported to be.
    /// </summary>
    [Fact]
    public void The_last_name_is_looked_up_once_and_a_later_swap_changes_nothing()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode file = fs.AddFile("sandbox/entry");

        int lookups = 0;
        fs.BeforeLookup = (_, name) =>
        {
            if (name != "entry")
            {
                return;
            }

            // A second lookup of the name would find a directory in its place.
            if (++lookups == 2)
            {
                fs.Remove("sandbox/entry");
                _ = fs.AddDirectory("sandbox/entry");
            }
        };

        Run(fs, (ops, root) =>
        {
            using OpenedNode opened = OpenNode(root, "entry");

            Assert.Equal(1, lookups);
            AssertIs(ops, file, opened.File!);
        });
    }

    /// <summary>
    /// A link at the last name is followed to what it leads to, and refused without being
    /// read when the request asks for it not to be.
    /// </summary>
    [Fact]
    public void A_final_link_is_followed_unless_the_request_refuses_it()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode directory = fs.AddDirectory("sandbox/dir");
        FakeNode file = fs.AddFile("sandbox/data");
        _ = fs.AddSymbolicLink("sandbox/to-dir", "dir");
        _ = fs.AddSymbolicLink("sandbox/to-file", "data");

        Run(fs, (ops, root) =>
        {
            using (OpenedNode opened = OpenNode(root, "to-dir"))
            {
                AssertIs(ops, directory, opened.Directory!);
            }

            using (OpenedNode opened = OpenNode(root, "to-file"))
            {
                AssertIs(ops, file, opened.File!);
            }

            AssertFails(CapErrorCategory.SymbolicLinkLoop, root, "to-dir", noFollow: true);
            AssertFails(CapErrorCategory.SymbolicLinkLoop, root, "to-file", noFollow: true);
            AssertFails(
                CapErrorCategory.SymbolicLinkLoop, root, "to-file", options: ConfinedResolveOptions.RefuseSymlinks);
        });
    }

    /// <summary>
    /// A path spelled as a directory opens only a directory, and a step up from a directory
    /// opens the one the walk climbed back to.
    /// </summary>
    [Fact]
    public void A_path_spelled_as_a_directory_opens_only_a_directory()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddFile("sandbox/data");
        FakeNode directory = fs.AddDirectory("sandbox/dir");
        _ = fs.AddDirectory("sandbox/dir/inner");

        Run(fs, (ops, root) =>
        {
            AssertFails(CapErrorCategory.NotADirectory, root, "data/");

            using (OpenedNode opened = OpenNode(root, "dir/"))
            {
                AssertIs(ops, directory, opened.Directory!);
            }

            using (OpenedNode opened = OpenNode(root, "dir/inner/.."))
            {
                AssertIs(ops, directory, opened.Directory!);
            }
        });
    }

    /// <summary>A path that climbs above the root is refused as an escape.</summary>
    [Fact]
    public void Climbing_above_the_root_is_an_escape()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddFile("outside");
        _ = fs.AddSymbolicLink("sandbox/out", "../outside");

        Run(fs, (_, root) =>
        {
            AssertFails(CapErrorCategory.Escaped, root, "../outside");
            AssertFails(CapErrorCategory.Escaped, root, "out");
        });
    }

    /// <summary>
    /// A request that could only be met by a file — one that writes, creates, empties or
    /// appends — is refused before anything is looked up.
    /// </summary>
    [Fact]
    public void A_request_only_a_file_could_satisfy_is_refused()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddFile("sandbox/data");

        int lookups = 0;
        fs.BeforeLookup = (_, _) => lookups++;

        FileOpenRequest[] requests =
        [
            FileOpenRequest.Existing(FileAccess.ReadWrite),
            new(FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read, FileOptions.None, 0),
            new(FileMode.Truncate, FileAccess.Write, FileShare.Read, FileOptions.None, 0),
            new(FileMode.Open, FileAccess.Write, FileShare.Read, FileOptions.None, 0, append: true),
        ];

        Run(fs, (_, root) =>
        {
            foreach (FileOpenRequest request in requests)
            {
                CapResult<OpenedNode> result = Resolver.OpenNode(root, Parse("data"), in request, ConfinedResolveOptions.None);
                Assert.False(result.IsSuccess);
                Assert.Equal(CapErrorCategory.InvalidArgument, result.Error.Category);
            }
        });

        Assert.Equal(0, lookups);
    }

    private static FakeFileSystem Sandbox()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        return fs;
    }

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

    private static OpenedNode OpenNode(SafeDirHandle root, string path)
    {
        CapResult<OpenedNode> result = PortableResolver.OpenNode(
            root, Parse(path), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        Assert.True(result.IsSuccess, $"'{path}': {result.Error.FailureDescription}");
        return result.Value!;
    }

    private static void AssertFails(
        CapErrorCategory expected,
        SafeDirHandle root,
        string path,
        bool noFollow = false,
        ConfinedResolveOptions options = ConfinedResolveOptions.None)
    {
        FileOpenRequest request = new(
            FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0, noFollow: noFollow);
        CapResult<OpenedNode> result = PortableResolver.OpenNode(root, Parse(path), in request, options);
        if (result.IsSuccess)
        {
            result.Value!.Dispose();
        }

        Assert.False(result.IsSuccess, $"'{path}' opened.");
        Assert.Equal(expected, result.Error.Category);
    }

    private static void AssertIs(FakePlatformOps ops, FakeNode expected, SafeHandle handle)
    {
        Assert.True(ops.DescribeHandle(handle, out CapNodeStat stat).IsSuccess);
        Assert.Equal(expected.NodeId, stat.NodeId);
    }
}
