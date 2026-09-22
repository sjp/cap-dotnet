using Cap.Primitives.Interop;
using Cap.Tests;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The walk against a real kernel and a real tree.
/// </summary>
/// <remarks>
/// <para>
/// The simulated filesystem can express attacks no kernel will let a test provoke, which is
/// why the detailed behaviour is asserted there. What it cannot do is notice that the walk
/// is asking the operating system for something the operating system does not mean the same
/// way — a flag that is ignored, an error code that arrives as something else, a link that
/// the open follows after all. That is what this is for.
/// </para>
/// <para>
/// It runs whether or not the kernel offers a confined open, because the walk does not use
/// one. On a host that has it these are the cases a caller reaches when a sandbox filter has
/// taken it away, and a suite that only exercised them on old kernels would be testing the
/// configuration nobody runs.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class PortableWalkOnDiskTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-walk-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A path of several real components reaches the file at the end of it.</summary>
    [Fact]
    public void A_real_tree_is_walked_component_by_component()
    {
        if (!SupportsSymbolicLinks)
        {
            return;
        }

        Build();

        using SafeDirHandle root = OpenSandbox();
        CapResult<SafeFileHandle> file = PortableResolver.OpenFile(
            root, Parse("inside/deeper/marker"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);

        Assert.True(file.IsSuccess, file.Error.FailureDescription);
        file.Value!.Dispose();
    }

    /// <summary>A link the kernel would have followed out of the tree is refused.</summary>
    [Fact]
    public void A_link_out_of_the_tree_is_refused_by_the_walk()
    {
        if (!SupportsSymbolicLinks)
        {
            return;
        }

        Build();

        using SafeDirHandle root = OpenSandbox();

        AssertFails(CapErrorCategory.Escaped, root, "escape");
        AssertFails(CapErrorCategory.Escaped, root, "absolute");
        AssertFails(CapErrorCategory.Escaped, root, "..");
        AssertFails(CapErrorCategory.Escaped, root, "inside/../../..");
    }

    /// <summary>
    /// A link that stays inside is followed, and the walk continues through it.
    /// </summary>
    [Fact]
    public void A_link_that_stays_inside_is_followed_by_the_walk()
    {
        if (!SupportsSymbolicLinks)
        {
            return;
        }

        Build();

        using SafeDirHandle root = OpenSandbox();
        CapResult<SafeFileHandle> file = PortableResolver.OpenFile(
            root, Parse("shortcut/marker"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);

        Assert.True(file.IsSuccess, file.Error.FailureDescription);
        file.Value!.Dispose();

        // The same again, but with the link one level down, so that reading it and opening
        // through it both happen against a handle the walk opened for traversal only. On
        // Linux that is a descriptor with no data access, and whether the calls a walk makes
        // accept one is a property of the kernel rather than something this code can decide.
        CapResult<SafeFileHandle> nested = PortableResolver.OpenFile(
            root, Parse("inside/nested/marker"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);

        Assert.True(nested.IsSuccess, nested.Error.FailureDescription);
        nested.Value!.Dispose();
    }

    /// <summary>A link that points at itself is stopped by the budget, not by the stack.</summary>
    [Fact]
    public void A_real_link_cycle_is_stopped()
    {
        if (!SupportsSymbolicLinks)
        {
            return;
        }

        Build();

        using SafeDirHandle root = OpenSandbox();
        AssertFails(CapErrorCategory.SymbolicLinkLoop, root, "cycle");
    }

    /// <summary>
    /// Repeatedly failing to resolve leaves the process holding no more descriptors than it
    /// started with.
    /// </summary>
    /// <remarks>
    /// Counted from the kernel's own list rather than from anything this library keeps, so
    /// that a leak the library is unaware of — a handle opened by a platform call and dropped
    /// on an error path before it was ever wrapped — still shows up. The failing paths are the
    /// ones worth repeating: they are the ones a hostile caller controls, and each of them
    /// leaves the walk partway down a tree with handles to unwind.
    /// </remarks>
    [Fact]
    public void Failing_to_resolve_leaks_no_descriptors()
    {
        if (!OperatingSystem.IsLinux() || !SupportsSymbolicLinks)
        {
            return;
        }

        Build();

        string[] failures =
        [
            "escape",
            "absolute",
            "cycle",
            "inside/../../..",
            "inside/deeper/missing/further",
            "inside/deeper/marker/beyond",
            "..",
        ];

        using SafeDirHandle root = OpenSandbox();

        // One warm-up round first: the pools and buffers a walk touches are allocated on
        // first use, and counting those as a leak would make this fail once and pass ever
        // after, which is the least useful way for it to behave.
        foreach (string path in failures)
        {
            _ = PortableResolver.OpenDirectory(root, Parse(path), CapAccess.Read, ConfinedResolveOptions.None);
        }

        int before = OpenDescriptorCount();

        for (int round = 0; round < 50; round++)
        {
            foreach (string path in failures)
            {
                CapResult<SafeDirHandle> result = PortableResolver.OpenDirectory(
                    root, Parse(path), CapAccess.Read, ConfinedResolveOptions.None);

                Assert.False(result.IsSuccess, $"'{path}' resolved when it should not have.");
            }
        }

        int after = OpenDescriptorCount();
        Assert.True(
            after <= before,
            $"The walk left {after - before} extra descriptors open after {failures.Length * 50} failed " +
            "resolutions. A descriptor leak on a path a caller controls is a way to exhaust the " +
            "process's descriptors from outside, and the failures it causes appear somewhere else " +
            "entirely.");
    }

    /// <summary>
    /// Every directory beneath the sandbox is entered without asking for the right to read
    /// it, which is the difference between passing through a directory and listing it.
    /// </summary>
    /// <remarks>
    /// A directory that grants traversal but not listing — the usual way a shared parent is
    /// kept from disclosing what is beneath it — must still be walkable. Linux is the
    /// platform that can express the distinction, so it is the one that can prove the walk
    /// asks for the smaller of the two.
    /// </remarks>
    [Fact]
    public void A_directory_that_cannot_be_listed_can_still_be_walked_through()
    {
        if (!OperatingSystem.IsLinux() || TestEnvironment.CanBypassFilePermissions)
        {
            return;
        }

        Build();

        string opaque = Path.Join(_root, "sandbox", "opaque");
        Directory.CreateDirectory(Path.Join(opaque, "beyond"));
        File.WriteAllText(Path.Join(opaque, "beyond", "marker"), "x");
        File.SetUnixFileMode(opaque, UnixFileMode.UserExecute);

        try
        {
            using SafeDirHandle root = OpenSandbox();
            CapResult<SafeFileHandle> file = PortableResolver.OpenFile(
                root, Parse("opaque/beyond/marker"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);

            Assert.True(file.IsSuccess, file.Error.FailureDescription);
            file.Value!.Dispose();
        }
        finally
        {
            File.SetUnixFileMode(opaque, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Builds a tree with something of each kind the walk has to reason about.
    /// </summary>
    /// <remarks>
    /// The sandbox is a subdirectory rather than the temporary directory itself, so that
    /// there is somewhere above it for an escape to aim at. A test whose root has nothing
    /// outside it cannot tell a refusal from a miss.
    /// </remarks>
    private void Build()
    {
        string sandbox = Path.Join(_root, "sandbox");
        Directory.CreateDirectory(Path.Join(sandbox, "inside", "deeper"));
        File.WriteAllText(Path.Join(sandbox, "inside", "deeper", "marker"), "x");
        Directory.CreateDirectory(Path.Join(_root, "outside"));
        File.WriteAllText(Path.Join(_root, "outside", "secret"), "x");

        Directory.CreateSymbolicLink(Path.Join(sandbox, "escape"), "../outside");
        Directory.CreateSymbolicLink(Path.Join(sandbox, "absolute"), Path.Join(_root, "outside"));
        Directory.CreateSymbolicLink(Path.Join(sandbox, "shortcut"), "inside/deeper");
        Directory.CreateSymbolicLink(Path.Join(sandbox, "inside", "nested"), "deeper");
        Directory.CreateSymbolicLink(Path.Join(sandbox, "cycle"), "cycle");
    }

    private SafeDirHandle OpenSandbox()
    {
        CapResult<SafeDirHandle> root =
            PlatformOps.Current.OpenAmbientDirectory(Path.Join(_root, "sandbox"), CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);
        return root.Value!;
    }

    private static void AssertFails(CapErrorCategory expected, SafeDirHandle root, string path)
    {
        CapResult<SafeDirHandle> result =
            PortableResolver.OpenDirectory(root, Parse(path), CapAccess.Read, ConfinedResolveOptions.None);

        Assert.False(result.IsSuccess, $"'{path}' resolved when it should not have.");
        Assert.Equal(expected, result.Error.Category);
    }

    private static CapPath Parse(string raw)
    {
        Assert.True(
            CapPath.TryParse(raw, CapPath.HostSyntax, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError error),
            $"'{raw}' did not parse: {error}");
        return path;
    }

    /// <summary>
    /// How many descriptors this process holds, read from the kernel's own list.
    /// </summary>
    private static int OpenDescriptorCount() => Directory.GetFileSystemEntries("/proc/self/fd").Length;

    /// <summary>
    /// Whether symbolic links can be created here. On Windows that needs either an elevated
    /// token or developer mode, and a host with neither cannot build the tree these need.
    /// </summary>
    private static bool SupportsSymbolicLinks => !OperatingSystem.IsWindows();
}
