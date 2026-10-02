using Cap.Escape.Tests;
using Cap.Primitives;
using Cap.Std;
using Cap.Tests;
using WasiHost.Preview1;

namespace WasiHost.Tests;

/// <summary>
/// The kind of link <c>path_symlink</c> makes, which the adapter chooses by opening the target
/// from the directory the link will sit in.
/// </summary>
/// <remarks>
/// The root is opened by path, through the host, so the class runs in the corpus's group, for
/// the reason <see cref="WasiDirectorySyncTests"/> gives.
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class WasiSymlinkTests
{
    /// <summary>
    /// A rooted target is refused without being read as a path beneath the link's directory:
    /// <c>/x</c> never stands for <c>docs/x</c>, even when that is a directory.
    /// </summary>
    [Fact]
    public void A_rooted_target_is_not_capable_and_makes_no_link()
    {
        using ScratchTree scratch = new();
        using Dir root = Dir.Open(scratch.HostPath, AmbientAuthority.Acquire());
        root.CreateDir("docs");
        root.CreateDir("docs/x");
        using TrampolineGuest guest = new(root);

        Assert.Equal(Errno.NotCapable, Symlink(guest, "/x", "docs/link"));
        Assert.False(root.TryGetMetadata("docs/link", followLink: false, out _));
    }

    /// <summary>
    /// A relative target that names a directory from the link's directory gets a link that can
    /// be traversed, which on Windows means one made as a directory link.
    /// </summary>
    [Fact]
    public void A_target_naming_a_directory_from_the_links_directory_gets_a_directory_link()
    {
        HostFeatures.Require(HostFeature.Symlinks, "The link cannot be made.");

        using ScratchTree scratch = new();
        using Dir root = Dir.Open(scratch.HostPath, AmbientAuthority.Acquire());
        root.CreateDir("docs");
        root.CreateDir("docs/real");
        root.WriteAllText("docs/real/file", "inside");
        using TrampolineGuest guest = new(root);

        Assert.Equal(Errno.Success, Symlink(guest, "real", "docs/link"));

        Assert.Equal("inside", root.ReadAllText("docs/link/file"));
        if (OperatingSystem.IsWindows())
        {
            CapMetadata link = root.GetMetadata("docs/link", followLink: false);
            Assert.Equal(CapFileType.Symlink, link.Type);
            Assert.True(link.Permissions.TryGetWindowsAttributes(out FileAttributes attributes));
            Assert.True(attributes.HasFlag(FileAttributes.Directory), $"The link was made as a file link: {attributes}.");
        }
    }

    private static Errno Symlink(TrampolineGuest guest, string target, string at)
    {
        int targetLength = guest.WritePath(TrampolineGuest.PathSlot, target);
        int atLength = guest.WritePath(TrampolineGuest.SecondPathSlot, at);
        return guest.Call(
            "path_symlink", TrampolineGuest.PathSlot, targetLength, TrampolineGuest.Root,
            TrampolineGuest.SecondPathSlot, atLength);
    }
}
