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
    /// Finds entries beneath a directory. The path is resolved and the directory opened now,
    /// so a missing one is reported by the call rather than by the first step of the
    /// enumeration, and a later change of the current directory does not change which
    /// directory is walked. The handle opened by the call is the one the first enumeration
    /// reads, as <c>System.IO</c> does; an enumeration that is never run holds it until it is
    /// collected, and each later one opens the directory again.
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

        Request request = fs.Resolve(path, nameof(path));
        return new PendingSearch(fs, request, OpenTop(fs, request), searchPattern, options, kinds);
    }

    private static Dir OpenTop(DirFileSystem fs, Request request)
    {
        try
        {
            return fs.OpenDirectory(request);
        }
        catch (Exception e) when (fs.Translate(e, request, Expected.Directory) is { } translated)
        {
            throw translated;
        }
    }

    private static IEnumerable<Found> Walk(
        PendingSearch search, DirFileSystem fs, Request request, string searchPattern, EnumerationOptions options, EntryKinds kinds)
    {
        // Taken only once the enumeration starts, so an enumerator that is never moved leaves
        // the handle with the search, for its next enumerator to read.
        Dir top = search.TakeOpened() ?? OpenTop(fs, request);
        bool ignoreCase = options.MatchCasing switch
        {
            MatchCasing.CaseSensitive => false,
            MatchCasing.CaseInsensitive => true,
            _ => DirFileSystem.IgnoresCase,
        };
        string expression = options.MatchType == MatchType.Win32
            ? fs.Paths.Win32Pattern(searchPattern)
            : searchPattern;

        Queue<(Dir Directory, string Relative, int Depth)> pending = new();
        pending.Enqueue((top, string.Empty, 0));
        try
        {
            bool describesEach = options.AttributesToSkip != 0 && DescribesEach(top, options.AttributesToSkip);
            while (pending.TryDequeue(out (Dir Directory, string Relative, int Depth) level))
            {
                using Dir directory = level.Directory;
                using IEnumerator<DirEntry> entries = directory.EnumerateEntries().GetEnumerator();
                while (true)
                {
                    // Entries are read as they are asked for, so a caller that stops early has
                    // not paid for the rest of the directory.
                    DirEntry entry;
                    try
                    {
                        if (!entries.MoveNext())
                        {
                            break;
                        }

                        entry = entries.Current;
                    }
                    catch (Exception e) when (level.Depth > 0 && options.IgnoreInaccessible && e is UnauthorizedAccessException)
                    {
                        break;
                    }
                    catch (Exception e) when (Failures.Translate(e, request.Virtual, Expected.Directory) is { } translated)
                    {
                        throw translated;
                    }

                    string relative = level.Relative.Length == 0 ? entry.Name : fs.Paths.Join(level.Relative, entry.Name);
                    bool isDirectory = entry.Type == CapFileType.Directory
                        || (entry.Type == CapFileType.Symlink
                            && directory.TryGetMetadata(entry.Name, followLink: true, out CapMetadata target)
                            && target.Type == CapFileType.Directory);

                    if (options.AttributesToSkip != 0 && Skips(directory, entry, isDirectory, options.AttributesToSkip, describesEach))
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

    /// <summary>
    /// A search whose directory has been checked, holding the handle the check opened until
    /// the first enumeration takes it.
    /// </summary>
    private sealed class PendingSearch(
        DirFileSystem fs, Request request, Dir opened, string searchPattern, EnumerationOptions options, EntryKinds kinds)
        : IEnumerable<Found>
    {
        private Dir? _opened = opened;

        /// <summary>The handle opened by the call, for the first enumeration that asks for it.</summary>
        public Dir? TakeOpened() => Interlocked.Exchange(ref _opened, null);

        public IEnumerator<Found> GetEnumerator() =>
            Walk(this, fs, request, searchPattern, options, kinds).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

    /// <summary>
    /// Whether deciding which entries to skip needs each one described, rather than only its
    /// type and name.
    /// </summary>
    /// <remarks>
    /// Where the filesystem records Windows attributes any of them can be asked about, but
    /// where it records a mode the only attribute read from one is read-only. The directory
    /// itself says which kind its filesystem records, for the price of one description.
    /// </remarks>
    private static bool DescribesEach(Dir top, FileAttributes skip)
    {
        if ((skip & ~(FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
        {
            return false;
        }

        if ((skip & FileAttributes.ReadOnly) != 0)
        {
            return true;
        }

        try
        {
            return top.GetMetadata().Permissions.TryGetWindowsAttributes(out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether an entry has one of the attributes the caller asked to skip.</summary>
    private static bool Skips(Dir directory, DirEntry entry, bool isDirectory, FileAttributes skip, bool describe)
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

        if (describe && directory.TryGetMetadata(entry.Name, followLink: false, out CapMetadata metadata))
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
