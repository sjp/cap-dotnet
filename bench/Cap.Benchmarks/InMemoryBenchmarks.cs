using BenchmarkDotNet.Attributes;
using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.Benchmarks;

/// <summary>
/// The hot-path operations against <see cref="InMemoryFileSystem"/>, under each resolution it
/// models.
/// </summary>
/// <remarks>
/// <para>
/// Two CI legs run the whole test suite against this filesystem in place of the host, so a
/// pathological slowdown in it shows up first as slow CI, with nothing to say where. These rows
/// say where.
/// </para>
/// <para>
/// There is no <c>System.IO</c> side: nothing in <c>System.IO</c> does what this filesystem
/// does, and the disk is not a yardstick for memory. With no baseline the rows have no ratio,
/// so the regression gate holds them to their allocation, and their times are reported for a
/// reader to compare with the disk rows of the same name. Nothing here touches the host, so the
/// rows run under one job, not once per host backend.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.HotPath, Categories.AllocationOnly, Categories.BackendIndependent)]
public class InMemory
{
    private const string SingleComponent = "small.bin";
    private const string FiveComponents = "a/b/c/d/deep.bin";
    private const string NewFile = "a/b/c/d/f";
    private const int Branches = 10;
    private const int FilesPerDirectory = 10;
    private const int Files = Branches * Branches * FilesPerDirectory;
    private Dir _root = null!;
    private Dir _tree = null!;

    /// <summary>The way the filesystem resolves a path: a name at a time, or all at once.</summary>
    [Params(ResolutionBackend.PortableWalk, ResolutionBackend.ConfinedOpen)]
    public ResolutionBackend Resolution { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var fs = new InMemoryFileSystem(new InMemoryFileSystemOptions { Resolution = Resolution });
        byte[] contents = new byte[4096];
        new Random(contents.Length).NextBytes(contents);
        fs.AddFile(SingleComponent, contents);
        fs.AddFile(FiveComponents, contents);
        for (int outer = 0; outer < Branches; outer++)
        {
            for (int inner = 0; inner < Branches; inner++)
            {
                for (int file = 0; file < FilesPerDirectory; file++)
                {
                    fs.AddFile($"tree/d{outer}/d{inner}/f{file:D3}");
                }
            }
        }

        _root = fs.OpenRoot();
        _tree = _root.OpenDir("tree");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _tree.Dispose();
        _root.Dispose();
    }

    /// <summary>The in-memory counterpart of <see cref="Cap.Benchmarks.OpenReadSingleComponent"/>.</summary>
    [Benchmark]
    public byte[] OpenReadSingleComponent() => _root.ReadAllBytes(SingleComponent);

    /// <summary>The in-memory counterpart of <see cref="Cap.Benchmarks.OpenReadFiveComponents"/>.</summary>
    [Benchmark]
    public byte[] OpenReadFiveComponents() => _root.ReadAllBytes(FiveComponents);

    /// <summary>The in-memory counterpart of <see cref="Cap.Benchmarks.StatFile"/>.</summary>
    [Benchmark]
    public DateTimeOffset StatFile() => _root.GetMetadata(SingleComponent).LastWriteTime;

    /// <summary>The in-memory counterpart of <see cref="Cap.Benchmarks.CreateDeleteFiveComponents"/>.</summary>
    [Benchmark]
    public void CreateDeleteFiveComponents()
    {
        _root.CreateNewFile(NewFile).Dispose();
        _root.DeleteFile(NewFile);
    }

    /// <summary>Walk a tree of a thousand files, a hundred directories two levels deep.</summary>
    [Benchmark]
    public int WalkTree()
    {
        int files = 0;
        foreach (WalkEntry entry in _tree.Walk())
        {
            if (entry.Type == CapFileType.File)
            {
                files++;
            }
        }

        return files == Files ? files : throw new InvalidOperationException($"Walked {files} files, expected {Files}.");
    }
}
