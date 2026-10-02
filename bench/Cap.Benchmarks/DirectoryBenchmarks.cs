using System.IO.Enumeration;
using BenchmarkDotNet.Attributes;
using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Benchmarks;

// Operations over many entries at once. They take long enough, and their trees take long
// enough to build, that they are left out of the per-change regression gate and run with the
// full suite instead.

/// <summary>List a directory of a hundred thousand entries.</summary>
/// <remarks>
/// <para>
/// The cap-dotnet side reads each entry's kind from the directory read itself where the
/// filesystem reports it, and hands back the bare name, so it builds no path string per entry.
/// The baseline builds a full path for every entry it yields. This is the row where the handle
/// is expected to be competitive or better, not merely close.
/// </para>
/// <para>
/// <see cref="SystemIONames"/> is the like-for-like comparison: the same listing through
/// <see cref="FileSystemEnumerable{TResult}"/>, yielding each file's bare name as
/// <see cref="DirEntry.Name"/> does, so it allocates what the cap-dotnet side allocates and
/// the gap between the two is the price of the handle. <see cref="SystemIO"/> stays the
/// baseline because it is the call a caller would be replacing.
/// </para>
/// <para>
/// Each side opens the directory inside the measured call, as a caller listing it once would.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EnumerateDirectory
{
    private const string Name = "wide";
    private const int Entries = 100_000;
    private Fixture _fixture = null!;
    private string _ambientPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        _fixture.CreateEmptyFiles(Name, Entries);
        _ambientPath = _fixture.Combine(Name);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark(Baseline = true)]
    public int SystemIO()
    {
        int files = 0;
        foreach (string _ in Directory.EnumerateFiles(_ambientPath))
        {
            files++;
        }

        return Checked(files);
    }

    [Benchmark]
    public int SystemIONames()
    {
        int files = 0;
        var names = new FileSystemEnumerable<string>(
            _ambientPath,
            static (ref FileSystemEntry entry) => entry.FileName.ToString(),
            new EnumerationOptions { AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
        };
        foreach (string _ in names)
        {
            files++;
        }

        return Checked(files);
    }

    [Benchmark]
    public int CapDotnet()
    {
        int files = 0;
        using Dir wide = _fixture.Root.OpenDir(Name);
        foreach (DirEntry entry in wide.EnumerateEntries())
        {
            if (entry.Type == CapFileType.File)
            {
                files++;
            }
        }

        return Checked(files);
    }

    private static int Checked(int files) =>
        files == Entries ? files : throw new InvalidOperationException($"Listed {files} files, expected {Entries}.");
}

/// <summary>Walk a tree of fifty thousand files, counting them.</summary>
/// <remarks>
/// A hundred directories two levels deep, five hundred files in each. The cap-dotnet side
/// descends by opening each directory relative to its parent's handle; the baseline composes a
/// path for each directory and each file.
/// </remarks>
[MemoryDiagnoser]
public class WalkTree
{
    private const int Branches = 10;
    private const int FilesPerDirectory = 500;
    private const int Files = Branches * Branches * FilesPerDirectory;
    private Fixture _fixture = null!;
    private string _ambientPath = null!;
    private Dir _tree = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        for (int outer = 0; outer < Branches; outer++)
        {
            for (int inner = 0; inner < Branches; inner++)
            {
                _fixture.CreateEmptyFiles($"tree/d{outer}/d{inner}", FilesPerDirectory);
            }
        }

        _ambientPath = _fixture.Combine("tree");
        _tree = _fixture.Root.OpenDir("tree");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _tree.Dispose();
        _fixture.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int SystemIO()
    {
        int files = 0;
        foreach (string _ in Directory.EnumerateFiles(_ambientPath, "*", SearchOption.AllDirectories))
        {
            files++;
        }

        return Checked(files);
    }

    [Benchmark]
    public int CapDotnet()
    {
        int files = 0;
        foreach (WalkEntry entry in _tree.Walk())
        {
            if (entry.Type == CapFileType.File)
            {
                files++;
            }
        }

        return Checked(files);
    }

    private static int Checked(int files) =>
        files == Files ? files : throw new InvalidOperationException($"Walked {files} files, expected {Files}.");
}

/// <summary>Find the text files in a tree of fifty thousand files, counting them.</summary>
/// <remarks>
/// <para>
/// The tree <see cref="WalkTree"/> walks, with half of each directory's files named
/// <c>*.txt</c> and half <c>*.md</c>, searched for <c>**/*.txt</c>. The cap-dotnet side
/// matches each name against the pattern as the walk reads it; the baseline is
/// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> with the same
/// filter, which composes a path for each match.
/// </para>
/// <para>
/// Set against <see cref="WalkTree"/>, the Allocated column is the price of the pattern: it
/// should be the walk's figure less the paths of the names the search does not yield, and
/// grow with nothing else.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class GlobTree
{
    private const int Branches = 10;
    private const int FilesPerDirectory = 500;
    private const int Matches = Branches * Branches * FilesPerDirectory / 2;
    private Fixture _fixture = null!;
    private string _ambientPath = null!;
    private Dir _tree = null!;
    private GlobPattern _pattern = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        for (int outer = 0; outer < Branches; outer++)
        {
            for (int inner = 0; inner < Branches; inner++)
            {
                _fixture.CreateEmptyFiles($"tree/d{outer}/d{inner}", FilesPerDirectory / 2, ".txt");
                _fixture.CreateEmptyFiles($"tree/d{outer}/d{inner}", FilesPerDirectory / 2, ".md");
            }
        }

        _ambientPath = _fixture.Combine("tree");
        _tree = _fixture.Root.OpenDir("tree");
        _pattern = GlobPattern.Parse("**/*.txt");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _tree.Dispose();
        _fixture.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int SystemIO()
    {
        int files = 0;
        foreach (string _ in Directory.EnumerateFiles(_ambientPath, "*.txt", SearchOption.AllDirectories))
        {
            files++;
        }

        return Checked(files);
    }

    [Benchmark]
    public int CapDotnet()
    {
        int files = 0;
        foreach (WalkEntry entry in _tree.Glob(_pattern))
        {
            if (entry.Type == CapFileType.File)
            {
                files++;
            }
        }

        return Checked(files);
    }

    private static int Checked(int files) =>
        files == Matches ? files : throw new InvalidOperationException($"Found {files} files, expected {Matches}.");
}

/// <summary>Create ten thousand empty files and delete them again, reported per file.</summary>
/// <remarks>
/// The baseline opens with <see cref="File.OpenHandle"/> rather than <see cref="File.Create(string)"/>.
/// A <see cref="CapFile"/> is a handle with positional reads and writes, and the object
/// <c>System.IO</c> has of that shape is a <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>;
/// <see cref="File.Create(string)"/> would add a <see cref="FileStream"/> to the baseline's
/// allocations that the other side never makes, and flatter the comparison.
/// </remarks>
[MemoryDiagnoser]
public class CreateDeleteFiles
{
    private const int Count = 10_000;
    private readonly string[] _names = new string[Count];
    private readonly string[] _ambientPaths = new string[Count];
    private Fixture _fixture = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = Fixture.Create();
        for (int i = 0; i < Count; i++)
        {
            _names[i] = $"f{i:D5}";
            _ambientPaths[i] = _fixture.Combine(_names[i]);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public void SystemIO()
    {
        foreach (string path in _ambientPaths)
        {
            File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write).Dispose();
        }

        foreach (string path in _ambientPaths)
        {
            File.Delete(path);
        }
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void CapDotnet()
    {
        Dir root = _fixture.Root;
        foreach (string name in _names)
        {
            root.CreateNewFile(name).Dispose();
        }

        foreach (string name in _names)
        {
            root.DeleteFile(name);
        }
    }
}
