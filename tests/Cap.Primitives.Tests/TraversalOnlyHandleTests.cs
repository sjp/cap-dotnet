using System.Runtime.Versioning;
using Cap.Primitives.Interop;

namespace Cap.Primitives.Tests;

/// <summary>
/// Directory handles opened for traversal alone.
/// </summary>
/// <remarks>
/// <para>
/// A directory has two quite separate uses. It can be a thing to read — a list of names
/// somebody wants back — or it can be a place, the point other names are resolved from. The
/// second needs none of the authority the first does, and the operating systems agree: their
/// own path resolution requires permission to traverse a directory and never requires
/// permission to list it.
/// </para>
/// <para>
/// Two things follow, and both are tested here. A handle held only in order to reach what is
/// beneath it should not carry the authority to read it, because authority nobody needs is
/// authority that can only ever be misused. And a directory that grants traversal without
/// granting listing has to remain reachable, because the kernel can walk through one and a
/// resolver that insisted on opening it readably could not — the same tree resolving on one
/// backend and failing on another, for a reason having nothing to do with containment.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class TraversalOnlyHandleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-anchor-").FullName;

    public void Dispose()
    {
        // A directory left without read permission cannot be walked for deletion, so the
        // permissions the tests took away are given back before anything tries.
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in Directory.EnumerateDirectories(_root))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private static IPlatformOps Ops => PlatformOps.Current;

    /// <summary>
    /// A traversal-only handle is a position in the tree: names resolve against it, it can
    /// say what it is, and it can be copied.
    /// </summary>
    [Fact]
    public void A_traversal_only_handle_can_still_resolve_names_beneath_it()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child", "grandchild"));

        using SafeDirHandle anchor = OpenRoot(CapAccess.None);

        CapResult<SafeDirHandle> child = Ops.OpenChildDirectory(anchor, "child", CapAccess.Read);
        Assert.True(child.IsSuccess, child.Error.FailureDescription);
        child.Value.Dispose();

        Assert.True(Ops.StatChild(anchor, "child", out CapNodeInfo byName).IsSuccess);
        Assert.True(Ops.StatHandle(anchor, out CapNodeInfo self).IsSuccess);
        Assert.False(byName.IsSameNodeAs(self));
    }

    /// <summary>
    /// Copying a handle reproduces the authority it had rather than asking for whatever the
    /// directory would allow.
    /// </summary>
    /// <remarks>
    /// The distinction matters because a copy is made by code that was handed a handle, not
    /// by the code that decided how much authority to take. If copying re-derived the answer
    /// from the directory's permissions, a handle deliberately opened with no read access
    /// would become readable the first time anyone duplicated it, and the narrowing would
    /// last exactly as long as nobody thought to copy it.
    /// </remarks>
    [Fact]
    public void A_copy_of_a_traversal_only_handle_is_traversal_only()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        using SafeDirHandle anchor = OpenRoot(CapAccess.None);
        CapResult<SafeDirHandle> copyResult = Ops.DuplicateDirectory(anchor);
        Assert.True(copyResult.IsSuccess, copyResult.Error.FailureDescription);

        using SafeDirHandle copy = copyResult.Value;
        Assert.Equal(CapAccess.None, copy.Access);

        // Still a position in the tree, which is the whole of what it was for.
        CapResult<SafeDirHandle> child = Ops.OpenChildDirectory(copy, "child", CapAccess.Read);
        Assert.True(child.IsSuccess, child.Error.FailureDescription);
        child.Value.Dispose();
    }

    /// <summary>
    /// No directory open can mean "for writing", so asking is a mistake and is reported as
    /// one rather than being rounded to the nearest thing that exists.
    /// </summary>
    [Fact]
    public void A_directory_cannot_be_opened_for_writing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        CapResult<SafeDirHandle> ambient = Ops.OpenAmbientDirectory(_root, CapAccess.Write);
        Assert.False(ambient.IsSuccess);
        Assert.Equal(CapErrorCategory.InvalidArgument, ambient.Error.Category);

        using SafeDirHandle root = OpenRoot(CapAccess.Read);

        CapResult<SafeDirHandle> child = Ops.OpenChildDirectory(root, "child", CapAccess.Write);
        Assert.False(child.IsSuccess);
        Assert.Equal(CapErrorCategory.InvalidArgument, child.Error.Category);

        if (Ops.Capabilities.SupportsConfinedOpen)
        {
            CapResult<SafeDirHandle> confined = Ops.OpenConfinedDirectory(
                root, "child", CapAccess.Write, ConfinedResolveOptions.None);
            Assert.False(confined.IsSuccess);
            Assert.Equal(CapErrorCategory.InvalidArgument, confined.Error.Category);
        }
    }

    /// <summary>
    /// A traversal-only open refuses a symbolic link like every other open here, rather than
    /// handing back a handle to the link itself.
    /// </summary>
    /// <remarks>
    /// This is the sharp edge of the flag that makes these handles possible. Asked not to
    /// follow links, and not also told the name must be a directory, it stops at the link and
    /// succeeds — returning something that is not the directory the caller named, to code
    /// whose next move is to resolve further names against it. Demanding a directory is what
    /// turns that back into the refusal every other open gives, and this is the test that
    /// says so out loud.
    /// </remarks>
    [Fact]
    public void A_symbolic_link_is_refused_by_a_traversal_only_open_too()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        if (!TryCreateDirectoryLink(Path.Combine(_root, "link"), "real"))
        {
            Assert.Skip("This host will not create symbolic links for an unprivileged process.");
        }

        using SafeDirHandle root = OpenRoot(CapAccess.Read);

        CapResult<SafeDirHandle> result = Ops.OpenChildDirectory(root, "link", CapAccess.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.SymbolicLink, result.Error.Category);
    }

    /// <summary>
    /// A directory that may be walked through but not listed can still be anchored, and
    /// still cannot be read.
    /// </summary>
    /// <remarks>
    /// The permission pattern is ordinary — it is how a shared parent directory is kept from
    /// disclosing who has an account beneath it — and it is the case that makes the narrower
    /// open a necessity rather than a nicety. Without it, resolution through such a directory
    /// fails on a backend that opens each step readably and succeeds on one that lets the
    /// kernel do the walk, which is the same path giving two answers for reasons a caller
    /// cannot see.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_directory_that_allows_traversal_without_reading_can_still_be_anchored()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string gate = Path.Combine(_root, "gate");
        Directory.CreateDirectory(Path.Combine(gate, "inner"));
        File.SetUnixFileMode(gate, UnixFileMode.UserExecute);

        using SafeDirHandle root = OpenRoot(CapAccess.Read);

        CapResult<SafeDirHandle> readable = Ops.OpenChildDirectory(root, "gate", CapAccess.Read);
        Assert.False(readable.IsSuccess);
        Assert.Equal(CapErrorCategory.PermissionDenied, readable.Error.Category);

        CapResult<SafeDirHandle> anchored = Ops.OpenChildDirectory(root, "gate", CapAccess.None);
        Assert.True(anchored.IsSuccess, anchored.Error.FailureDescription);

        using SafeDirHandle anchor = anchored.Value;
        CapResult<SafeDirHandle> inner = Ops.OpenChildDirectory(anchor, "inner", CapAccess.Read);
        Assert.True(inner.IsSuccess, inner.Error.FailureDescription);
        inner.Value.Dispose();
    }

    /// <summary>
    /// The kernel's own confined walk passes through such a directory as well, needing
    /// nothing from the components it merely traverses.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_confined_open_walks_through_a_directory_it_could_not_have_read()
    {
        if (!OperatingSystem.IsLinux() || !Ops.Capabilities.SupportsConfinedOpen)
        {
            return;
        }

        string gate = Path.Combine(_root, "gate");
        Directory.CreateDirectory(Path.Combine(gate, "inner"));
        File.SetUnixFileMode(gate, UnixFileMode.UserExecute);

        using SafeDirHandle root = OpenRoot(CapAccess.Read);
        CapResult<SafeDirHandle> result = Ops.OpenConfinedDirectory(
            root, "gate/inner", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        result.Value.Dispose();
    }

    private SafeDirHandle OpenRoot(CapAccess access)
    {
        CapResult<SafeDirHandle> result = Ops.OpenAmbientDirectory(_root, access);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        Assert.Equal(access, result.Value.Access);
        return result.Value;
    }

    /// <summary>
    /// Creates a directory symbolic link, or reports that this host will not allow one.
    /// </summary>
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
