using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// An <see cref="IDir"/> that is not a <see cref="Dir"/>: it forwards every call to a real
/// handle and writes down what it was asked.
/// </summary>
/// <remarks>
/// <para>
/// The kind of wrapper a consumer writes to log, meter or restrict what a component does to a
/// directory. Handing one to a helper makes the helper take the path written against the
/// interface, and the log says which calls that path made.
/// </para>
/// <para>
/// Everything handed back stays inside the wrapper: directories opened through it are wrapped
/// in turn and share the log, entries it lists open through it, and files it opens are
/// wrapped so their writes and flushes are logged too. A handle passed in as a destination is
/// unwrapped before it reaches the real one.
/// </para>
/// </remarks>
internal sealed class RecordingDir(IDir inner, List<string> log, string label) : IDir
{
    public RecordingDir(IDir inner)
        : this(inner, [], ".")
    {
    }

    /// <summary>Every call made through this handle and the ones opened from it, in order.</summary>
    public List<string> Log => log;

    /// <summary>Which directory this is, as the calls in the log name it.</summary>
    public string Label => label;

    public SymlinkPolicy SymlinkPolicy => inner.SymlinkPolicy;

    public ResolutionBackend Backend => inner.Backend;

    public IDir OpenDir(string path, bool noFollow = false) =>
        Child(Record(inner.OpenDir(path, noFollow), path, noFollow), path);

    public bool TryOpenDir(string path, [NotNullWhen(true)] out IDir? dir) =>
        Opened(inner.TryOpenDir(path, out dir), ref dir, path, Call(path));

    public bool TryOpenDir(string path, bool noFollow, [NotNullWhen(true)] out IDir? dir) =>
        Opened(inner.TryOpenDir(path, noFollow, out dir), ref dir, path, Call(path, noFollow));

    public IDir CreateDir(string path) => Child(Record(inner.CreateDir(path), path), path);

    public bool TryCreateDir(string path, [NotNullWhen(true)] out IDir? dir) =>
        Opened(inner.TryCreateDir(path, out dir), ref dir, path, Call(path));

    public IDir OpenOrCreateDir(string path) => Child(Record(inner.OpenOrCreateDir(path), path), path);

    public bool TryOpenOrCreateDir(string path, [NotNullWhen(true)] out IDir? dir) =>
        Opened(inner.TryOpenOrCreateDir(path, out dir), ref dir, path, Call(path));

    public ICapFile OpenFile(
        string path,
        FileMode mode = FileMode.Open,
        FileAccess access = FileAccess.Read,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0,
        bool append = false,
        bool noFollow = false) =>
        File(Record(inner.OpenFile(path, mode, access, share, options, preallocationSize, append, noFollow), path, mode), path);

    public bool TryOpenFile(string path, [NotNullWhen(true)] out ICapFile? file) =>
        FileOpened(inner.TryOpenFile(path, out file), ref file, path, Call(path));

    public bool TryOpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        bool noFollow,
        [NotNullWhen(true)] out ICapFile? file) =>
        FileOpened(
            inner.TryOpenFile(path, mode, access, share, options, preallocationSize, append, noFollow, out file),
            ref file,
            path,
            Call(path, mode));

    public ICapOpened OpenAny(string path, FileShare share = FileShare.Read, FileOptions options = FileOptions.None, bool noFollow = false) =>
        Record(inner.OpenAny(path, share, options, noFollow), path);

    public bool TryOpenAny(string path, [NotNullWhen(true)] out ICapOpened? opened) =>
        Record(inner.TryOpenAny(path, out opened), path);

    public bool TryOpenAny(string path, bool noFollow, [NotNullWhen(true)] out ICapOpened? opened) =>
        Record(inner.TryOpenAny(path, noFollow, out opened), path, noFollow);

    public ICapFile CreateFile(string path) => File(Record(inner.CreateFile(path), path), path);

    public ICapFile CreateNewFile(string path) => File(Record(inner.CreateNewFile(path), path), path);

    public bool TryCreateFile(string path, [NotNullWhen(true)] out ICapFile? file) =>
        FileOpened(inner.TryCreateFile(path, out file), ref file, path, Call(path));

    public bool TryCreateNewFile(string path, [NotNullWhen(true)] out ICapFile? file) =>
        FileOpened(inner.TryCreateNewFile(path, out file), ref file, path, Call(path));

    public byte[] ReadAllBytes(string path) => Record(inner.ReadAllBytes(path), path);

    public string ReadAllText(string path) => Record(inner.ReadAllText(path), path);

    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        _ = Record(0, path);
        inner.WriteAllBytes(path, bytes);
    }

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) =>
        Record(inner.ReadAllBytesAsync(path, cancellationToken), path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        Record(inner.ReadAllTextAsync(path, cancellationToken), path);

    public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        Record(inner.WriteAllBytesAsync(path, bytes, cancellationToken), path);

    public void DeleteFile(string path)
    {
        _ = Record(0, path);
        inner.DeleteFile(path);
    }

    public bool TryDeleteFile(string path) => Record(inner.TryDeleteFile(path), path);

    public void DeleteDir(string path)
    {
        _ = Record(0, path);
        inner.DeleteDir(path);
    }

    public bool TryDeleteDir(string path) => Record(inner.TryDeleteDir(path), path);

    public void Rename(string from, IDir toDir, string to, bool replaceExisting = false)
    {
        _ = Record(0, from, Named(toDir), to, replaceExisting);
        inner.Rename(from, Unwrap(toDir), to, replaceExisting);
    }

    public bool TryRename(string from, IDir toDir, string to, bool replaceExisting = false) =>
        Record(inner.TryRename(from, Unwrap(toDir), to, replaceExisting), from, Named(toDir), to, replaceExisting);

    public void CreateSymlink(string linkPath, string target)
    {
        _ = Record(0, linkPath, target);
        inner.CreateSymlink(linkPath, target);
    }

    public bool TryCreateSymlink(string linkPath, string target) =>
        Record(inner.TryCreateSymlink(linkPath, target), linkPath, target);

    public void CreateDirSymlink(string linkPath, string target)
    {
        _ = Record(0, linkPath, target);
        inner.CreateDirSymlink(linkPath, target);
    }

    public bool TryCreateDirSymlink(string linkPath, string target) =>
        Record(inner.TryCreateDirSymlink(linkPath, target), linkPath, target);

    public void CreateHardLink(string path, IDir toDir, string to, bool followLink = false)
    {
        _ = Record(0, path, Named(toDir), to);
        inner.CreateHardLink(path, Unwrap(toDir), to, followLink);
    }

    public bool TryCreateHardLink(string path, IDir toDir, string to, bool followLink = false) =>
        Record(inner.TryCreateHardLink(path, Unwrap(toDir), to, followLink), path, Named(toDir), to);

    public bool Exists(string path) => Record(inner.Exists(path), path);

    public string ReadLink(string path) => Record(inner.ReadLink(path), path);

    public bool TryReadLink(string path, [NotNullWhen(true)] out string? target) =>
        Record(inner.TryReadLink(path, out target), path);

    public CapMetadata GetMetadata() => Record(inner.GetMetadata());

    public CapMetadata GetMetadata(string path, bool followLink = false) =>
        Record(inner.GetMetadata(path, followLink), path);

    public bool TryGetMetadata(string path, out CapMetadata metadata) =>
        Record(inner.TryGetMetadata(path, out metadata), path);

    public bool TryGetMetadata(string path, bool followLink, out CapMetadata metadata) =>
        Record(inner.TryGetMetadata(path, followLink, out metadata), path, followLink);

    public void SetTimes(CapFileTime lastAccess = default, CapFileTime lastWrite = default)
    {
        _ = Record(0);
        inner.SetTimes(lastAccess, lastWrite);
    }

    public void SetTimes(string path, CapFileTime lastAccess = default, CapFileTime lastWrite = default, bool followLink = false)
    {
        _ = Record(0, path);
        inner.SetTimes(path, lastAccess, lastWrite, followLink);
    }

    public bool TrySetTimes(string path, CapFileTime lastAccess = default, CapFileTime lastWrite = default, bool followLink = false) =>
        Record(inner.TrySetTimes(path, lastAccess, lastWrite, followLink), path);

    public bool Flush(bool toDisk) => Record(inner.Flush(toDisk), toDisk);

    public IEnumerable<IDirEntry> EnumerateEntries()
    {
        _ = Record(0);
        IEnumerable<IDirEntry> entries = inner.EnumerateEntries();
        return Relisted(entries);

        IEnumerable<IDirEntry> Relisted(IEnumerable<IDirEntry> source)
        {
            foreach (IDirEntry entry in source)
            {
                yield return TestEntries.Create(entry.Name, entry.Type, entry.FileId, this);
            }
        }
    }

    public IAsyncEnumerable<IDirEntry> EnumerateEntriesAsync(CancellationToken cancellationToken = default)
    {
        _ = Record(0);
        return Relisted(inner.EnumerateEntriesAsync(cancellationToken), CancellationToken.None);

        async IAsyncEnumerable<IDirEntry> Relisted(
            IAsyncEnumerable<IDirEntry> source,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (IDirEntry entry in source.WithCancellation(token).ConfigureAwait(false))
            {
                yield return TestEntries.Create(entry.Name, entry.Type, entry.FileId, this);
            }
        }
    }

    public IDir Clone() => Child(Record(inner.Clone()), ".");

    public bool TryClone([NotNullWhen(true)] out IDir? clone) =>
        Opened(inner.TryClone(out clone), ref clone, ".", Call());

    public IDir Restrict(SymlinkPolicy policy) => Child(Record(inner.Restrict(policy), policy), ".");

    public bool TryRestrict(SymlinkPolicy policy, [NotNullWhen(true)] out IDir? restricted) =>
        Opened(inner.TryRestrict(policy, out restricted), ref restricted, ".", Call(policy));

    public bool TryGetPath(AmbientAuthority authority, [NotNullWhen(true)] out string? path) =>
        Record(inner.TryGetPath(authority, out path));

    public void Dispose() => inner.Dispose();

    private static IDir Unwrap(IDir dir) => dir is RecordingDir recording ? recording.Inner : dir;

    /// <summary>How a handle passed as an argument appears in the log: bracketed, so it is not read as a name.</summary>
    private static string Named(IDir dir) => dir is RecordingDir recording ? $"[{recording.Label}]" : "[?]";

    private IDir Inner => inner;

    private string Call(object? first = null, object? second = null, [CallerMemberName] string member = "") =>
        $"{label}: {member}({string.Join(", ", new[] { first, second }.Where(a => a is not null))})";

    private T Record<T>(T result, object? first = null, object? second = null, object? third = null, object? fourth = null, [CallerMemberName] string member = "")
    {
        log.Add($"{label}: {member}({string.Join(", ", new[] { first, second, third, fourth }.Where(a => a is not null))})");
        return result;
    }

    private RecordingDir Child(IDir dir, string path) =>
        new(dir, log, path == "." ? label : $"{label}/{path.TrimEnd('/', '\\')}");

    private RecordingFile File(ICapFile file, string path) => new(file, log, $"{label}/{path}");

    private bool Opened(bool opened, [NotNullWhen(true)] ref IDir? dir, string path, string call)
    {
        log.Add(call);
        if (opened)
        {
            dir = Child(dir!, path);
        }

        return opened;
    }

    private bool FileOpened(bool opened, [NotNullWhen(true)] ref ICapFile? file, string path, string call)
    {
        log.Add(call);
        if (opened)
        {
            file = File(file!, path);
        }

        return opened;
    }
}

/// <summary>An <see cref="ICapFile"/> that forwards to a real one and logs its writes and flushes.</summary>
internal sealed class RecordingFile(ICapFile inner, List<string> log, string label) : ICapFile
{
    public FileAccess Access => inner.Access;

    public bool IsAsync => inner.IsAsync;

    public bool IsAppending
    {
        get => inner.IsAppending;
        set => inner.IsAppending = value;
    }

    public long Length => inner.Length;

    public CapMetadata GetMetadata() => inner.GetMetadata();

    public void SetLength(long length) => inner.SetLength(length);

    public void SetTimes(CapFileTime lastAccess = default, CapFileTime lastWrite = default)
    {
        log.Add($"{label}: SetTimes()");
        inner.SetTimes(lastAccess, lastWrite);
    }

    public void Flush(bool toDisk)
    {
        log.Add($"{label}: Flush({toDisk})");
        inner.Flush(toDisk);
    }

    public int Read(Span<byte> buffer, long fileOffset) => inner.Read(buffer, fileOffset);

    public void Write(ReadOnlySpan<byte> buffer, long fileOffset)
    {
        log.Add($"{label}: Write({buffer.Length}, {fileOffset})");
        inner.Write(buffer, fileOffset);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, fileOffset, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default)
    {
        log.Add($"{label}: WriteAsync({buffer.Length}, {fileOffset})");
        return inner.WriteAsync(buffer, fileOffset, cancellationToken);
    }

    public Stream AsStream(bool leaveOpen = true, int bufferSize = CapFile.DefaultStreamBufferSize) =>
        inner.AsStream(leaveOpen, bufferSize);

    public void Dispose() => inner.Dispose();
}
