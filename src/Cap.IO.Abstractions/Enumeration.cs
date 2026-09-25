using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;
using Cap.Primitives;
using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary>Which entries an enumeration yields.</summary>
internal enum EntryKinds
{
    Files,
    Directories,
    Both,
}

/// <summary>An entry found by an enumeration.</summary>
/// <param name="Relative">Its path relative to the directory enumerated.</param>
/// <param name="IsDirectory">Whether it is, or through a link leads to, a directory.</param>
internal readonly record struct Found(string Relative, bool IsDirectory);

/// <summary>
/// Directory enumeration with <c>System.IO</c>'s search patterns and options, done beneath the
/// <see cref="Dir"/> one directory handle at a time.
/// </summary>
/// <remarks>
/// <para>
/// Each subdirectory is opened from the handle of the directory that lists it, by its name
/// alone, so descending never builds a path for anything to resolve. The relative paths
/// yielded are for the caller to read; nothing here resolves them again.
/// </para>
/// <para>
/// A symbolic link is reported as a directory when it leads to one inside the tree, as .NET
/// reports it, but recursion never follows one: .NET does not either, and a link can lead
/// back to a directory the walk is already inside.
/// </para>
/// </remarks>
internal static class Enumeration
{
    /// <summary>The options .NET uses for the overloads that take a <see cref="SearchOption"/>.</summary>
    public static EnumerationOptions Compatible(SearchOption searchOption) => new()
    {
        RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
        MatchType = MatchType.Win32,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    /// <summary>
    /// Finds entries beneath a directory. The directory is checked now, so a missing one is
    /// reported by the call rather than by the first step of the enumeration; it is opened
    /// again, and its entries read, as they are asked for, so an enumeration that is never
    /// run holds nothing open.
    /// </summary>
    public static IEnumerable<Found> Search(
        DirFileSystem fs, string path, string searchPattern, EnumerationOptions options, EntryKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(searchPattern);
        ArgumentNullException.ThrowIfNull(options);
        if (searchPattern.AsSpan().IndexOfAny(fs.Paths.Separator, fs.Paths.AltSeparator) >= 0)
        {
            throw new ArgumentException(
                "A search pattern here matches names within one directory and may not contain a separator. Enumerate the subdirectory instead.",
                nameof(searchPattern));
        }

        fs.Run(path, Expected.Directory, request => fs.OpenDirectory(request).Dispose());
        return Walk(fs, path, searchPattern, options, kinds);
    }

    private static IEnumerable<Found> Walk(
        DirFileSystem fs, string path, string searchPattern, EnumerationOptions options, EntryKinds kinds)
    {
        Dir top = fs.Run(path, Expected.Directory, request => fs.OpenDirectory(request));
        bool ignoreCase = options.MatchCasing switch
        {
            MatchCasing.CaseSensitive => false,
            MatchCasing.CaseInsensitive => true,
            _ => DirFileSystem.IgnoresCase,
        };
        string expression = options.MatchType == MatchType.Win32
            ? FileSystemName.TranslateWin32Expression(searchPattern)
            : searchPattern;

        Queue<(Dir Directory, string Relative, int Depth)> pending = new();
        pending.Enqueue((top, string.Empty, 0));
        try
        {
            while (pending.TryDequeue(out (Dir Directory, string Relative, int Depth) level))
            {
                using Dir directory = level.Directory;
                List<DirEntry> entries;
                try
                {
                    entries = [.. directory.EnumerateEntries()];
                }
                catch (Exception e) when (level.Depth > 0 && options.IgnoreInaccessible && e is UnauthorizedAccessException)
                {
                    continue;
                }
                catch (Exception e) when (Failures.Translate(e, path, Expected.Directory) is { } translated)
                {
                    throw translated;
                }

                foreach (DirEntry entry in entries)
                {
                    string relative = level.Relative.Length == 0 ? entry.Name : fs.Paths.Join(level.Relative, entry.Name);
                    bool isDirectory = entry.Type == CapFileType.Directory
                        || (entry.Type == CapFileType.Symlink
                            && directory.TryGetMetadata(entry.Name, followLink: true, out CapMetadata target)
                            && target.Type == CapFileType.Directory);

                    if (options.AttributesToSkip != 0 && Skips(directory, entry, isDirectory, options.AttributesToSkip))
                    {
                        continue;
                    }

                    bool wanted = kinds switch
                    {
                        EntryKinds.Files => !isDirectory,
                        EntryKinds.Directories => isDirectory,
                        _ => true,
                    };

                    bool matches = options.MatchType == MatchType.Win32
                        ? FileSystemName.MatchesWin32Expression(expression, entry.Name, ignoreCase)
                        : FileSystemName.MatchesSimpleExpression(expression, entry.Name, ignoreCase);

                    if (wanted && matches)
                    {
                        yield return new Found(relative, isDirectory);
                    }

                    if (options.RecurseSubdirectories
                        && entry.Type == CapFileType.Directory
                        && level.Depth < options.MaxRecursionDepth
                        && TryDescend(directory, entry.Name, options.IgnoreInaccessible, out Dir? child))
                    {
                        pending.Enqueue((child, relative, level.Depth + 1));
                    }
                }
            }
        }
        finally
        {
            while (pending.TryDequeue(out (Dir Directory, string Relative, int Depth) left))
            {
                left.Directory.Dispose();
            }
        }
    }

    private static bool TryDescend(Dir directory, string name, bool ignoreInaccessible, [NotNullWhen(true)] out Dir? child)
    {
        try
        {
            child = directory.OpenDir(name, noFollow: true);
            return true;
        }
        catch (UnauthorizedAccessException) when (ignoreInaccessible)
        {
            child = null;
            return false;
        }
    }

    /// <summary>Whether an entry has one of the attributes the caller asked to skip.</summary>
    private static bool Skips(Dir directory, DirEntry entry, bool isDirectory, FileAttributes skip)
    {
        FileAttributes attributes = 0;
        if (isDirectory)
        {
            attributes |= FileAttributes.Directory;
        }

        if (entry.Type == CapFileType.Symlink)
        {
            attributes |= FileAttributes.ReparsePoint;
        }

        if (directory.TryGetMetadata(entry.Name, followLink: false, out CapMetadata metadata))
        {
            if (metadata.Permissions.TryGetWindowsAttributes(out FileAttributes recorded))
            {
                attributes |= recorded;
            }
            else if (metadata.Permissions.TryGetUnixMode(out UnixFileMode mode) && (mode & UnixFileMode.UserWrite) == 0)
            {
                attributes |= FileAttributes.ReadOnly;
            }
        }

        if (!OperatingSystem.IsWindows() && entry.Name.Length > 1 && entry.Name[0] == '.')
        {
            attributes |= FileAttributes.Hidden;
        }

        return (attributes & skip) != 0;
    }
}
