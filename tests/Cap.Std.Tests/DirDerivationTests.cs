using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Tests;

/// <summary>
/// Deriving one handle from another, and what happens to a path that asks for more.
/// </summary>
/// <remarks>
/// <para>
/// Derivation is the whole mechanism: a handle can reach what is beneath it and nothing
/// else, so the set of things a component can touch is fixed by the handles it was given.
/// The cases worth testing are therefore the refusals. A path that climbs out, one that was
/// absolute to begin with, and one naming a location the platform resolves from ambient
/// process state all have to be turned away, and turned away as what they are rather than as
/// a file that happened not to exist.
/// </para>
/// <para>
/// The distinction between refusal kinds is not decoration. An application that wants to
/// know when something handed it a path that tried to leave — and that is a thing worth
/// alerting on — needs to tell it apart from a typo, and needs to do so without matching on
/// message text.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirDerivationTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    /// <summary>A path of several names resolves to the directory it names.</summary>
    [Fact]
    public void A_nested_path_opens_the_directory_it_names()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b", "c"));

        using Dir root = OpenRoot();
        using Dir nested = root.OpenDir("a/b/c");

        Assert.True(PlatformOps.Host.StatHandle((SafeDirHandle)nested.UnsafeGetHandle(), out CapNodeInfo opened).IsSuccess);
        Assert.Equal(CapNodeType.Directory, opened.Type);
    }

    /// <summary>Repeated separators and <c>.</c> are dropped, as every kernel drops them.</summary>
    [Fact]
    public void Redundant_separators_and_dot_components_are_ignored()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b"));

        using Dir root = OpenRoot();
        using Dir viaTidyPath = root.OpenDir("a/b");
        using Dir viaUntidyPath = root.OpenDir("a//./b");

        Assert.True(SameDirectory(viaTidyPath, viaUntidyPath));
    }

    /// <summary>A missing directory is an ordinary missing thing.</summary>
    [Fact]
    public void A_missing_directory_is_reported_as_missing()
    {
        using Dir root = OpenRoot();

        Assert.Throws<DirectoryNotFoundException>(() => root.OpenDir("absent"));
        Assert.False(root.TryOpenDir("absent", out Dir? dir));
        Assert.Null(dir);
    }

    /// <summary>Opening a file as a directory fails, and not as an escape.</summary>
    [Fact]
    public void A_file_is_not_a_directory()
    {
        File.WriteAllText(Path.Combine(_tree.HostPath, "file.txt"), "contents");

        using Dir root = OpenRoot();

        Exception thrown = Assert.ThrowsAny<IOException>(() => root.OpenDir("file.txt"));
        Assert.IsNotType<SandboxEscapeException>(thrown);
    }

    /// <summary>A path that climbs above the root is refused as the escape it is.</summary>
    /// <remarks>
    /// Refused while it is still a string, before anything is opened. The resolvers beneath
    /// can take an upward step safely — they step back through a handle they already hold —
    /// but a path arriving from a caller is not walked upwards at all, because a caller
    /// asking to go above the root has been handed something that tries to leave and the
    /// useful answer is to say so.
    /// </remarks>
    [Theory]
    [InlineData("..")]
    [InlineData("../sibling")]
    [InlineData("a/../../sibling")]
    public void A_path_that_climbs_out_is_refused(string path)
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a"));

        using Dir root = OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(path));
        Assert.False(root.TryOpenDir(path, out _));
    }

    /// <summary>An absolute path names a place the handle confers no authority over.</summary>
    [Fact]
    public void An_absolute_path_is_refused()
    {
        using Dir root = OpenRoot();

        string absolute = OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc";
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(absolute));
    }

    /// <summary>
    /// So does one the platform would resolve from process-wide state rather than from the
    /// handle.
    /// </summary>
    [Fact]
    public void A_path_resolved_from_ambient_process_state_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These spellings are ambient only under Windows path rules.");
        }

        using Dir root = OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(@"\file"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(@"C:file"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(@"\\server\share"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir(@"\\?\C:\Windows"));
    }

    /// <summary>A name the system routes to a device is not a name beneath the handle.</summary>
    [Fact]
    public void A_reserved_device_name_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Device names are reserved only under Windows path rules.");
        }

        using Dir root = OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.OpenDir("CON"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir("sub/NUL.txt"));
    }

    /// <summary>A name that is malformed rather than out of bounds is an argument error.</summary>
    /// <remarks>
    /// The line is what the path was asking for, not how badly it was written. A name
    /// carrying a character that cannot reach the kernel intact is a mistake in the calling
    /// code; it is not an attempt to leave, and reporting it as one would put noise into the
    /// log an application keeps of the attempts that are.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("with\0null")]
    public void A_malformed_name_is_an_argument_error(string path)
    {
        using Dir root = OpenRoot();

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => root.OpenDir(path));
        Assert.Equal("path", thrown.ParamName);
    }

    /// <summary>A null path is a null argument, in both forms.</summary>
    [Fact]
    public void A_null_path_is_refused_by_both_forms()
    {
        using Dir root = OpenRoot();

        Assert.Throws<ArgumentNullException>(() => root.OpenDir(null!));
        Assert.Throws<ArgumentNullException>(() => root.TryOpenDir(null!, out _));
    }

    /// <summary>
    /// The policy a root is opened under is copied into everything derived from it.
    /// </summary>
    /// <remarks>
    /// Inheritance is the whole of what makes it a policy. If a derived handle could be
    /// given a looser one, a component handed a handle could widen what it was allowed to
    /// resolve simply by deriving from it, and the restriction would last exactly until
    /// somebody thought to try.
    /// </remarks>
    [Fact]
    public void A_derived_handle_resolves_under_the_policy_its_root_was_opened_with()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b"));

        CapError error = Dir.OpenRootCore(
            PlatformOps.Host, _tree.HostPath, AmbientAuthority.Acquire(), ConfinedResolveOptions.RefuseSymlinks, out Dir? root);
        Assert.True(error.IsSuccess, error.FailureDescription);

        using Dir opened = root!;
        {
            using Dir child = opened.OpenDir("a");
            using Dir grandchild = child.OpenDir("b");

            using Dir copy = child.Clone();

            Assert.Equal(ConfinedResolveOptions.RefuseSymlinks, child.Options);
            Assert.Equal(ConfinedResolveOptions.RefuseSymlinks, grandchild.Options);
            Assert.Equal(ConfinedResolveOptions.RefuseSymlinks, copy.Options);
        }
    }

    /// <summary>Whether two handles refer to the same directory, by identity rather than by name.</summary>
    private static bool SameDirectory(Dir left, Dir right)
    {
        IPlatformOps ops = PlatformOps.Host;
        Assert.True(ops.StatHandle((SafeDirHandle)left.UnsafeGetHandle(), out CapNodeInfo first).IsSuccess);
        Assert.True(ops.StatHandle((SafeDirHandle)right.UnsafeGetHandle(), out CapNodeInfo second).IsSuccess);
        return first.IsSameNodeAs(second);
    }
}
