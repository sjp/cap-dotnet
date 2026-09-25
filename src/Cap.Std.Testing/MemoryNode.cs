using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Testing;

/// <summary>
/// One object in a filesystem held in memory: a file, a directory, a symbolic link, or
/// something a test has made to stand for another kind.
/// </summary>
/// <remarks>
/// <para>
/// Shared by <see cref="InMemoryFileSystem"/> and by the simulation the library's own tests
/// attack the resolver with, so that the two describe objects the same way. It is deliberately
/// mutable and reachable while a resolution is in flight. The interesting failures in a path
/// walk are the ones where a component is one thing when it is looked at and another when it
/// is opened, and a simulation can only produce those if a test can change a node at will.
/// </para>
/// <para>
/// Nothing here takes the filesystem's lock. A backend that is used from several threads
/// guards the tree itself; the contents alone are guarded here, because a file handle promises
/// reads and writes from several threads at once and a simulation that has no tree lock must
/// still keep that promise.
/// </para>
/// </remarks>
internal sealed class MemoryNode
{
    private readonly object _contentLock = new();
    private byte[] _content = [];
    private long _length;

    /// <summary>Creates a node whose entries, if it is a directory, compare names exactly.</summary>
    public MemoryNode()
        : this(StringComparer.Ordinal)
    {
    }

    /// <summary>Creates a node whose entries compare names as <paramref name="names"/> does.</summary>
    /// <param name="names">
    /// How two names are decided to be the same one. A filesystem that ignores case gives every
    /// directory a comparer that does, so a lookup, a creation and a rename all agree on it.
    /// </param>
    public MemoryNode(StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(names);
        Entries = new Dictionary<string, MemoryNode>(names);
    }

    /// <summary>What this object is.</summary>
    public CapNodeType Type { get; set; } = CapNodeType.Directory;

    /// <summary>Which filesystem it lives on.</summary>
    public ulong VolumeId { get; set; }

    /// <summary>Its identity within that filesystem.</summary>
    public ulong NodeId { get; set; }

    /// <summary>The stored target, when this is a link.</summary>
    public string? LinkTarget { get; set; }

    /// <summary>
    /// Whether a link was made as a link to a directory, which Windows records and the other
    /// platforms do not.
    /// </summary>
    public bool LinkIsDirectory { get; set; }

    /// <summary>The reparse tag, for modelling a Windows link that is not a filesystem link.</summary>
    public uint ReparseTag { get; set; }

    /// <summary>True when every operation on this object, and every lookup inside it, is refused.</summary>
    public bool Unreadable { get; set; }

    /// <summary>
    /// True when removing this object's name is refused until the refusal is cleared.
    /// </summary>
    /// <remarks>
    /// Models the Windows read-only attribute, which lives on the object rather than in its
    /// security descriptor and stops the object being deleted by an account otherwise
    /// entitled to delete it. Anything that empties a directory has to deal with it, and
    /// simulating it is the only way to exercise that on a machine that is not Windows.
    /// </remarks>
    public bool RefusesRemoval { get; set; }

    /// <summary>
    /// True when removing or replacing this object's name is refused, and clearing the
    /// removal block does not help.
    /// </summary>
    /// <remarks>
    /// A fault a test injects, standing for a name the filesystem will not give up for a reason
    /// no retry fixes: an immutable file, or a directory whose permissions deny the removal.
    /// </remarks>
    public bool Undeletable { get; set; }

    /// <summary>
    /// True once the object has no name left in any directory: a directory that was removed,
    /// or a file whose last name was removed while something still held it open.
    /// </summary>
    /// <remarks>
    /// A handle on such an object keeps working for what can be done without a name, and a
    /// directory in this state is empty and can be given nothing new, as on a real system.
    /// </remarks>
    public bool Detached { get; set; }

    /// <summary>
    /// True when a read of the directory holding this entry should decline to say what it
    /// is, leaving the kind to be looked up separately.
    /// </summary>
    /// <remarks>
    /// Several real filesystems answer that way for every entry they hold. Modelling it is
    /// the only way to exercise the lookup that covers for them without needing one of those
    /// filesystems mounted on the machine running the tests.
    /// </remarks>
    public bool HidesKindFromDirectoryRead { get; set; }

    /// <summary>
    /// What a caller reading the directory is told this is, overriding what
    /// <see cref="Type"/> implies.
    /// </summary>
    /// <remarks>
    /// Resolution collapses sockets, pipes and device nodes into one case because it has no
    /// use for the difference; a caller listing a directory is told which it is. Setting
    /// this is how a test produces one of those without the simulation having to model what
    /// they are.
    /// </remarks>
    public CapFileType? EntryType { get; set; }

    /// <summary>The length of this object's contents, in bytes.</summary>
    /// <remarks>
    /// Setting it truncates the contents or extends them with zeroes, as setting the length of
    /// a real file does.
    /// </remarks>
    public long Length
    {
        get
        {
            lock (_contentLock)
            {
                return _length;
            }
        }

        set => SetLength(value);
    }

    /// <summary>A copy of this object's contents.</summary>
    /// <remarks>
    /// Setting it replaces them, which is how a test seeds a file with bytes to be read.
    /// </remarks>
    public byte[] Contents
    {
        get
        {
            lock (_contentLock)
            {
                return _content.AsSpan(0, (int)_length).ToArray();
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_contentLock)
            {
                _content = (byte[])value.Clone();
                _length = value.Length;
            }
        }
    }

    /// <summary>When this object's contents were last read.</summary>
    public DateTimeOffset LastAccessTime { get; set; }

    /// <summary>When this object's contents were last changed.</summary>
    public DateTimeOffset LastWriteTime { get; set; }

    /// <summary>When this object was created, or null to model a filesystem that does not say.</summary>
    public DateTimeOffset? CreationTime { get; set; }

    /// <summary>
    /// When anything about this object last changed, or null to model a filesystem that does
    /// not say.
    /// </summary>
    public DateTimeOffset? ChangeTime { get; set; }

    /// <summary>How many directory entries refer to this object.</summary>
    /// <remarks>
    /// Kept up to date by whichever backend creates and removes names. A directory's count is
    /// the backend's to report, since the platforms disagree about what it counts.
    /// </remarks>
    public long LinkCount { get; set; } = 1;

    /// <summary>
    /// The Unix mode bits, when the filesystem is standing in for a Unix platform.
    /// </summary>
    /// <remarks>
    /// Set by default so that the common case needs no arranging. A filesystem standing in for
    /// the other kind of platform clears this and sets <see cref="WindowsAttributes"/>; the
    /// two are never both reported, because no real platform reports both.
    /// </remarks>
    public UnixFileMode? UnixMode { get; set; } =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// The Windows attribute bits, when the filesystem is standing in for Windows.
    /// </summary>
    public FileAttributes? WindowsAttributes { get; set; }

    /// <summary>Entries, when this is a directory.</summary>
    public Dictionary<string, MemoryNode> Entries { get; }

    /// <summary>Copies contents from a position into a buffer.</summary>
    /// <returns>How many bytes were copied. Zero at or beyond the end.</returns>
    public int ReadAt(Span<byte> buffer, long offset)
    {
        lock (_contentLock)
        {
            if (offset >= _length)
            {
                return 0;
            }

            int count = (int)Math.Min(buffer.Length, _length - offset);
            _content.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        }
    }

    /// <summary>Writes a buffer at a position, extending the contents with zeroes to reach it.</summary>
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        lock (_contentLock)
        {
            WriteLocked(buffer, offset);
        }
    }

    /// <summary>Writes a buffer at the end of the contents, found and written in one step.</summary>
    public void Append(ReadOnlySpan<byte> buffer)
    {
        lock (_contentLock)
        {
            WriteLocked(buffer, _length);
        }
    }

    /// <summary>Truncates the contents, or extends them with zeroes.</summary>
    public void SetLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (_contentLock)
        {
            EnsureCapacity(length);
            if (length < _length)
            {
                _content.AsSpan((int)length, (int)(_length - length)).Clear();
            }

            _length = length;
        }
    }

    private void WriteLocked(ReadOnlySpan<byte> buffer, long offset)
    {
        long end = offset + buffer.Length;
        EnsureCapacity(end);
        buffer.CopyTo(_content.AsSpan((int)offset));
        _length = Math.Max(_length, end);
    }

    /// <summary>
    /// Grows the backing array to hold a length. Bytes past the current length are always
    /// zero, so growing the length exposes zeroes, as a gap in a real file reads.
    /// </summary>
    private void EnsureCapacity(long length)
    {
        if (length > Array.MaxLength)
        {
            throw new IOException("A file held in memory cannot hold that much.");
        }

        if (length > _content.Length)
        {
            Array.Resize(ref _content, (int)Math.Max(length, Math.Min(Array.MaxLength, (long)_content.Length * 2)));
        }
    }

    /// <summary>Its description, as the platform layer would report it.</summary>
    public CapNodeInfo Info => new(Type, VolumeId, NodeId, ReparseTag);

    /// <summary>Its full description, as the metadata layer would report it.</summary>
    public CapNodeStat Stat => Describe(LinkCount);

    /// <summary>Its full description, with a link count the caller worked out.</summary>
    public CapNodeStat Describe(long linkCount) => new(
        FileType,
        VolumeId,
        NodeId,
        Length,
        LastAccessTime,
        LastWriteTime,
        CreationTime,
        ChangeTime,
        linkCount,
        UnixMode,
        UnixMode is null ? WindowsAttributes : null);

    /// <summary>Its kind, as a caller reading the directory is told it.</summary>
    public CapFileType FileType => EntryType ?? Type switch
    {
        CapNodeType.File => CapFileType.File,
        CapNodeType.Directory => CapFileType.Directory,
        CapNodeType.SymbolicLink => CapFileType.Symlink,
        CapNodeType.UnknownReparsePoint => CapFileType.ReparsePoint,
        _ => CapFileType.Unknown,
    };
}
