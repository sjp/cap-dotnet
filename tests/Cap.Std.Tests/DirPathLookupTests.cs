using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Asking a handle where it is, which is a diagnostic and not a capability.
/// </summary>
/// <remarks>
/// <para>
/// The answer is deliberately hard to misuse: it costs an ambient-authority token to get,
/// because the path it produces names the directory from a filesystem root the handle
/// confers no authority over, and it is documented as a snapshot of one of possibly several
/// names rather than as an address. Nothing in the library resolves anything against it.
/// </para>
/// <para>
/// It also has to be allowed to fail. Some hosts have no mechanism to answer — a Linux
/// container without the process filesystem mounted is the usual one — and a log line is
/// not worth an exception, so the absence of an answer is an ordinary false rather than a
/// failure a caller has to handle separately.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirPathLookupTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>Where an answer is available, it names the directory that was opened.</summary>
    /// <remarks>
    /// Compared by its last component alone. The full string is not predictable from the
    /// path the directory was created by: temporary directories sit behind symbolic links on
    /// some systems, and the extended-length prefix appears on others, and a test that
    /// insisted on an exact match would be asserting something the contract does not promise.
    /// </remarks>
    [Fact]
    public void A_handle_can_say_what_directory_it_was_opened_on()
    {
        string nested = Path.Combine(_tree.HostPath, "somewhere");
        HostDirectory.CreateDirectory(nested);

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir child = root.OpenDir("somewhere");

        if (!child.TryGetPath(AmbientAuthority.Acquire(), out string? path))
        {
            Assert.Skip("This host has no way to ask an open handle what path it is reachable by.");
        }

        Assert.EndsWith("somewhere", path.TrimEnd('/', '\\'), StringComparison.Ordinal);
    }

    /// <summary>A copy is the same directory, so it answers with the same path.</summary>
    [Fact]
    public void A_copy_answers_the_same_way_as_the_handle_it_came_from()
    {
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir copy = root.Clone();

        if (!root.TryGetPath(AmbientAuthority.Acquire(), out string? original))
        {
            Assert.Skip("This host has no way to ask an open handle what path it is reachable by.");
        }

        Assert.True(copy.TryGetPath(AmbientAuthority.Acquire(), out string? duplicate));
        Assert.Equal(original, duplicate);
    }

    /// <summary>
    /// The answer describes where the directory is now, not where it was when it was opened.
    /// </summary>
    /// <remarks>
    /// The point of the test is the point of the warning on the method. A handle survives a
    /// rename because it refers to the object rather than to the name; the path does not,
    /// and a caller that stored one and reopened it later would be opening whatever holds
    /// that name by then.
    /// </remarks>
    [Fact]
    public void The_answer_follows_a_rename_rather_than_recording_the_name_it_was_opened_by()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "before"));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir child = root.OpenDir("before");

        if (!child.TryGetPath(AmbientAuthority.Acquire(), out string? opened))
        {
            Assert.Skip("This host has no way to ask an open handle what path it is reachable by.");
        }

        Assert.EndsWith("before", opened.TrimEnd('/', '\\'), StringComparison.Ordinal);

        HostDirectory.Move(Path.Combine(_tree.HostPath, "before"), Path.Combine(_tree.HostPath, "after"));

        Assert.True(child.TryGetPath(AmbientAuthority.Acquire(), out string? renamed));
        Assert.EndsWith("after", renamed.TrimEnd('/', '\\'), StringComparison.Ordinal);
    }
}
