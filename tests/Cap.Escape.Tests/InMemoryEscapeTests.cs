using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.Escape.Tests;

/// <summary>
/// A handle on a filesystem held in memory refuses to leave its tree for the reasons a handle
/// on disk does, down both resolution strategies and under both path syntaxes.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory filesystem replaces only the bottom layer, so every refusal here is made by
/// the same parsing and resolution that make it on disk. That is the claim the filesystem
/// rests on, and these cases hold it to it wherever the corpus runs, including as a NativeAOT
/// binary and as a trimmed single file.
/// </para>
/// <para>
/// No collection: each case builds its own filesystem, which shares nothing with any other
/// case or with the disk.
/// </para>
/// </remarks>
public sealed class InMemoryEscapeTests
{
    public static TheoryData<ResolutionBackend, CapPathSyntax, string> Escapes
    {
        get
        {
            TheoryData<ResolutionBackend, CapPathSyntax, string> rows = [];
            foreach (ResolutionBackend resolution in new[] { ResolutionBackend.PortableWalk, ResolutionBackend.ConfinedOpen })
            {
                foreach (string path in new[] { "../outside/secret.txt", "inner/../../outside/secret.txt", "out/secret.txt", "inner/up", "rooted" })
                {
                    rows.Add(resolution, CapPathSyntax.Unix, path);
                    rows.Add(resolution, CapPathSyntax.Windows, path);
                }

                rows.Add(resolution, CapPathSyntax.Unix, "/outside/secret.txt");
                rows.Add(resolution, CapPathSyntax.Windows, @"C:\outside\secret.txt");
                rows.Add(resolution, CapPathSyntax.Windows, @"..\outside\secret.txt");
                rows.Add(resolution, CapPathSyntax.Windows, "CON");
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Escapes))]
    public void An_escape_through_an_in_memory_handle_is_refused(ResolutionBackend resolution, CapPathSyntax syntax, string path)
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { Resolution = resolution, PathSyntax = syntax });
        fs.AddDirectory("sandbox/inner");
        fs.AddFile("outside/secret.txt", "secret");
        fs.AddSymbolicLink("sandbox/out", "../outside");
        fs.AddSymbolicLink("sandbox/inner/up", "../../outside/secret.txt");
        fs.AddSymbolicLink("sandbox/rooted", syntax == CapPathSyntax.Windows ? @"C:\outside\secret.txt" : "/outside/secret.txt");

        using Dir sandbox = fs.OpenRoot("sandbox");

        SandboxEscapeException refused = Assert.Throws<SandboxEscapeException>(() => sandbox.ReadAllBytes(path));
        Assert.Equal(CapErrorKind.Escaped, refused.Kind);

        // A write never follows a link at the last component, so where the path ends in one the
        // write is refused for that before the link's target is looked at. Either way nothing is
        // written, inside or out.
        Assert.ThrowsAny<IOException>(() => sandbox.WriteAllBytes(path, [1]));
        Assert.Equal("secret", fs.ReadAllText("outside/secret.txt"));
        Assert.Equal(["inner", "out", "rooted"], fs.GetEntries("sandbox"));
    }

    [Theory]
    [InlineData(ResolutionBackend.PortableWalk)]
    [InlineData(ResolutionBackend.ConfinedOpen)]
    public void A_link_refused_by_policy_is_refused_in_memory(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { Resolution = resolution });
        fs.AddFile("sandbox/real.txt", "x");
        fs.AddSymbolicLink("sandbox/alias.txt", "real.txt");

        using Dir sandbox = fs.OpenRoot("sandbox", SymlinkPolicy.Deny);

        Assert.Equal("x", sandbox.ReadAllText("real.txt"));
        Exception refused = Assert.ThrowsAny<Exception>(() => sandbox.ReadAllBytes("alias.txt/"));
        Assert.Equal(CapErrorKind.LinkNotFollowed, CapIOException.KindOf(refused));
    }
}
