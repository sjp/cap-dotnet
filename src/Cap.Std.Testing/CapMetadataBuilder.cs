using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Testing;

/// <summary>
/// Makes <see cref="CapMetadata"/> values for test doubles.
/// </summary>
/// <remarks>
/// <para>
/// <strong>For doubles only.</strong> A stub of <see cref="IDir.GetMetadata()"/>,
/// <see cref="ICapFile"/> or <see cref="IDirEntry.GetMetadata"/> has to return a
/// description, and <see cref="CapMetadata"/> has no public constructor, so this is where a
/// test gets one. Production code gets descriptions from a handle and nothing else. The
/// constructor stays closed in <c>Cap.Std</c> because a description carries an identity, and
/// <see cref="CapMetadata.IsSameFileAs"/> is only worth asking if the filesystem issued it;
/// see <see cref="TestFileIds"/>.
/// </para>
/// <para>
/// <strong>Defaults.</strong> Without any setter, <see cref="Build"/> describes an empty
/// regular file with one name: length zero, all four times at midnight UTC on
/// 1 January 2000, and an identity from <see cref="TestFileIds.Next"/>, so two builders
/// describe two different objects unless told otherwise. The permissions are what a new file
/// of the described type gets on the running platform, as a real handle would report them:
/// mode <c>0644</c> for a file, <c>0755</c> for a directory and <c>0777</c> for a symbolic link
/// on Unix; the <see cref="FileAttributes.Archive"/>, <see cref="FileAttributes.Directory"/> or
/// <see cref="FileAttributes.ReparsePoint"/> attribute on Windows. Code that branches on the
/// platform therefore takes the branch it would take against a real handle.
/// </para>
/// <para>
/// <strong>Permissions.</strong> A description holds a Unix mode or a Windows attribute set,
/// never both, because a real platform records one kind or the other. Setting one therefore
/// clears the other, and the last setter called decides.
/// </para>
/// <para>
/// Each setter returns the builder, so a description can be made in one expression. A builder
/// can be reused: <see cref="Build"/> takes a snapshot, and changing the builder afterwards
/// does not change a description already built. Instances are not safe to use from several
/// threads at once.
/// </para>
/// </remarks>
public sealed class CapMetadataBuilder
{
    /// <summary>The time every default timestamp is set to.</summary>
    private static readonly DateTimeOffset DefaultTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private CapFileType _type = CapFileType.File;
    private long _length;
    private DateTimeOffset _lastAccessTime = DefaultTime;
    private DateTimeOffset _lastWriteTime = DefaultTime;
    private DateTimeOffset? _creationTime = DefaultTime;
    private DateTimeOffset? _changeTime = DefaultTime;
    private long _linkCount = 1;
    private UnixFileMode? _unixMode;
    private FileAttributes? _windowsAttributes;
    private bool _permissionsSet;
    private CapFileId _fileId = TestFileIds.Next();

    /// <summary>Sets what the object is. Defaults to <see cref="CapFileType.File"/>.</summary>
    /// <param name="type">The object's kind.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not one the enumeration defines.</exception>
    /// <remarks>
    /// Unless permissions have been set, the default permissions follow the type, so a
    /// directory is reported with a directory's mode or attributes.
    /// </remarks>
    public CapMetadataBuilder WithType(CapFileType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "The file type is not one the enumeration defines.");
        }

        _type = type;
        return this;
    }

    /// <summary>Sets the length in bytes. Defaults to zero.</summary>
    /// <param name="length">The length.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public CapMetadataBuilder WithLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
        return this;
    }

    /// <summary>Sets when the contents were last changed.</summary>
    /// <param name="time">The time.</param>
    /// <returns>This builder.</returns>
    public CapMetadataBuilder WithLastWriteTime(DateTimeOffset time)
    {
        _lastWriteTime = time;
        return this;
    }

    /// <summary>Sets when the contents were last read.</summary>
    /// <param name="time">The time.</param>
    /// <returns>This builder.</returns>
    public CapMetadataBuilder WithLastAccessTime(DateTimeOffset time)
    {
        _lastAccessTime = time;
        return this;
    }

    /// <summary>Sets when the object was created, or null for a filesystem that does not record it.</summary>
    /// <param name="time">The time, or null.</param>
    /// <returns>This builder.</returns>
    public CapMetadataBuilder WithCreationTime(DateTimeOffset? time)
    {
        _creationTime = time;
        return this;
    }

    /// <summary>Sets when anything about the object last changed, or null for a filesystem that does not record it.</summary>
    /// <param name="time">The time, or null.</param>
    /// <returns>This builder.</returns>
    public CapMetadataBuilder WithChangeTime(DateTimeOffset? time)
    {
        _changeTime = time;
        return this;
    }

    /// <summary>Sets how many directory entries refer to the object. Defaults to one.</summary>
    /// <param name="linkCount">The count; zero describes an object whose last name has been removed.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="linkCount"/> is negative.</exception>
    public CapMetadataBuilder WithLinkCount(long linkCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(linkCount);
        _linkCount = linkCount;
        return this;
    }

    /// <summary>
    /// Makes the description report a Unix mode, and no Windows attributes, whatever the
    /// running platform.
    /// </summary>
    /// <param name="mode">The mode bits.</param>
    /// <returns>This builder.</returns>
    public CapMetadataBuilder WithUnixMode(UnixFileMode mode)
    {
        _unixMode = mode;
        _windowsAttributes = null;
        _permissionsSet = true;
        return this;
    }

    /// <summary>
    /// Makes the description report Windows attributes, and no Unix mode, whatever the
    /// running platform.
    /// </summary>
    /// <param name="attributes">The attribute bits.</param>
    /// <returns>This builder.</returns>
    public CapMetadataBuilder WithWindowsAttributes(FileAttributes attributes)
    {
        _windowsAttributes = attributes;
        _unixMode = null;
        _permissionsSet = true;
        return this;
    }

    /// <summary>
    /// Sets the object's identity. Defaults to a fresh one from <see cref="TestFileIds.Next"/>.
    /// </summary>
    /// <param name="fileId">The identity, from <see cref="TestFileIds"/> or from another description.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Two descriptions given the same identity answer true to
    /// <see cref="CapMetadata.IsSameFileAs"/>, which is how a test describes one object
    /// reached under two names.
    /// </remarks>
    public CapMetadataBuilder WithFileId(CapFileId fileId)
    {
        _fileId = fileId;
        return this;
    }

    /// <summary>Makes a description from what has been set so far.</summary>
    /// <returns>The description.</returns>
    public CapMetadata Build()
    {
        UnixFileMode? unixMode = _unixMode;
        FileAttributes? windowsAttributes = _windowsAttributes;
        if (!_permissionsSet)
        {
            if (OperatingSystem.IsWindows())
            {
                windowsAttributes = DefaultAttributes(_type);
            }
            else
            {
                unixMode = DefaultMode(_type);
            }
        }

        return new CapMetadata(new CapNodeStat(
            _type,
            _fileId.VolumeId,
            _fileId.NodeId,
            _length,
            _lastAccessTime,
            _lastWriteTime,
            _creationTime,
            _changeTime,
            _linkCount,
            unixMode,
            windowsAttributes));
    }

    private static UnixFileMode DefaultMode(CapFileType type) => type switch
    {
        CapFileType.Directory => (UnixFileMode)0x1ED, // 0755
        CapFileType.Symlink => (UnixFileMode)0x1FF, // 0777
        _ => (UnixFileMode)0x1A4, // 0644
    };

    private static FileAttributes DefaultAttributes(CapFileType type) => type switch
    {
        CapFileType.Directory => FileAttributes.Directory,
        CapFileType.Symlink => FileAttributes.ReparsePoint,
        _ => FileAttributes.Archive,
    };
}
