using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// One entry a directory read produced, from whichever implementation of the directory read it.
/// </summary>
/// <remarks>
/// <para>
/// The helpers here accept any <see cref="IDir"/>, but the handle they are nearly always given is
/// a <see cref="Dir"/>, whose entries are a value type. Holding such an entry as the interface
/// would box it, one allocation per name read, on the path a walk over a large tree spends all
/// of its time on. So the entry is held here as it came: the value itself when a
/// <see cref="Dir"/> produced it, and the interface only when something else did.
/// </para>
/// <para>
/// Everything is answered by the entry itself, so an entry is opened and described through the
/// directory that listed it whichever form it is held in.
/// </para>
/// </remarks>
internal readonly struct ListedEntry
{
    private readonly DirEntry _concrete;
    private readonly IDirEntry? _other;

    public ListedEntry(DirEntry entry)
    {
        _concrete = entry;
        _other = null;
    }

    public ListedEntry(IDirEntry entry)
    {
        if (entry is DirEntry concrete)
        {
            _concrete = concrete;
            _other = null;
        }
        else
        {
            _concrete = default;
            _other = entry;
        }
    }

    /// <summary>The entry's name: a single component.</summary>
    public string Name => _other is null ? _concrete.Name : _other.Name;

    /// <summary>What the directory read said the entry is.</summary>
    public CapFileType Type => _other is null ? _concrete.Type : _other.Type;

    /// <summary>The entry as the interface, boxed only when it is asked for this way.</summary>
    public IDirEntry AsInterface() => _other ?? _concrete;

    public IDir OpenDir() => _other is null ? _concrete.OpenDir() : _other.OpenDir();

    public bool TryOpenDir([NotNullWhen(true)] out IDir? dir)
    {
        if (_other is not null)
        {
            return _other.TryOpenDir(out dir);
        }

        bool opened = _concrete.TryOpenDir(out Dir? concrete);
        dir = concrete;
        return opened;
    }

    public ICapFile OpenFile(
        FileMode mode = FileMode.Open,
        FileAccess access = FileAccess.Read,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0) =>
        _other is null
            ? _concrete.OpenFile(mode, access, share, options, preallocationSize)
            : _other.OpenFile(mode, access, share, options, preallocationSize);

    public bool TryOpenFile([NotNullWhen(true)] out ICapFile? file)
    {
        if (_other is not null)
        {
            return _other.TryOpenFile(out file);
        }

        bool opened = _concrete.TryOpenFile(out CapFile? concrete);
        file = concrete;
        return opened;
    }

    public CapMetadata GetMetadata() => _other is null ? _concrete.GetMetadata() : _other.GetMetadata();

    public bool TryGetMetadata(out CapMetadata metadata) =>
        _other is null ? _concrete.TryGetMetadata(out metadata) : _other.TryGetMetadata(out metadata);
}

/// <summary>
/// The reading of one directory's entries, kept in the form the directory produces them.
/// </summary>
/// <remarks>
/// A value rather than an object, so a walk or a copy pays for nothing beyond the enumerator
/// the directory hands back. Copying one is harmless: the copies share that enumerator, which
/// is where the position in the reading is kept.
/// </remarks>
internal readonly struct EntryReader
{
    private readonly IEnumerator<DirEntry>? _concrete;
    private readonly IEnumerator<IDirEntry>? _other;
    private readonly IAsyncEnumerator<DirEntry>? _concreteAsync;
    private readonly IAsyncEnumerator<IDirEntry>? _otherAsync;

    private EntryReader(
        IEnumerator<DirEntry>? concrete,
        IEnumerator<IDirEntry>? other,
        IAsyncEnumerator<DirEntry>? concreteAsync,
        IAsyncEnumerator<IDirEntry>? otherAsync)
    {
        _concrete = concrete;
        _other = other;
        _concreteAsync = concreteAsync;
        _otherAsync = otherAsync;
    }

    /// <summary>Starts reading a directory on the calling thread.</summary>
    public static EntryReader Open(IDir directory) =>
        directory is Dir concrete
            ? new EntryReader(concrete.EnumerateEntries().GetEnumerator(), null, null, null)
            : new EntryReader(null, directory.EnumerateEntries().GetEnumerator(), null, null);

    /// <summary>Starts reading a directory in the form that does not hold the calling thread.</summary>
    public static EntryReader OpenAsync(IDir directory, CancellationToken cancellationToken) =>
        directory is Dir concrete
            ? new EntryReader(
                null, null, concrete.EnumerateEntriesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken), null)
            : new EntryReader(
                null, null, null, directory.EnumerateEntriesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken));

    /// <summary>The entry the reading is on.</summary>
    public ListedEntry Current =>
        _concrete is not null ? new ListedEntry(_concrete.Current)
        : _other is not null ? new ListedEntry(_other.Current)
        : _concreteAsync is not null ? new ListedEntry(_concreteAsync.Current)
        : new ListedEntry(_otherAsync!.Current);

    public bool MoveNext() => _concrete?.MoveNext() ?? _other!.MoveNext();

    /// <summary>
    /// Moves to the next entry of a reading made on the calling thread, and hands it back.
    /// </summary>
    /// <remarks>
    /// One call per entry rather than two, with the form a <see cref="Dir"/> produces tested
    /// first: this is the step a walk over a large tree repeats for every name in it.
    /// </remarks>
    public bool TryNext(out ListedEntry entry)
    {
        if (_concrete is not null)
        {
            if (_concrete.MoveNext())
            {
                entry = new ListedEntry(_concrete.Current);
                return true;
            }
        }
        else if (_other!.MoveNext())
        {
            entry = new ListedEntry(_other.Current);
            return true;
        }

        entry = default;
        return false;
    }

    public ValueTask<bool> MoveNextAsync() =>
        _concreteAsync?.MoveNextAsync() ?? _otherAsync!.MoveNextAsync();

    /// <summary>Stops a reading made on the calling thread.</summary>
    public void Dispose()
    {
        _concrete?.Dispose();
        _other?.Dispose();
    }

    /// <summary>Stops a reading of either form, waiting for an asynchronous one to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();

        if (_concreteAsync is not null)
        {
            await _concreteAsync.DisposeAsync().ConfigureAwait(false);
        }

        if (_otherAsync is not null)
        {
            await _otherAsync.DisposeAsync().ConfigureAwait(false);
        }
    }
}
