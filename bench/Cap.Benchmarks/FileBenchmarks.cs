using BenchmarkDotNet.Attributes;
using Cap.Std;
using Microsoft.Win32.SafeHandles;

namespace Cap.Benchmarks;

// The operations a program makes on every request -- open a file by name and read it, read at
// an offset, ask when a file last changed. Each class is one operation measured both ways, with
// System.IO as the baseline, so the Ratio column is the price of reaching the file through a
// handle rather than through the process's ambient authority. These are the rows the regression
// gate watches.

/// <summary>Open and read a small file named by one component.</summary>
/// <remarks>
/// The confined open costs the same single syscall an ordinary open does, so on that backend
/// this row should read as parity. The walk costs the same here too: with one component there
/// is nothing to walk.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.HotPath)]
public class OpenReadSingleComponent
{
    private const string Name = "small.bin";
    private Fixture _fixture = null!;
    private string _ambientPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        _fixture.WriteFile(Name, 4096);
        _ambientPath = _fixture.Combine(Name);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark(Baseline = true)]
    public byte[] SystemIO() => File.ReadAllBytes(_ambientPath);

    [Benchmark]
    public byte[] CapDotnet() => _fixture.Root.ReadAllBytes(Name);
}

/// <summary>Open and read a small file five components beneath the handle.</summary>
/// <remarks>
/// The row that separates the backends. The confined open still resolves the whole path in one
/// syscall; the walk opens each of the four directories on the way and then the file, so its
/// cost grows with depth where the confined open's does not.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.HotPath)]
public class OpenReadFiveComponents
{
    private const string Name = "a/b/c/d/deep.bin";
    private Fixture _fixture = null!;
    private string _ambientPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        _fixture.WriteFile(Name, 4096);
        _ambientPath = _fixture.Combine(Name);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark(Baseline = true)]
    public byte[] SystemIO() => File.ReadAllBytes(_ambientPath);

    [Benchmark]
    public byte[] CapDotnet() => _fixture.Root.ReadAllBytes(Name);
}

/// <summary>Read 4 KiB at an offset from a file that is already open.</summary>
/// <remarks>
/// Nothing is resolved here -- both sides hold an open handle -- so this row measures only
/// what the wrapper adds to a positional read. It should be indistinguishable from the
/// baseline on every backend, and allocate nothing.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.HotPath)]
public class PositionalRead
{
    private const string Name = "data.bin";
    private const long Offset = 64 * 1024;
    private readonly byte[] _buffer = new byte[4096];
    private Fixture _fixture = null!;
    private SafeFileHandle _ambientHandle = null!;
    private CapFile _file = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        _fixture.WriteFile(Name, 1024 * 1024);
        _ambientHandle = File.OpenHandle(_fixture.Combine(Name));
        _file = _fixture.Root.OpenFile(Name);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _file.Dispose();
        _ambientHandle.Dispose();
        _fixture.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int SystemIO() => RandomAccess.Read(_ambientHandle, _buffer, Offset);

    [Benchmark]
    public int CapDotnet() => _file.Read(_buffer, Offset);
}

/// <summary>Ask when a file named by one component last changed.</summary>
/// <remarks>
/// The cap-dotnet side reads every field of the file's metadata at once and takes one, which
/// is what the baseline does beneath its API too.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.HotPath)]
public class StatFile
{
    private const string Name = "small.bin";
    private Fixture _fixture = null!;
    private string _ambientPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        _fixture.WriteFile(Name, 4096);
        _ambientPath = _fixture.Combine(Name);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark(Baseline = true)]
    public DateTime SystemIO() => File.GetLastWriteTimeUtc(_ambientPath);

    [Benchmark]
    public DateTimeOffset CapDotnet() => _fixture.Root.GetMetadata(Name).LastWriteTime;
}
