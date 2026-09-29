using Cap.Tests;

namespace Cap.Testing;

/// <summary>Whether a test can plant the links it attacks through, in the tree it arranges.</summary>
internal static class LinkSupport
{
    /// <summary>
    /// Makes and removes a symbolic link in <paramref name="directory"/>, on
    /// <see cref="HostTree.Current"/>. Where that is refused the test stands aside, or fails if
    /// this run was set up to have links (see <see cref="ExpectedHostFeatures"/>).
    /// </summary>
    public static void RequireSymbolicLinks(string directory) =>
        HostLinks.Require(HostFeature.Symlinks, () =>
        {
            string probe = Path.Join(directory, "link-probe");
            HostFile.CreateSymbolicLink(probe, "target");
            HostFile.Delete(probe);
        });
}
