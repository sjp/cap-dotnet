using System.IO.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Cap.IO.Abstractions;

/// <summary>Makes <see cref="IFileInfo"/>s over virtual paths.</summary>
internal sealed class FileInfoFactory(DirFileSystem fs) : IFileInfoFactory
{
    public IFileSystem FileSystem => fs;

    public IFileInfo New(string fileName) => new FileInfoAdapter(fs, fileName);

    public IFileInfo? Wrap(FileInfo? fileInfo) => fileInfo is null ? null : throw Unsupported.Wrap(nameof(FileInfo));
}

/// <summary>Makes <see cref="IDirectoryInfo"/>s over virtual paths.</summary>
internal sealed class DirectoryInfoFactory(DirFileSystem fs) : IDirectoryInfoFactory
{
    public IFileSystem FileSystem => fs;

    public IDirectoryInfo New(string path) => new DirectoryInfoAdapter(fs, path);

    public IDirectoryInfo? Wrap(DirectoryInfo? directoryInfo) =>
        directoryInfo is null ? null : throw Unsupported.Wrap(nameof(DirectoryInfo));
}

/// <summary>Opens streams by virtual path, with the defaults of <see cref="FileStream"/>'s constructors.</summary>
internal sealed class FileStreamFactory(DirFileSystem fs) : IFileStreamFactory
{
    private const int DefaultBufferSize = 4096;

    public IFileSystem FileSystem => fs;

    public FileSystemStream New(string path, FileMode mode) =>
        New(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite);

    public FileSystemStream New(string path, FileMode mode, FileAccess access) =>
        New(path, mode, access, FileShare.Read);

    public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share) =>
        New(path, mode, access, share, DefaultBufferSize);

    public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) =>
        New(path, mode, access, share, bufferSize, FileOptions.None);

    public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) =>
        New(path, mode, access, share, bufferSize, useAsync ? FileOptions.Asynchronous : FileOptions.None);

    public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) =>
        fs.OpenStream(path, mode, access, share, bufferSize, options);

    public FileSystemStream New(string path, FileStreamOptions options) => fs.File.Open(path, options);

    public FileSystemStream New(SafeFileHandle handle, FileAccess access) => throw Unsupported.Handle();

    public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) => throw Unsupported.Handle();

    public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) =>
        throw Unsupported.Handle();

    public FileSystemStream Wrap(FileStream fileStream) => throw Unsupported.Wrap(nameof(FileStream));
}

/// <summary>Drives: none, since the namespace is one directory handle.</summary>
internal sealed class DriveInfoFactory(DirFileSystem fs) : IDriveInfoFactory
{
    public IFileSystem FileSystem => fs;

    public IDriveInfo[] GetDrives() => throw Unsupported.Drives();

    public IDriveInfo New(string driveName) => throw Unsupported.Drives();

    public IDriveInfo? Wrap(DriveInfo? driveInfo) => throw Unsupported.Drives();
}

/// <summary>Watchers: none, since change notification cannot be confined to a handle.</summary>
internal sealed class FileSystemWatcherFactory(DirFileSystem fs) : IFileSystemWatcherFactory
{
    public IFileSystem FileSystem => fs;

    public IFileSystemWatcher New() => throw Unsupported.Watcher();

    public IFileSystemWatcher New(string path) => throw Unsupported.Watcher();

    public IFileSystemWatcher New(string path, string filter) => throw Unsupported.Watcher();

    public IFileSystemWatcher? Wrap(FileSystemWatcher? fileSystemWatcher) => throw Unsupported.Watcher();
}

/// <summary>Version information: none, since reading it opens a file by host path.</summary>
internal sealed class FileVersionInfoFactory(DirFileSystem fs) : IFileVersionInfoFactory
{
    public IFileSystem FileSystem => fs;

    public IFileVersionInfo GetVersionInfo(string fileName) => throw Unsupported.VersionInfo();
}
