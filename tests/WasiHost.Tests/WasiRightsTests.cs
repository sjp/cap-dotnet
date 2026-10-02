using Cap.Escape.Tests;
using Cap.Primitives;
using Cap.Std;
using WasiHost.Preview1;

namespace WasiHost.Tests;

/// <summary>
/// Rights narrowing beneath a directory a guest opened with fewer inheriting rights than its
/// preopen.
/// </summary>
/// <remarks>
/// <para>
/// <c>path_open</c> may hand out only rights the directory it resolves against lets its
/// children hold, so a guest that opens a directory with narrow inheriting rights has built a
/// sandbox of its own inside the preopen. The preopen itself lets its children hold
/// everything, so the narrowing shows only one directory down.
/// </para>
/// <para>
/// The root is opened by path, through the host, so the class runs in the corpus's group, for
/// the reason <see cref="WasiDirectorySyncTests"/> gives.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class WasiRightsTests
{
    private const Rights Narrow = Rights.FdRead | Rights.FdSeek;

    [Fact]
    public void A_file_beneath_a_narrowed_directory_cannot_be_opened_with_wider_rights()
    {
        using ScratchTree scratch = new();
        using Dir root = Dir.Open(scratch.HostPath, AmbientAuthority.Acquire());
        root.CreateDir("sub");
        root.WriteAllBytes("sub/file", [1, 2, 3]);
        using TrampolineGuest guest = new(root);
        uint sub = OpenSubdirectory(guest, Narrow);

        Assert.Equal(Errno.NotCapable, Open(guest, sub, "file", Rights.FdWrite, Rights.None));
        Assert.Equal(Errno.NotCapable, Open(guest, sub, "file", Rights.FdRead, Rights.FdWrite));
    }

    [Fact]
    public void A_file_beneath_a_narrowed_directory_opens_with_rights_it_allows()
    {
        using ScratchTree scratch = new();
        using Dir root = Dir.Open(scratch.HostPath, AmbientAuthority.Acquire());
        root.CreateDir("sub");
        root.WriteAllBytes("sub/file", [1, 2, 3]);
        using TrampolineGuest guest = new(root);
        uint sub = OpenSubdirectory(guest, Narrow);

        Assert.Equal(Errno.Success, Open(guest, sub, "file", Rights.FdRead, Rights.None));
    }

    /// <summary>Naming the directory itself takes the same check as naming an entry.</summary>
    [Fact]
    public void A_narrowed_directory_cannot_be_reopened_with_wider_rights()
    {
        using ScratchTree scratch = new();
        using Dir root = Dir.Open(scratch.HostPath, AmbientAuthority.Acquire());
        root.CreateDir("sub");
        using TrampolineGuest guest = new(root);
        uint sub = OpenSubdirectory(guest, Narrow);

        Assert.Equal(Errno.NotCapable, Open(guest, sub, ".", Rights.PathOpen, Rights.None, OFlags.Directory));
    }

    /// <summary>Opens <c>sub</c> beneath the preopen, able to open beneath it and nothing more.</summary>
    private static uint OpenSubdirectory(TrampolineGuest guest, Rights inheriting)
    {
        Assert.Equal(Errno.Success, Open(guest, TrampolineGuest.Root, "sub", Rights.PathOpen, inheriting, OFlags.Directory));
        return guest.ReadU32(TrampolineGuest.ResultSlot);
    }

    private static Errno Open(
        TrampolineGuest guest,
        uint fd,
        string path,
        Rights rightsBase,
        Rights rightsInheriting,
        OFlags oflags = 0)
    {
        int length = guest.WritePath(TrampolineGuest.PathSlot, path);
        return guest.Call(
            "path_open",
            (int)fd,
            (int)LookupFlags.SymlinkFollow,
            TrampolineGuest.PathSlot,
            length,
            (int)oflags,
            (long)rightsBase,
            (long)rightsInheriting,
            0,
            TrampolineGuest.ResultSlot);
    }
}
