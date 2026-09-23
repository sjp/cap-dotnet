using Cap.Primitives;
using Cap.Std;

namespace Cap.Benchmarks;

/// <summary>
/// A scratch directory on disk, opened both ways: as a <see cref="Dir"/> for the cap-dotnet
/// side of a comparison and as a path for the <c>System.IO</c> side.
/// </summary>
/// <remarks>
/// Both sides read the same files on the same volume, so a difference between two rows is the
/// difference between the two ways of reaching them and not between two filesystems. The tree
/// is built with <c>System.IO</c> because building it is setup, not what is measured.
/// </remarks>
internal sealed class Fixture : IDisposable
{
    private Fixture(string path, Dir root)
    {
        Path = path;
        Root = root;
    }

    /// <summary>The scratch directory, for the <c>System.IO</c> baselines.</summary>
    public string Path { get; }

    /// <summary>The same directory as a capability, for the cap-dotnet side.</summary>
    public Dir Root { get; }

    /// <summary>
    /// Creates an empty scratch directory, after checking the process is on the backend its
    /// job is named for.
    /// </summary>
    public static Fixture Create()
    {
        Backend.Verify();
        string path = Directory.CreateTempSubdirectory("cap-bench-").FullName;
        return new Fixture(path, Dir.Open(path, AmbientAuthority.Acquire()));
    }

    /// <summary>The ambient path of <paramref name="relative"/> beneath the scratch directory.</summary>
    public string Combine(string relative) => System.IO.Path.Join(Path, relative);

    /// <summary>Writes a file of <paramref name="length"/> bytes, creating its parents.</summary>
    public void WriteFile(string relative, int length)
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        byte[] bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>
    /// Fills <paramref name="relative"/> with <paramref name="count"/> empty files.
    /// </summary>
    /// <remarks>
    /// Empty because enumeration reads names, not contents, and so that a tree of this size is
    /// quick to build and to remove before every run.
    /// </remarks>
    public void CreateEmptyFiles(string relative, int count)
    {
        string directory = Combine(relative);
        Directory.CreateDirectory(directory);
        for (int i = 0; i < count; i++)
        {
            File.OpenHandle(System.IO.Path.Join(directory, $"f{i:D6}"), FileMode.CreateNew, FileAccess.Write).Dispose();
        }
    }

    public void Dispose()
    {
        Root.Dispose();
        Directory.Delete(Path, recursive: true);
    }
}
