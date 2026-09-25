using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Tests.Fakes;

/// <summary>
/// One object in a simulated filesystem.
/// </summary>
/// <remarks>
/// Deliberately mutable and reachable from a test while a resolution is in flight. That is
/// the whole point of the simulation: the interesting failures in a path walk are the ones
/// where a component is one thing when it is looked at and another when it is opened, and
/// against a real kernel those can only be provoked by running an attack in a loop and
/// hoping to land in the window.
/// </remarks>
internal sealed class FakeNode
{
    // The contents are guarded because a file handle promises reads and writes from several
    // threads at once, and a test of that promise must not be defeated by the simulation.
    private readonly object _contentLock = new();
    private byte[] _content = [];
    private long _length;

    /// <summary>What this object is.</summary>
    public CapNodeType Type { get; set; } = CapNodeType.Directory;

    /// <summary>Which simulated filesystem it lives on.</summary>
    public ulong VolumeId { get; set; }

    /// <summary>Its identity within that filesystem.</summary>
    public ulong NodeId { get; set; }

    /// <summary>The stored target, when this is a link.</summary>
    public string? LinkTarget { get; set; }

    /// <summary>The reparse tag, for modelling a Windows link that is not a filesystem link.</summary>
    public uint ReparseTag { get; set; }

    /// <summary>True when every operation on this object should be refused.</summary>
    public bool Unreadable { get; set; }

    /// <summary>
    /// True when removing this object's name should be refused until the refusal is cleared.
    /// </summary>
    /// <remarks>
    /// Models the Windows read-only attribute, which lives on the object rather than in its
    /// security descriptor and stops the object being deleted by an account otherwise
    /// entitled to delete it. Anything that empties a directory has to deal with it, and
    /// simulating it is the only way to exercise that on a machine that is not Windows.
    /// </remarks>
    public bool RefusesRemoval { get; set; }

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
    /// Kept up to date by the simulation's hard-link creation and file removal, which are the
    /// operations a test of the count exercises. A rename that replaces a name is not counted
    /// against what it replaced.
    /// </remarks>
    public long LinkCount { get; set; } = 1;

    /// <summary>
    /// The Unix mode bits, when the simulation is standing in for a Unix platform.
    /// </summary>
    /// <remarks>
    /// Set by default so that the common case needs no arranging. A test that wants the
    /// other kind of platform clears this and sets <see cref="WindowsAttributes"/>; the two
    /// are never both reported, because no real platform reports both.
    /// </remarks>
    public UnixFileMode? UnixMode { get; set; } =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// The Windows attribute bits, when the simulation is standing in for Windows.
    /// </summary>
    public FileAttributes? WindowsAttributes { get; set; }

    /// <summary>Entries, when this is a directory.</summary>
    public Dictionary<string, FakeNode> Entries { get; } = new(StringComparer.Ordinal);

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
            throw new IOException("The simulated file cannot hold that much.");
        }

        if (length > _content.Length)
        {
            Array.Resize(ref _content, (int)Math.Max(length, Math.Min(Array.MaxLength, (long)_content.Length * 2)));
        }
    }

    /// <summary>Its description, as the platform layer would report it.</summary>
    public CapNodeInfo Info => new(Type, VolumeId, NodeId, ReparseTag);

    /// <summary>Its full description, as the metadata layer would report it.</summary>
    public CapNodeStat Stat => new(
        FileType,
        VolumeId,
        NodeId,
        Length,
        LastAccessTime,
        LastWriteTime,
        CreationTime,
        ChangeTime,
        LinkCount,
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
