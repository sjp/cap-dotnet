using Cap.Escape.Tests;
using Cap.Primitives;
using Cap.Std;
using WasiHost.Preview1;

namespace WasiHost.Tests;

/// <summary>
/// Committing a directory descriptor's entries, as a guest does after renaming a file into
/// place.
/// </summary>
/// <remarks>
/// <para>
/// A directory has no data apart from its entries, so both sync calls commit them. The answer
/// depends only on whether the platform can commit a directory at all. Windows cannot, and the
/// guest is told so with <c>ENOTSUP</c> rather than a success it would build a durability
/// promise on.
/// </para>
/// <para>
/// The root is opened by path, through the host, so the class runs in the corpus's group: a
/// corpus case replaces the host for its duration, and a root opened meanwhile would resolve
/// through whichever backend that case had installed.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class WasiDirectorySyncTests
{
    [Theory]
    [InlineData("fd_sync")]
    [InlineData("fd_datasync")]
    public void A_directory_descriptor_commits_its_entries_where_the_platform_can(string call)
    {
        using ScratchTree scratch = new();
        using Dir root = Dir.Open(scratch.HostPath, AmbientAuthority.Acquire());
        using TrampolineGuest guest = new(root);

        Errno expected = OperatingSystem.IsWindows() ? Errno.NotSup : Errno.Success;

        Assert.Equal(expected, guest.Call(call, TrampolineGuest.Root));
    }

    /// <summary>A descriptor that was never opened is reported as such, not as unsupported.</summary>
    [Fact]
    public void Syncing_an_unknown_descriptor_is_a_bad_descriptor()
    {
        using ScratchTree scratch = new();
        using Dir root = Dir.Open(scratch.HostPath, AmbientAuthority.Acquire());
        using TrampolineGuest guest = new(root);

        Assert.Equal(Errno.BadF, guest.Call("fd_sync", 99));
    }
}
