using Cap.Primitives;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// An attempt to leave a handle's tree through an in-memory filesystem is refused exactly as
/// it is on disk: the same exception type and the same kind.
/// </summary>
/// <remarks>
/// Each case is run against a tree on disk built the same way, and the two refusals compared,
/// so the test says nothing about what the refusal is and everything about whether the two
/// agree.
/// </remarks>
public sealed class EscapeParityTests : IDisposable
{
    private readonly DirectoryInfo _disk = Directory.CreateTempSubdirectory("cap-memory-parity-");
    private readonly bool _linksAvailable;

    public EscapeParityTests()
    {
        Directory.CreateDirectory(Path.Combine(_disk.FullName, "sandbox", "inner"));
        Directory.CreateDirectory(Path.Combine(_disk.FullName, "outside"));
        File.WriteAllText(Path.Combine(_disk.FullName, "outside", "secret.txt"), "secret");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_disk.FullName, "sandbox", "out"), Path.Combine("..", "outside"));
            File.CreateSymbolicLink(
                Path.Combine(_disk.FullName, "sandbox", "inner", "up"), Path.Combine("..", "..", "outside", "secret.txt"));
            _linksAvailable = true;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            // Windows without the privilege to create links. The cases then cannot be built on
            // disk to compare against, and are skipped.
        }
    }

    public void Dispose() => _disk.Delete(recursive: true);

    public static TheoryData<ResolutionBackend, string> Cases()
    {
        TheoryData<ResolutionBackend, string> cases = [];
        foreach (ResolutionBackend resolution in (ResolutionBackend[])[ResolutionBackend.PortableWalk, ResolutionBackend.ConfinedOpen])
        {
            foreach (string path in (string[])["/etc/passwd", "../outside/secret.txt", "inner/../../outside/secret.txt", "out/secret.txt", "inner/up"])
            {
                cases.Add(resolution, path);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void An_escape_is_refused_as_it_is_on_disk(ResolutionBackend resolution, string path)
    {
        Assert.SkipUnless(_linksAvailable, "This host cannot create the symbolic links the disk tree needs.");

        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { Resolution = resolution });
        fs.AddDirectory("sandbox/inner");
        fs.AddFile("outside/secret.txt", "secret");
        fs.AddSymbolicLink("sandbox/out", "../outside");
        fs.AddSymbolicLink("sandbox/inner/up", "../../outside/secret.txt");

        using Dir memory = fs.OpenRoot("sandbox");
        using Dir disk = Dir.Open(Path.Combine(_disk.FullName, "sandbox"), AmbientAuthority.Acquire());

        Exception onDisk = Assert.ThrowsAny<Exception>(() => disk.ReadAllBytes(path));
        Exception inMemory = Assert.ThrowsAny<Exception>(() => memory.ReadAllBytes(path));

        Assert.Equal(onDisk.GetType(), inMemory.GetType());
        Assert.Equal(CapIOException.KindOf(onDisk), CapIOException.KindOf(inMemory));
    }

    [Fact]
    public void A_planted_link_to_a_rooted_target_is_refused_as_an_escape()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        fs.AddSymbolicLink("passwd", "/etc/passwd");

        using Dir root = fs.OpenRoot();

        SandboxEscapeException refused = Assert.Throws<SandboxEscapeException>(() => root.ReadAllBytes("passwd"));
        Assert.Equal(CapErrorKind.Escaped, refused.Kind);
        Assert.Throws<SandboxEscapeException>(() => root.CreateSymlink("again", "/etc/passwd"));
    }
}
