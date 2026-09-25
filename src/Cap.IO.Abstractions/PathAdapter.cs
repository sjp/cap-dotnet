using System.IO.Abstractions;
using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary><see cref="IPath"/> in a <see cref="DirFileSystem"/>'s virtual namespace.</summary>
/// <remarks>
/// <para>
/// The members that are string functions, such as <c>Combine</c>, <c>GetFileName</c> and
/// <c>GetExtension</c>, are the platform's own. The members that depend on where the root
/// is, such as <c>IsPathFullyQualified</c>, <c>GetPathRoot</c>, <c>GetFullPath</c> and
/// <c>GetRelativePath</c>, answer for the virtual namespace, and never consult the host's
/// working directory.
/// </para>
/// <para>
/// <c>GetFullPath</c> folds <c>..</c> as text, as <c>System.IO</c> does. What it returns
/// names a request, not a location: when a component before a <c>..</c> is a symbolic link,
/// the folded path and the original lead to different places. Pass the original on when that
/// matters.
/// </para>
/// </remarks>
internal sealed class PathAdapter(DirFileSystem fs) : IPath
{
    public IFileSystem FileSystem => fs;

    public char AltDirectorySeparatorChar => fs.Paths.AltSeparator;

    public char DirectorySeparatorChar => fs.Paths.Separator;

    public char PathSeparator => Path.PathSeparator;

    public char VolumeSeparatorChar => Path.VolumeSeparatorChar;

    // --- String functions, as the platform's ------------------------------------------------

    public string? ChangeExtension(string? path, string? extension) => Path.ChangeExtension(path, extension);

    public string Combine(string path1, string path2) => Path.Combine(path1, path2);

    public string Combine(string path1, string path2, string path3) => Path.Combine(path1, path2, path3);

    public string Combine(string path1, string path2, string path3, string path4) =>
        Path.Combine(path1, path2, path3, path4);

    public string Combine(params string[] paths) => Path.Combine(paths);

    public string Combine(params ReadOnlySpan<string> paths) => Path.Combine(paths);

    public bool EndsInDirectorySeparator(ReadOnlySpan<char> path) => Path.EndsInDirectorySeparator(path);

    public bool EndsInDirectorySeparator(string path) => Path.EndsInDirectorySeparator(path);

    public ReadOnlySpan<char> GetDirectoryName(ReadOnlySpan<char> path) => Path.GetDirectoryName(path);

    public string? GetDirectoryName(string? path) => Path.GetDirectoryName(path);

    public ReadOnlySpan<char> GetExtension(ReadOnlySpan<char> path) => Path.GetExtension(path);

    public string? GetExtension(string? path) => Path.GetExtension(path);

    public ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path) => Path.GetFileName(path);

    public string? GetFileName(string? path) => Path.GetFileName(path);

    public ReadOnlySpan<char> GetFileNameWithoutExtension(ReadOnlySpan<char> path) =>
        Path.GetFileNameWithoutExtension(path);

    public string? GetFileNameWithoutExtension(string? path) => Path.GetFileNameWithoutExtension(path);

    public char[] GetInvalidFileNameChars() => Path.GetInvalidFileNameChars();

    public char[] GetInvalidPathChars() => Path.GetInvalidPathChars();

    public bool HasExtension(ReadOnlySpan<char> path) => Path.HasExtension(path);

    public bool HasExtension(string? path) => Path.HasExtension(path);

    public string Join(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2) => Path.Join(path1, path2);

    public string Join(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2, ReadOnlySpan<char> path3) =>
        Path.Join(path1, path2, path3);

    public string Join(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2, ReadOnlySpan<char> path3, ReadOnlySpan<char> path4) =>
        Path.Join(path1, path2, path3, path4);

    public string Join(string? path1, string? path2) => Path.Join(path1, path2);

    public string Join(string? path1, string? path2, string? path3) => Path.Join(path1, path2, path3);

    public string Join(string? path1, string? path2, string? path3, string? path4) =>
        Path.Join(path1, path2, path3, path4);

    public string Join(params string?[] paths) => Path.Join(paths);

    public string Join(params ReadOnlySpan<string?> paths) => Path.Join(paths);

    public ReadOnlySpan<char> TrimEndingDirectorySeparator(ReadOnlySpan<char> path) =>
        Path.TrimEndingDirectorySeparator(path);

    public string TrimEndingDirectorySeparator(string path) => Path.TrimEndingDirectorySeparator(path);

    public bool TryJoin(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2, Span<char> destination, out int charsWritten) =>
        Path.TryJoin(path1, path2, destination, out charsWritten);

    public bool TryJoin(
        ReadOnlySpan<char> path1,
        ReadOnlySpan<char> path2,
        ReadOnlySpan<char> path3,
        Span<char> destination,
        out int charsWritten) =>
        Path.TryJoin(path1, path2, path3, destination, out charsWritten);

    // --- The virtual namespace --------------------------------------------------------------

    public bool Exists(string? path) => fs.TryDescribeForExistence(path, out _);

    public string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return fs.Paths.GetFullPath(path, fs.CurrentDirectory);
    }

    public string GetFullPath(string path, string basePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(basePath);
        if (!fs.Paths.IsFullyQualified(basePath))
        {
            throw new ArgumentException("The base path must be fully qualified in this file system.", nameof(basePath));
        }

        return path.Length == 0 ? basePath : fs.Paths.GetFullPath(path, basePath);
    }

    public ReadOnlySpan<char> GetPathRoot(ReadOnlySpan<char> path) =>
        fs.Paths.GetPathRoot(path) is { } root ? root.AsSpan() : Path.GetPathRoot(path);

    public string? GetPathRoot(string? path) =>
        path is null ? null : fs.Paths.GetPathRoot(path) ?? Path.GetPathRoot(path);

    public string GetRelativePath(string relativeTo, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativeTo);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return fs.Paths.GetRelativePath(
            relativeTo,
            path,
            fs.CurrentDirectory,
            DirFileSystem.IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public bool IsPathFullyQualified(ReadOnlySpan<char> path) => fs.Paths.IsFullyQualified(path);

    public bool IsPathFullyQualified(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return fs.Paths.IsFullyQualified(path);
    }

    public bool IsPathRooted(ReadOnlySpan<char> path) => fs.Paths.IsRooted(path);

    public bool IsPathRooted(string? path) => path is not null && fs.Paths.IsRooted(path);

    // --- Scratch space ----------------------------------------------------------------------

    public string GetRandomFileName() => throw Unsupported.RandomFileName();

    /// <remarks>
    /// Creates an empty file in the virtual scratch directory, <c>.tmp</c> beneath the root,
    /// with a name drawn by <see cref="CapTempFile"/>, and returns its virtual path.
    /// </remarks>
    public string GetTempFileName()
    {
        using Dir scratch = fs.OpenTempDirectory();
        using CapTempFile made = CapTempFile.New(scratch);
        made.Keep();
        return fs.Paths.Join(fs.Paths.TempPath, made.Name!);
    }

    /// <remarks>
    /// The virtual scratch directory, <c>.tmp</c> beneath the root, created the first time it
    /// is asked for. It is part of the tree the file system was given, so what is put there
    /// can be seen by whatever else can see that tree, and is not cleared away.
    /// </remarks>
    public string GetTempPath()
    {
        fs.OpenTempDirectory().Dispose();
        return fs.Paths.TempPath;
    }
}
