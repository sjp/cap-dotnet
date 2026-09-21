using System.Buffers;

namespace Cap.Primitives.Interop;

/// <summary>
/// The directories a walk has descended through, each still open.
/// </summary>
/// <remarks>
/// <para>
/// Holding them open is the point. A walk that meets <c>..</c> has to move up, and there are
/// two ways to do that: ask the kernel for the parent of the directory currently open, or
/// go back to a handle already held. Only the second is safe. Asking the kernel resolves the
/// parent of whatever that directory is <em>now</em>, and a concurrent rename can have moved
/// it anywhere — including outside the sandbox, where the walk would then carry on quite
/// happily, because every subsequent step is still correctly relative to a handle. Stepping
/// back through a handle we already have cannot be redirected by anything, because nothing
/// about it is looked up.
/// </para>
/// <para>
/// The root is borrowed rather than owned: it belongs to the caller and outlives the walk.
/// Everything above it is owned and is closed by <see cref="Dispose"/>, which every exit
/// from the walk must reach. Descriptors are a process-wide resource and a walk that leaks
/// them on its error paths — the paths a hostile input is trying to take — is a walk that
/// can be made to break opens in code that has nothing to do with it.
/// </para>
/// </remarks>
internal ref struct DirectoryStack
{
    /// <summary>Entries to make room for before any growth is needed.</summary>
    private const int InitialCapacity = 8;

    private readonly SafeDirHandle _root;
    private Entry[]? _entries;
    private int _depth;
    private ulong _rootVolumeId;

    /// <summary>Starts a stack whose base is <paramref name="root"/>.</summary>
    public DirectoryStack(SafeDirHandle root)
    {
        _root = root;
        _entries = null;
        _depth = 0;
        _rootVolumeId = 0;
    }

    /// <summary>
    /// True when nothing has been descended into, so the top is the sandbox root itself and
    /// a further <c>..</c> would leave it.
    /// </summary>
    public readonly bool AtRoot => _depth == 0;

    /// <summary>The directory the next component is looked up in.</summary>
    public readonly SafeDirHandle Top => _depth == 0 ? _root : _entries![_depth - 1].Handle;

    /// <summary>
    /// Which filesystem the top directory is on, or zero when the walk was not asked to care.
    /// </summary>
    public readonly ulong TopVolumeId => _depth == 0 ? _rootVolumeId : _entries![_depth - 1].VolumeId;

    /// <summary>
    /// Records which filesystem the root is on, so that a step onto another one can be
    /// recognised as having crossed a mount point.
    /// </summary>
    public void SetRootVolumeId(ulong volumeId) => _rootVolumeId = volumeId;

    /// <summary>
    /// Takes ownership of <paramref name="handle"/> and makes it the new top.
    /// </summary>
    /// <returns>
    /// False when the depth limit is reached, in which case the handle is not taken and the
    /// caller still owns it.
    /// </returns>
    public bool TryPush(SafeDirHandle handle, ulong volumeId)
    {
        if (_depth == PortableResolver.MaxDepth)
        {
            return false;
        }

        EnsureCapacity(_depth + 1);
        _entries![_depth] = new Entry(handle, volumeId);
        _depth++;
        return true;
    }

    /// <summary>Closes the top handle and steps back to the one below it.</summary>
    public void Pop()
    {
        _entries![--_depth].Handle.Dispose();
        _entries[_depth] = default;
    }

    /// <summary>
    /// Hands the top directory to the caller, who becomes responsible for closing it.
    /// </summary>
    /// <remarks>
    /// At the root this has to duplicate rather than surrender, because the root is the
    /// caller's and the walk only borrowed it. That case is reached by any path that ends
    /// back where it started — <c>a/..</c>, or a link pointing at the directory holding it —
    /// and the copy is of a handle the caller already has, so it conveys nothing new.
    /// </remarks>
    public CapResult<SafeDirHandle> DetachTop(IPlatformOps ops)
    {
        if (_depth == 0)
        {
            return ops.DuplicateDirectory(_root);
        }

        SafeDirHandle handle = _entries![--_depth].Handle;
        _entries[_depth] = default;
        return CapResult<SafeDirHandle>.Ok(handle);
    }

    /// <summary>Closes every handle the walk still owns.</summary>
    public void Dispose()
    {
        for (int i = _depth - 1; i >= 0; i--)
        {
            _entries![i].Handle.Dispose();
        }

        _depth = 0;

        if (_entries is not null)
        {
            // Cleared on return: the array goes back to a pool other code will be handed,
            // and an uncleared slot would keep a closed handle object alive for as long as
            // the pool holds the buffer.
            ArrayPool<Entry>.Shared.Return(_entries, clearArray: true);
            _entries = null;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_entries is not null && _entries.Length >= required)
        {
            return;
        }

        int capacity = Math.Max(InitialCapacity, required);
        if (_entries is not null)
        {
            capacity = Math.Max(capacity, _entries.Length * 2);
        }

        Entry[] grown = ArrayPool<Entry>.Shared.Rent(Math.Min(capacity, PortableResolver.MaxDepth));
        if (_entries is not null)
        {
            Array.Copy(_entries, grown, _depth);
            ArrayPool<Entry>.Shared.Return(_entries, clearArray: true);
        }

        _entries = grown;
    }

    /// <summary>One directory the walk is standing on, and the filesystem it is on.</summary>
    private readonly struct Entry
    {
        public Entry(SafeDirHandle handle, ulong volumeId)
        {
            Handle = handle;
            VolumeId = volumeId;
        }

        public SafeDirHandle Handle { get; }

        public ulong VolumeId { get; }
    }
}
