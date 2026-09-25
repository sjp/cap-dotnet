using System.IO.Abstractions;
using Cap.Primitives;
using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary><see cref="IDirectory"/> over a <see cref="DirFileSystem"/>'s directory.</summary>
/// <remarks>
/// Enumeration returns paths as <c>System.IO</c> does: the path the caller passed, joined to
/// each entry's path beneath it. They are virtual paths, meaningful only to this adapter.
/// </remarks>
internal sealed class DirectoryAdapter(DirFileSystem fs) : IDirectory
{
    private const string MatchAll = "*";

    public IFileSystem FileSystem => fs;

    // --- Creating and removing --------------------------------------------------------------

    public IDirectoryInfo CreateDirectory(string path)
    {
        fs.Run(path, Expected.Directory, request => fs.CreateDirectoryChain(request));
        return new DirectoryInfoAdapter(fs, path);
    }

    public IDirectoryInfo CreateDirectory(string path, UnixFileMode unixCreateMode) => throw Unsupported.CreateMode();

    public IFileSystemInfo CreateSymbolicLink(string path, string pathToTarget)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathToTarget);
        fs.Run(path, Expected.Any, request =>
        {
            if (request.IsRoot)
            {
                throw Failures.RootIsFixed(request.Virtual);
            }

            fs.Dir.CreateDirSymlink(request.Relative, pathToTarget);
        });

        return new DirectoryInfoAdapter(fs, path);
    }

    /// <remarks>
    /// Made inside the virtual scratch directory, <c>.tmp</c> beneath the root, with a name
    /// drawn by <see cref="CapTempDir"/> and <paramref name="prefix"/> put in front of it.
    /// </remarks>
    public IDirectoryInfo CreateTempSubdirectory(string? prefix = null)
    {
        if (prefix is not null && prefix.AsSpan().IndexOfAny(fs.Paths.Separator, fs.Paths.AltSeparator) >= 0)
        {
            throw new ArgumentException("The prefix may not contain a separator.", nameof(prefix));
        }

        using Dir scratch = fs.OpenTempDirectory();
        using CapTempDir made = CapTempDir.NewIn(scratch);
        made.Keep();

        string name = made.Name;
        if (!string.IsNullOrEmpty(prefix))
        {
            string prefixed = string.Concat(prefix, name);
            scratch.Rename(name, scratch, prefixed);
            name = prefixed;
        }

        return new DirectoryInfoAdapter(fs, fs.Paths.Join(fs.Paths.TempPath, name));
    }

    public void Delete(string path) => fs.DeleteDirectory(path, recursive: false);

    public void Delete(string path, bool recursive) => fs.DeleteDirectory(path, recursive);

    public void Move(string sourceDirName, string destDirName)
    {
        Request source = fs.Resolve(sourceDirName, nameof(sourceDirName));
        Request destination = fs.Resolve(destDirName, nameof(destDirName));
        try
        {
            if (source.IsRoot)
            {
                throw Failures.RootIsFixed(source.Virtual);
            }

            if (destination.IsRoot)
            {
                throw Failures.RootIsFixed(destination.Virtual);
            }

            fs.Dir.Rename(source.Relative, fs.Dir, destination.Relative, replaceExisting: false);
        }
        catch (Exception e) when (fs.Translate(e, source, Expected.Directory) is { } translated)
        {
            throw translated;
        }
    }

    // --- Existence and links ----------------------------------------------------------------

    public bool Exists(string? path) =>
        fs.TryDescribeForExistence(path, out CapMetadata metadata) && metadata.Type == CapFileType.Directory;

    public IFileSystemInfo? ResolveLinkTarget(string linkPath, bool returnFinalTarget) =>
        Descriptions.ResolveLinkTarget(fs, linkPath, returnFinalTarget, directory: true);

    // --- Enumeration ------------------------------------------------------------------------

    public IEnumerable<string> EnumerateDirectories(string path) =>
        EnumerateDirectories(path, MatchAll);

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern) =>
        EnumerateDirectories(path, searchPattern, SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) =>
        EnumerateDirectories(path, searchPattern, Enumeration.Compatible(searchOption));

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions enumerationOptions) =>
        Paths(path, searchPattern, enumerationOptions, EntryKinds.Directories);

    public IEnumerable<string> EnumerateFiles(string path) =>
        EnumerateFiles(path, MatchAll);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern) =>
        EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) =>
        EnumerateFiles(path, searchPattern, Enumeration.Compatible(searchOption));

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions enumerationOptions) =>
        Paths(path, searchPattern, enumerationOptions, EntryKinds.Files);

    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        EnumerateFileSystemEntries(path, MatchAll);

    public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern) =>
        EnumerateFileSystemEntries(path, searchPattern, SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, SearchOption searchOption) =>
        EnumerateFileSystemEntries(path, searchPattern, Enumeration.Compatible(searchOption));

    public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, EnumerationOptions enumerationOptions) =>
        Paths(path, searchPattern, enumerationOptions, EntryKinds.Both);

    public string[] GetDirectories(string path) => [.. EnumerateDirectories(path)];

    public string[] GetDirectories(string path, string searchPattern) => [.. EnumerateDirectories(path, searchPattern)];

    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) =>
        [.. EnumerateDirectories(path, searchPattern, searchOption)];

    public string[] GetDirectories(string path, string searchPattern, EnumerationOptions enumerationOptions) =>
        [.. EnumerateDirectories(path, searchPattern, enumerationOptions)];

    public string[] GetFiles(string path) => [.. EnumerateFiles(path)];

    public string[] GetFiles(string path, string searchPattern) => [.. EnumerateFiles(path, searchPattern)];

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption) =>
        [.. EnumerateFiles(path, searchPattern, searchOption)];

    public string[] GetFiles(string path, string searchPattern, EnumerationOptions enumerationOptions) =>
        [.. EnumerateFiles(path, searchPattern, enumerationOptions)];

    public string[] GetFileSystemEntries(string path) => [.. EnumerateFileSystemEntries(path)];

    public string[] GetFileSystemEntries(string path, string searchPattern) =>
        [.. EnumerateFileSystemEntries(path, searchPattern)];

    public string[] GetFileSystemEntries(string path, string searchPattern, SearchOption searchOption) =>
        [.. EnumerateFileSystemEntries(path, searchPattern, searchOption)];

    public string[] GetFileSystemEntries(string path, string searchPattern, EnumerationOptions enumerationOptions) =>
        [.. EnumerateFileSystemEntries(path, searchPattern, enumerationOptions)];

    // --- The namespace ----------------------------------------------------------------------

    /// <remarks>
    /// The current directory is this instance's own, set only by this member and never taken
    /// from or given to the process. It is checked to be a directory when it is set, and
    /// kept as the caller spelled it, so that a relative path taken against it reaches the
    /// <see cref="Dir"/> with the caller's own components.
    /// </remarks>
    public void SetCurrentDirectory(string path) =>
        fs.CurrentDirectory = fs.Run(path, Expected.Directory, request =>
        {
            fs.OpenDirectory(request).Dispose();
            return request.Virtual;
        });

    public string GetCurrentDirectory() => fs.Paths.GetFullPath(fs.CurrentDirectory, fs.Paths.Root);

    public string GetDirectoryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return fs.Paths.GetPathRoot(fs.Paths.GetFullPath(path, fs.CurrentDirectory)) ?? fs.Paths.Root;
    }

    public string[] GetLogicalDrives() => [fs.Paths.Root];

    public IDirectoryInfo? GetParent(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        DirectoryInfoAdapter info = new(fs, path);
        return info.Parent;
    }

    // --- Times ------------------------------------------------------------------------------

    public DateTime GetCreationTime(string path) => Descriptions.GetTime(fs, path, TimeKind.Creation, utc: false);

    public DateTime GetCreationTimeUtc(string path) => Descriptions.GetTime(fs, path, TimeKind.Creation, utc: true);

    public DateTime GetLastAccessTime(string path) => Descriptions.GetTime(fs, path, TimeKind.Access, utc: false);

    public DateTime GetLastAccessTimeUtc(string path) => Descriptions.GetTime(fs, path, TimeKind.Access, utc: true);

    public DateTime GetLastWriteTime(string path) => Descriptions.GetTime(fs, path, TimeKind.Write, utc: false);

    public DateTime GetLastWriteTimeUtc(string path) => Descriptions.GetTime(fs, path, TimeKind.Write, utc: true);

    public void SetCreationTime(string path, DateTime creationTime) => throw Unsupported.CreationTime();

    public void SetCreationTimeUtc(string path, DateTime creationTimeUtc) => throw Unsupported.CreationTime();

    public void SetLastAccessTime(string path, DateTime lastAccessTime) =>
        Descriptions.SetTime(fs, path, TimeKind.Access, lastAccessTime, utc: false);

    public void SetLastAccessTimeUtc(string path, DateTime lastAccessTimeUtc) =>
        Descriptions.SetTime(fs, path, TimeKind.Access, lastAccessTimeUtc, utc: true);

    public void SetLastWriteTime(string path, DateTime lastWriteTime) =>
        Descriptions.SetTime(fs, path, TimeKind.Write, lastWriteTime, utc: false);

    public void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) =>
        Descriptions.SetTime(fs, path, TimeKind.Write, lastWriteTimeUtc, utc: true);

    private IEnumerable<string> Paths(string path, string searchPattern, EnumerationOptions options, EntryKinds kinds)
    {
        IEnumerable<Found> found = Enumeration.Search(fs, path, searchPattern, options, kinds);
        return found.Select(entry => fs.Paths.Join(path, entry.Relative));
    }
}
