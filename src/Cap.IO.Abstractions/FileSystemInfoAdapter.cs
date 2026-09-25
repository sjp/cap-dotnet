using System.IO.Abstractions;
using Cap.Primitives;
using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary>
/// What <see cref="FileInfoAdapter"/> and <see cref="DirectoryInfoAdapter"/> share: a virtual
/// path, and a description of what it names read lazily and kept until <see cref="Refresh"/>,
/// as <c>System.IO</c>'s infos keep theirs.
/// </summary>
/// <remarks>
/// <para>
/// Two spellings of the path are kept. <see cref="FullName"/> is folded lexically, as
/// <c>System.IO</c> folds it, for the caller to read. <see cref="Request"/> keeps the caller's
/// own components, spelled out against the current directory at the time the info was made,
/// and is what every operation hands to the <see cref="Dir"/>, so a <c>..</c> in it is walked
/// beneath the handle rather than folded away.
/// </para>
/// <para>
/// Access control is not supported; the members <see cref="IFileSystemAclSupport"/> declares
/// throw <see cref="NotSupportedException"/> saying why, which is what the access control
/// extension methods then report.
/// </para>
/// </remarks>
internal abstract class FileSystemInfoAdapter : IFileSystemInfo, IFileSystemAclSupport
{
    private readonly object _gate = new();
    private bool _loaded;
    private bool _exists;
    private CapMetadata _metadata;
    private CapMetadata _own;

    /// <summary>Creates an info over a caller's path, taken against the current directory now.</summary>
    protected FileSystemInfoAdapter(DirFileSystem fs, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Fs = fs;
        OriginalPath = path;
        Request request = fs.Resolve(path, nameof(path));
        Request = request.Virtual;
        FullName = fs.Paths.GetFullPath(request.Virtual, fs.CurrentDirectory);
    }

    public IFileSystem FileSystem => Fs;

    public string FullName { get; private set; }

    public string Name => Fs.Paths.LastName(FullName);

    public string Extension => System.IO.Path.GetExtension(Name);

    public abstract bool Exists { get; }

    public FileAttributes Attributes
    {
        get
        {
            Load();
            return _exists
                ? Descriptions.AttributesOf(Fs, Resolved, _own)
                : (FileAttributes)(-1);
        }

        set
        {
            if (value != Attributes)
            {
                throw Unsupported.Permissions();
            }
        }
    }

    public DateTime CreationTime
    {
        get => Time(TimeKind.Creation, utc: false);
        set => throw Unsupported.CreationTime();
    }

    public DateTime CreationTimeUtc
    {
        get => Time(TimeKind.Creation, utc: true);
        set => throw Unsupported.CreationTime();
    }

    public DateTime LastAccessTime
    {
        get => Time(TimeKind.Access, utc: false);
        set => SetTime(TimeKind.Access, value, utc: false);
    }

    public DateTime LastAccessTimeUtc
    {
        get => Time(TimeKind.Access, utc: true);
        set => SetTime(TimeKind.Access, value, utc: true);
    }

    public DateTime LastWriteTime
    {
        get => Time(TimeKind.Write, utc: false);
        set => SetTime(TimeKind.Write, value, utc: false);
    }

    public DateTime LastWriteTimeUtc
    {
        get => Time(TimeKind.Write, utc: true);
        set => SetTime(TimeKind.Write, value, utc: true);
    }

    public string? LinkTarget
    {
        get
        {
            Load();
            if (!_exists || _own.Type != CapFileType.Symlink)
            {
                return null;
            }

            return Fs.Run(Request, Expected.Any, request => Fs.Dir.ReadLink(request.Relative));
        }
    }

    public UnixFileMode UnixFileMode
    {
        get
        {
            Load();
            return _exists ? Descriptions.UnixModeOf(_metadata) : (UnixFileMode)(-1);
        }

        set => throw Unsupported.Permissions();
    }

    /// <summary>The virtual path as the caller spelled it, before folding.</summary>
    internal string Request { get; private set; }

    /// <summary>The path this info was created with, as <c>System.IO</c>'s <c>ToString</c> returns it.</summary>
    protected string OriginalPath { get; private set; }

    /// <summary>The file system this info belongs to.</summary>
    protected DirFileSystem Fs { get; }

    /// <summary>Whether the path named something the last time it was described.</summary>
    protected bool Present
    {
        get
        {
            Load();
            return _exists;
        }
    }

    /// <summary>What the path named, through a final link, the last time it was described.</summary>
    protected CapMetadata Metadata
    {
        get
        {
            Load();
            return _metadata;
        }
    }

    private Request Resolved => Fs.Paths.Resolve(Request, Fs.CurrentDirectory);

    public abstract void Delete();

    public abstract void CreateAsSymbolicLink(string pathToTarget);

    public IFileSystemInfo? ResolveLinkTarget(bool returnFinalTarget) =>
        Descriptions.ResolveLinkTarget(Fs, Request, returnFinalTarget, directory: this is DirectoryInfoAdapter);

    public void Refresh()
    {
        lock (_gate)
        {
            _loaded = false;
        }
    }

    public object GetAccessControl() => throw Unsupported.AccessControl();

    public object GetAccessControl(IFileSystemAclSupport.AccessControlSections includeSections) =>
        throw Unsupported.AccessControl();

    public void SetAccessControl(object value) => throw Unsupported.AccessControl();

    public override string ToString() => OriginalPath;

    /// <summary>Points this info at another path, as a move does in <c>System.IO</c>.</summary>
    protected void Retarget(string path)
    {
        Request request = Fs.Resolve(path, nameof(path));
        OriginalPath = path;
        Request = request.Virtual;
        FullName = Fs.Paths.GetFullPath(request.Virtual, Fs.CurrentDirectory);
        Refresh();
    }

    private void Load()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _exists = Fs.TryDescribeForExistence(Request, out _metadata);
            _own = _exists && Fs.TryDescribe(Resolved, followLink: false, out CapMetadata own) ? own : _metadata;
            _loaded = true;
        }
    }

    private DateTime Time(TimeKind kind, bool utc)
    {
        Load();
        return _exists ? Descriptions.TimeOf(_metadata, kind, utc) : Descriptions.Missing(utc);
    }

    private void SetTime(TimeKind kind, DateTime value, bool utc)
    {
        Descriptions.SetTime(Fs, Request, kind, value, utc);
        Refresh();
    }
}

/// <summary><see cref="IFileInfo"/> over a virtual path.</summary>
internal sealed class FileInfoAdapter(DirFileSystem fs, string path) : FileSystemInfoAdapter(fs, path), IFileInfo
{
    public override bool Exists => Present && Metadata.Type != CapFileType.Directory;

    public IDirectoryInfo? Directory => DirectoryName is { } parent ? new DirectoryInfoAdapter(Fs, Fs.Paths.ParentRequest(Request) ?? parent) : null;

    public string? DirectoryName => Fs.Paths.ParentRequest(FullName);

    public bool IsReadOnly
    {
        get => (Attributes & FileAttributes.ReadOnly) != 0;
        set
        {
            if (value != IsReadOnly)
            {
                throw Unsupported.Permissions();
            }
        }
    }

    public long Length => Exists ? Metadata.Length : throw Failures.FileNotFound(FullName);

    public StreamWriter AppendText() => Fs.File.AppendText(Request);

    public IFileInfo CopyTo(string destFileName) => CopyTo(destFileName, overwrite: false);

    public IFileInfo CopyTo(string destFileName, bool overwrite)
    {
        Fs.File.Copy(Request, destFileName, overwrite);
        return new FileInfoAdapter(Fs, destFileName);
    }

    public FileSystemStream Create()
    {
        FileSystemStream stream = Fs.File.Create(Request);
        Refresh();
        return stream;
    }

    public StreamWriter CreateText() => Fs.File.CreateText(Request);

    public void Decrypt() => throw Unsupported.Encryption();

    public void Encrypt() => throw Unsupported.Encryption();

    public override void Delete()
    {
        Fs.File.Delete(Request);
        Refresh();
    }

    public override void CreateAsSymbolicLink(string pathToTarget)
    {
        Fs.File.CreateSymbolicLink(Request, pathToTarget);
        Refresh();
    }

    public void MoveTo(string destFileName) => MoveTo(destFileName, overwrite: false);

    public void MoveTo(string destFileName, bool overwrite)
    {
        Fs.File.Move(Request, destFileName, overwrite);
        Retarget(destFileName);
    }

    public FileSystemStream Open(FileMode mode) => Fs.File.Open(Request, mode);

    public FileSystemStream Open(FileMode mode, FileAccess access) => Fs.File.Open(Request, mode, access);

    public FileSystemStream Open(FileMode mode, FileAccess access, FileShare share) =>
        Fs.File.Open(Request, mode, access, share);

    public FileSystemStream Open(FileStreamOptions options) => Fs.File.Open(Request, options);

    public FileSystemStream OpenRead() => Fs.File.OpenRead(Request);

    public StreamReader OpenText() => Fs.File.OpenText(Request);

    public FileSystemStream OpenWrite() => Fs.File.OpenWrite(Request);

    public IFileInfo Replace(string destinationFileName, string? destinationBackupFileName) =>
        Replace(destinationFileName, destinationBackupFileName, ignoreMetadataErrors: false);

    public IFileInfo Replace(string destinationFileName, string? destinationBackupFileName, bool ignoreMetadataErrors)
    {
        Fs.File.Replace(Request, destinationFileName, destinationBackupFileName, ignoreMetadataErrors);
        return new FileInfoAdapter(Fs, destinationFileName);
    }
}

/// <summary><see cref="IDirectoryInfo"/> over a virtual path.</summary>
internal sealed class DirectoryInfoAdapter(DirFileSystem fs, string path) : FileSystemInfoAdapter(fs, path), IDirectoryInfo
{
    public override bool Exists => Present && Metadata.Type == CapFileType.Directory;

    public IDirectoryInfo? Parent =>
        Fs.Paths.ParentRequest(FullName) is null
            ? null
            : new DirectoryInfoAdapter(Fs, Fs.Paths.ParentRequest(Request) ?? Fs.Paths.Root);

    public IDirectoryInfo Root => new DirectoryInfoAdapter(Fs, Fs.Paths.Root);

    public void Create()
    {
        Fs.Directory.CreateDirectory(Request);
        Refresh();
    }

    /// <remarks>
    /// <paramref name="path"/> is taken beneath this directory by joining it on, and the
    /// <see cref="Dir"/> resolves the result from the root. No check on the text keeps it
    /// beneath this directory, as <c>System.IO</c>'s does: containment is the root's, and a
    /// <c>..</c> that climbs out of this directory but not out of the root is allowed.
    /// </remarks>
    public IDirectoryInfo CreateSubdirectory(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Fs.Paths.IsRooted(path))
        {
            throw new ArgumentException("The path must be relative to this directory.", nameof(path));
        }

        return Fs.Directory.CreateDirectory(Fs.Paths.Join(Request, path));
    }

    public override void Delete()
    {
        Fs.Directory.Delete(Request);
        Refresh();
    }

    public void Delete(bool recursive)
    {
        Fs.Directory.Delete(Request, recursive);
        Refresh();
    }

    public override void CreateAsSymbolicLink(string pathToTarget)
    {
        Fs.Directory.CreateSymbolicLink(Request, pathToTarget);
        Refresh();
    }

    public void MoveTo(string destDirName)
    {
        Fs.Directory.Move(Request, destDirName);
        Retarget(destDirName);
    }

    public IEnumerable<IDirectoryInfo> EnumerateDirectories() => EnumerateDirectories("*");

    public IEnumerable<IDirectoryInfo> EnumerateDirectories(string searchPattern) =>
        EnumerateDirectories(searchPattern, SearchOption.TopDirectoryOnly);

    public IEnumerable<IDirectoryInfo> EnumerateDirectories(string searchPattern, SearchOption searchOption) =>
        EnumerateDirectories(searchPattern, Enumeration.Compatible(searchOption));

    public IEnumerable<IDirectoryInfo> EnumerateDirectories(string searchPattern, EnumerationOptions enumerationOptions) =>
        Infos(searchPattern, enumerationOptions, EntryKinds.Directories).Cast<IDirectoryInfo>();

    public IEnumerable<IFileInfo> EnumerateFiles() => EnumerateFiles("*");

    public IEnumerable<IFileInfo> EnumerateFiles(string searchPattern) =>
        EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly);

    public IEnumerable<IFileInfo> EnumerateFiles(string searchPattern, SearchOption searchOption) =>
        EnumerateFiles(searchPattern, Enumeration.Compatible(searchOption));

    public IEnumerable<IFileInfo> EnumerateFiles(string searchPattern, EnumerationOptions enumerationOptions) =>
        Infos(searchPattern, enumerationOptions, EntryKinds.Files).Cast<IFileInfo>();

    public IEnumerable<IFileSystemInfo> EnumerateFileSystemInfos() => EnumerateFileSystemInfos("*");

    public IEnumerable<IFileSystemInfo> EnumerateFileSystemInfos(string searchPattern) =>
        EnumerateFileSystemInfos(searchPattern, SearchOption.TopDirectoryOnly);

    public IEnumerable<IFileSystemInfo> EnumerateFileSystemInfos(string searchPattern, SearchOption searchOption) =>
        EnumerateFileSystemInfos(searchPattern, Enumeration.Compatible(searchOption));

    public IEnumerable<IFileSystemInfo> EnumerateFileSystemInfos(string searchPattern, EnumerationOptions enumerationOptions) =>
        Infos(searchPattern, enumerationOptions, EntryKinds.Both);

    public IDirectoryInfo[] GetDirectories() => [.. EnumerateDirectories()];

    public IDirectoryInfo[] GetDirectories(string searchPattern) => [.. EnumerateDirectories(searchPattern)];

    public IDirectoryInfo[] GetDirectories(string searchPattern, SearchOption searchOption) =>
        [.. EnumerateDirectories(searchPattern, searchOption)];

    public IDirectoryInfo[] GetDirectories(string searchPattern, EnumerationOptions enumerationOptions) =>
        [.. EnumerateDirectories(searchPattern, enumerationOptions)];

    public IFileInfo[] GetFiles() => [.. EnumerateFiles()];

    public IFileInfo[] GetFiles(string searchPattern) => [.. EnumerateFiles(searchPattern)];

    public IFileInfo[] GetFiles(string searchPattern, SearchOption searchOption) =>
        [.. EnumerateFiles(searchPattern, searchOption)];

    public IFileInfo[] GetFiles(string searchPattern, EnumerationOptions enumerationOptions) =>
        [.. EnumerateFiles(searchPattern, enumerationOptions)];

    public IFileSystemInfo[] GetFileSystemInfos() => [.. EnumerateFileSystemInfos()];

    public IFileSystemInfo[] GetFileSystemInfos(string searchPattern) => [.. EnumerateFileSystemInfos(searchPattern)];

    public IFileSystemInfo[] GetFileSystemInfos(string searchPattern, SearchOption searchOption) =>
        [.. EnumerateFileSystemInfos(searchPattern, searchOption)];

    public IFileSystemInfo[] GetFileSystemInfos(string searchPattern, EnumerationOptions enumerationOptions) =>
        [.. EnumerateFileSystemInfos(searchPattern, enumerationOptions)];

    private IEnumerable<IFileSystemInfo> Infos(string searchPattern, EnumerationOptions options, EntryKinds kinds)
    {
        string request = Request;
        return Enumeration.Search(Fs, request, searchPattern, options, kinds)
            .Select(found => found.IsDirectory
                ? (IFileSystemInfo)new DirectoryInfoAdapter(Fs, Fs.Paths.Join(request, found.Relative))
                : new FileInfoAdapter(Fs, Fs.Paths.Join(request, found.Relative)));
    }
}
