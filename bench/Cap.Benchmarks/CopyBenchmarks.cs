using BenchmarkDotNet.Attributes;
using Cap.Fs.Ext;
using Cap.Std;

namespace Cap.Benchmarks;

// Copies, one large file and one tree of small ones. Each side moves contents by whatever
// shortcut the filesystem under the temporary directory offers: File.Copy asks for a reflink and
// then copy_file_range on Linux, clonefile on macOS and CopyFileEx on Windows, and the copy
// helpers take their own route to the same shortcuts. So the rows say how the two compare on
// this filesystem, and are not a measure of either shortcut.

/// <summary>Copy a 64 MiB file onto a name it replaces.</summary>
/// <remarks>
/// Replacing, so every iteration does the same work: the baseline truncates and refills the
/// file, and the copy writes a scratch file beside it and moves it over the name.
/// </remarks>
[MemoryDiagnoser]
public class CopyLargeFile
{
    private const int Length = 64 * 1024 * 1024;
    private const string Source = "large.bin";
    private const string Destination = "large-copy.bin";
    private Fixture _fixture = null!;
    private string _ambientSource = null!;
    private string _ambientDestination = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        _fixture.WriteFile(Source, Length);
        _ambientSource = _fixture.Combine(Source);
        _ambientDestination = _fixture.Combine(Destination);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark(Baseline = true)]
    public void SystemIO() => File.Copy(_ambientSource, _ambientDestination, overwrite: true);

    [Benchmark]
    public long CapDotnet() => _fixture.Root.CopyFile(Source, _fixture.Root, Destination, overwrite: true);
}

/// <summary>
/// Copy a tree of ten thousand 4 KiB files in a hundred directories into an empty directory,
/// reported per file.
/// </summary>
/// <remarks>
/// The baseline is the loop a program writes with <c>System.IO</c>: create each directory, then
/// <see cref="File.Copy(string, string)"/> each file. The destination is removed and made again
/// before every iteration, outside the measurement.
/// </remarks>
[MemoryDiagnoser]
public class CopyTree
{
    private const int Directories = 100;
    private const int FilesPerDirectory = 100;
    private const int Files = Directories * FilesPerDirectory;
    private const string Tree = "tree";
    private const string Copy = "copy";
    private Fixture _fixture = null!;
    private string _ambientTree = null!;
    private string _ambientCopy = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        for (int d = 0; d < Directories; d++)
        {
            for (int f = 0; f < FilesPerDirectory; f++)
            {
                _fixture.WriteFile($"{Tree}/d{d:D3}/f{f:D3}.bin", 4096);
            }
        }

        _ambientTree = _fixture.Combine(Tree);
        _ambientCopy = _fixture.Combine(Copy);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [IterationSetup]
    public void Empty()
    {
        if (Directory.Exists(_ambientCopy))
        {
            Directory.Delete(_ambientCopy, recursive: true);
        }

        Directory.CreateDirectory(_ambientCopy);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Files)]
    public void SystemIO()
    {
        foreach (string directory in Directory.EnumerateDirectories(_ambientTree, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(_ambientCopy, Path.GetRelativePath(_ambientTree, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(_ambientTree, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Join(_ambientCopy, Path.GetRelativePath(_ambientTree, file)));
        }
    }

    [Benchmark(OperationsPerInvoke = Files)]
    public CopyReport CapDotnet()
    {
        using Dir source = _fixture.Root.OpenDir(Tree);
        using Dir destination = _fixture.Root.OpenDir(Copy);
        return source.CopyTo(destination);
    }
}
