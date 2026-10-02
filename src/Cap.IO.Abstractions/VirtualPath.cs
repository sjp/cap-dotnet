using System.Text;

namespace Cap.IO.Abstractions;

/// <summary>
/// The syntax of the virtual namespace a <see cref="DirFileSystem"/> presents: where its root
/// is, what separates components, and how a caller's string becomes the path handed to the
/// <see cref="Cap.Std.Dir"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only file in the assembly that handles separators.</strong> Code
/// written against <c>IFileSystem</c> passes absolute paths, relative paths against a current
/// directory, and expects <c>FullName</c> strings back, so the adapter cannot avoid some text
/// work. All of it is kept here, where it can be read in one sitting, and every other file is
/// held to the rule the rest of the library is held to: no literal in it contains a separator.
/// </para>
/// <para>
/// <strong>What the text work is not.</strong> Nothing here decides containment, and nothing
/// here that feeds the <see cref="Cap.Std.Dir"/> rewrites the caller's components. A virtual
/// absolute path loses its root prefix and nothing else. A relative one is joined onto the
/// current directory, which only spells out the caller's request in full. <c>..</c>, links and
/// every other component reach the <see cref="Cap.Std.Dir"/> as written, and it is resolution
/// beneath the handle that refuses what would leave. Folding <c>a/../b</c> into <c>b</c> would
/// be wrong whenever <c>a</c> is a symbolic link, which is why the lexical
/// <see cref="GetFullPath(string, string)"/> below is used only to produce display strings and
/// never to produce a request.
/// </para>
/// <para>
/// The syntax is chosen by the constructor rather than read from the running system, so the
/// Windows rules can be exercised on any machine.
/// </para>
/// </remarks>
internal sealed class VirtualPath
{
    /// <summary>The name of the scratch directory created beneath the root on demand.</summary>
    public const string TempDirectoryName = ".tmp";

    private readonly bool _windows;
    private readonly char? _drive;

    /// <summary>Creates the syntax for a root spelled <c>/</c>, or as a drive.</summary>
    /// <param name="drive">
    /// An upper-case ASCII letter to spell the root as that drive, or null to spell it
    /// <c>/</c>.
    /// </param>
    /// <param name="windows">
    /// Whether <c>\</c> also separates components and drive-letter paths are recognised as
    /// rooted, as on Windows.
    /// </param>
    public VirtualPath(char? drive, bool windows)
    {
        _windows = windows;
        _drive = drive;
        if (drive is char letter)
        {
            Separator = '\\';
            Root = string.Concat(letter.ToString(), ":\\");
        }
        else
        {
            Separator = '/';
            Root = "/";
        }
    }

    /// <summary>The virtual root: <c>/</c>, or a drive such as <c>C:\</c>.</summary>
    public string Root { get; }

    /// <summary>The separator this namespace writes.</summary>
    public char Separator { get; }

    /// <summary>The other separator this namespace reads, where there is one.</summary>
    public char AltSeparator => _windows ? (Separator == '/' ? '\\' : '/') : '/';

    /// <summary>Whether a character separates components in this namespace.</summary>
    public bool IsSeparator(char c) => c == '/' || (_windows && c == '\\');

    /// <summary>Whether a path begins with the virtual root.</summary>
    public bool IsFullyQualified(ReadOnlySpan<char> path) => RootLength(path) > 0;

    /// <summary>
    /// Whether a path is rooted somewhere: at the virtual root, or in some way the host would
    /// read as rooted, such as another drive.
    /// </summary>
    public bool IsRooted(ReadOnlySpan<char> path) => RootLength(path) > 0 || IsForeignRooted(path);

    /// <summary>
    /// The path the caller is asking for, spelled out in full, and the part of it the
    /// <see cref="Cap.Std.Dir"/> is given.
    /// </summary>
    /// <param name="path">What the caller passed.</param>
    /// <param name="currentDirectory">The adapter's current directory, a virtual absolute path.</param>
    /// <returns>
    /// The virtual absolute request and the <see cref="Cap.Std.Dir"/>-relative remainder. A
    /// path rooted somewhere other than the virtual root is passed on untouched, so that the
    /// <see cref="Cap.Std.Dir"/> refuses it as the absolute path it is.
    /// </returns>
    public Request Resolve(string path, string currentDirectory)
    {
        int root = RootLength(path);
        if (root > 0)
        {
            return new Request(path, path[root..], NamesRoot(path.AsSpan(root)));
        }

        if (IsForeignRooted(path))
        {
            return new Request(path, path, false);
        }

        string joined = Join(currentDirectory, path);
        int joinedRoot = RootLength(joined);
        return new Request(joined, joined[joinedRoot..], NamesRoot(joined.AsSpan(joinedRoot)));
    }

    /// <summary>Joins a name, or a relative path, onto a path, adding a separator if needed.</summary>
    /// <remarks>
    /// When both sides carry a separator at the seam, one of them is dropped, so that
    /// <c>Join("/", "/b")</c> spells the <c>/b</c> the caller meant rather than a <c>//b</c> that
    /// <see cref="RootLength"/> would leave rooted. Only one is dropped: a <c>//x</c> joined on
    /// still carries the doubled separator the caller wrote.
    /// </remarks>
    public string Join(string path, string name)
    {
        if (path.Length == 0)
        {
            return name;
        }

        if (name.Length == 0)
        {
            return path;
        }

        if (IsSeparator(path[^1]) && IsSeparator(name[0]))
        {
            return string.Concat(path, name.AsSpan(1));
        }

        return IsSeparator(path[^1]) || IsSeparator(name[0])
            ? string.Concat(path, name)
            : string.Concat(path, Separator.ToString(), name);
    }

    /// <summary>
    /// Combines paths as <see cref="Path.Combine(string[])"/> does, writing this namespace's
    /// separator between them.
    /// </summary>
    /// <remarks>
    /// A rooted part discards everything before it, and an empty one is skipped. Delegating to
    /// the platform's own combine would write the host's separator, which is not this
    /// namespace's when the root is <c>/</c> on Windows, and the result would then disagree
    /// with every path the adapter hands back.
    /// </remarks>
    public string Combine(ReadOnlySpan<string> paths)
    {
        foreach (string path in paths)
        {
            ArgumentNullException.ThrowIfNull(path, nameof(paths));
        }

        string combined = string.Empty;
        foreach (string path in paths)
        {
            if (path.Length > 0)
            {
                combined = IsRooted(path) ? path : Join(combined, path);
            }
        }

        return combined;
    }

    /// <summary>
    /// Joins paths as <see cref="Path.Join(string?[])"/> does, writing this namespace's
    /// separator between them.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Combine"/>, a rooted part is joined on like any other, and null or
    /// empty parts are skipped. Unlike <see cref="Path.Join(string?[])"/>, a separator on both
    /// sides of a seam is written once; see <see cref="Join(string, string)"/>.
    /// </remarks>
    public string JoinAll(ReadOnlySpan<string?> paths)
    {
        string joined = string.Empty;
        foreach (string? path in paths)
        {
            if (!string.IsNullOrEmpty(path))
            {
                joined = Join(joined, path);
            }
        }

        return joined;
    }

    /// <summary>
    /// Every leading portion of a <see cref="Cap.Std.Dir"/>-relative path that ends in an
    /// ordinary name, shortest first, each a slice of the caller's own string.
    /// </summary>
    /// <remarks>
    /// Creating a chain of directories creates each of these in turn. A portion ending in
    /// <c>.</c> or <c>..</c> names a directory that must already be there, so it is skipped;
    /// the next portion still carries it, and the <see cref="Cap.Std.Dir"/> walks it.
    /// </remarks>
    public IEnumerable<string> CreatablePrefixes(string relative)
    {
        int start = 0;
        for (int i = 0; i <= relative.Length; i++)
        {
            if (i < relative.Length && !IsSeparator(relative[i]))
            {
                continue;
            }

            ReadOnlySpan<char> component = relative.AsSpan(start, i - start);
            if (component.Length > 0 && component is not "." and not "..")
            {
                yield return relative[..i];
            }

            start = i + 1;
        }
    }

    /// <summary>
    /// Whether a leading portion of a <see cref="Cap.Std.Dir"/>-relative path, as
    /// <see cref="CreatablePrefixes"/> gives it, ends in the path's last name: nothing but
    /// separators and <c>.</c> follow it.
    /// </summary>
    /// <remarks>
    /// A <c>..</c> after it means the name was only passed through, as <c>a</c> is in
    /// <c>a/..</c>.
    /// </remarks>
    public bool EndsInLastName(string relative, string prefix)
    {
        int start = prefix.Length;
        for (int i = start; i <= relative.Length; i++)
        {
            if (i < relative.Length && !IsSeparator(relative[i]))
            {
                continue;
            }

            if (relative.AsSpan(start, i - start) is not "" and not ".")
            {
                return false;
            }

            start = i + 1;
        }

        return true;
    }

    /// <summary>
    /// The request for the directory that holds a request's last name: the caller's string
    /// with that name sliced off.
    /// </summary>
    /// <returns>
    /// Null when the request is the root. A request that ends in <c>.</c> or <c>..</c> has no
    /// name to slice off, so the parent is asked for with a further <c>..</c>, and it is the
    /// <see cref="Cap.Std.Dir"/> that works out where that is.
    /// </returns>
    public string? ParentRequest(string request)
    {
        int end = request.Length;
        while (end > 0 && IsSeparator(request[end - 1]))
        {
            end--;
        }

        int root = RootLength(request);
        if (end <= root)
        {
            return null;
        }

        int slash = end - 1;
        while (slash >= 0 && !IsSeparator(request[slash]))
        {
            slash--;
        }

        ReadOnlySpan<char> last = request.AsSpan(slash + 1, end - slash - 1);
        if (last is "." or "..")
        {
            return Join(request[..end], "..");
        }

        if (slash < 0)
        {
            return null;
        }

        return slash + 1 <= root ? request[..root] : request[..slash];
    }

    /// <summary>The last name in a path, ignoring trailing separators; the root for the root.</summary>
    public string LastName(string path)
    {
        int end = path.Length;
        while (end > 0 && IsSeparator(path[end - 1]))
        {
            end--;
        }

        if (end <= RootLength(path))
        {
            return path.Length == 0 ? path : path[..Math.Max(RootLength(path), 1)];
        }

        int slash = path.LastIndexOfAny(Separators(), end - 1);
        return path[(slash + 1)..end];
    }

    /// <summary>
    /// The directory part of a path as <see cref="Path.GetDirectoryName(string)"/> gives it,
    /// with separators written as this namespace writes them.
    /// </summary>
    /// <remarks>
    /// The platform's own collapses doubled separators in what it returns and, on Windows,
    /// rewrites <c>/</c> as <c>\</c>, which is not this namespace's separator when the root is
    /// <c>/</c>; the result would then disagree with every <c>FullName</c> the adapter hands
    /// back. This one does the same tidying but writes <see cref="Separator"/>. A trailing
    /// separator is not trimmed first, so <c>/a/b/</c> gives <c>/a/b</c>, as it does there. A
    /// path rooted outside the namespace is left to the platform, whose spelling it already is.
    /// </remarks>
    public string? GetDirectoryName(string? path)
    {
        if (path is null || IsEffectivelyEmpty(path))
        {
            return null;
        }

        if (RootLength(path) == 0 && IsForeignRooted(path))
        {
            return Path.GetDirectoryName(path);
        }

        int end = DirectoryNameEnd(path);
        return end < 0 ? null : NormalizeSeparators(path.AsSpan(0, end));
    }

    /// <summary>
    /// The directory part of a path as <see cref="Path.GetDirectoryName(ReadOnlySpan{char})"/>
    /// gives it: a slice of the caller's own characters, with the root found as this namespace
    /// finds it.
    /// </summary>
    public ReadOnlySpan<char> GetDirectoryName(ReadOnlySpan<char> path)
    {
        if (IsEffectivelyEmpty(path))
        {
            return [];
        }

        if (RootLength(path) == 0 && IsForeignRooted(path))
        {
            return Path.GetDirectoryName(path);
        }

        int end = DirectoryNameEnd(path);
        return end < 0 ? [] : path[..end];
    }

    /// <summary>
    /// A path made absolute against a base and folded lexically: <c>.</c> and empty
    /// components dropped, and each <c>..</c> removing the name before it, never climbing
    /// above the root.
    /// </summary>
    /// <remarks>
    /// This is what <c>FullName</c> and <c>GetFullPath</c> report, and it names a request, not
    /// a location: when a component before a <c>..</c> is a symbolic link, the folded string
    /// and the caller's own lead to different places. It is never handed to the
    /// <see cref="Cap.Std.Dir"/>. A path rooted outside the virtual namespace is returned
    /// unchanged, since there is nothing in this namespace for it to be folded against.
    /// </remarks>
    public string GetFullPath(string path, string basePath)
    {
        string absolute;
        if (RootLength(path) > 0)
        {
            absolute = path;
        }
        else if (IsForeignRooted(path))
        {
            return path;
        }
        else
        {
            absolute = Join(basePath, path);
        }

        List<string> names = Components(absolute);
        if (names.Count == 0)
        {
            return Root;
        }

        string folded = string.Concat(Root, string.Join(Separator, names));
        return absolute.Length > 0 && IsSeparator(absolute[^1])
            ? string.Concat(folded, Separator.ToString())
            : folded;
    }

    /// <summary>
    /// The lexical relative path from one path to another, both first made absolute against
    /// the current directory and folded as <see cref="GetFullPath(string, string)"/> folds.
    /// </summary>
    public string GetRelativePath(string relativeTo, string path, string currentDirectory, StringComparison comparison)
    {
        string from = GetFullPath(relativeTo, currentDirectory);
        string to = GetFullPath(path, currentDirectory);
        if (RootLength(from) == 0 || RootLength(to) == 0)
        {
            return path;
        }

        List<string> fromNames = Components(from);
        List<string> toNames = Components(to);

        int common = 0;
        while (common < fromNames.Count && common < toNames.Count
            && string.Equals(fromNames[common], toNames[common], comparison))
        {
            common++;
        }

        List<string> steps = [];
        for (int i = common; i < fromNames.Count; i++)
        {
            steps.Add("..");
        }

        steps.AddRange(toNames.Skip(common));
        if (steps.Count == 0)
        {
            return ".";
        }

        // System.IO carries a trailing separator over only when some of the target is left
        // after the common part: "/a" to "/a/b/" is "b/", but "/a/b" to "/a/" is "..".
        string relative = string.Join(Separator, steps);
        return toNames.Count > common && path.Length > 0 && IsSeparator(path[^1])
            ? string.Concat(relative, Separator.ToString())
            : relative;
    }

    /// <summary>The virtual scratch directory, spelled as a directory.</summary>
    public string TempPath => string.Concat(Root, TempDirectoryName, Separator.ToString());

    /// <summary>The root prefix of a path, if it is fully qualified in this namespace.</summary>
    public string? GetPathRoot(ReadOnlySpan<char> path)
    {
        int root = RootLength(path);
        return root > 0 ? path[..root].ToString() : null;
    }

    /// <summary>
    /// Whether a <see cref="Cap.Std.Dir"/>-relative remainder is made only of <c>.</c> and
    /// empty components, and so names the root itself.
    /// </summary>
    /// <remarks>
    /// The <see cref="Cap.Std.Dir"/> refuses an empty path, so the adapter answers for the
    /// root from the handle it holds. This recognises exactly the spellings that are the root
    /// without a walk, and nothing that would need one.
    /// </remarks>
    private bool NamesRoot(ReadOnlySpan<char> relative)
    {
        int start = 0;
        for (int i = 0; i <= relative.Length; i++)
        {
            if (i < relative.Length && !IsSeparator(relative[i]))
            {
                continue;
            }

            ReadOnlySpan<char> component = relative[start..i];
            if (component.Length > 0 && component is not ".")
            {
                return false;
            }

            start = i + 1;
        }

        return true;
    }

    /// <summary>
    /// Where the directory part of a path ends: before its last name and the separators ahead
    /// of that name, but never inside the root. Minus one for the root itself.
    /// </summary>
    private int DirectoryNameEnd(ReadOnlySpan<char> path)
    {
        int root = RootLength(path);
        int end = path.Length;
        if (end <= root)
        {
            return -1;
        }

        do
        {
            end--;
        }
        while (end > root && !IsSeparator(path[end]));

        while (end > root && IsSeparator(path[end - 1]))
        {
            end--;
        }

        return end;
    }

    /// <summary>
    /// A path with each run of separators written as one of this namespace's, as
    /// <c>System.IO</c> tidies one; under Windows rules a doubled leading separator is kept,
    /// as it is there.
    /// </summary>
    private string NormalizeSeparators(ReadOnlySpan<char> path)
    {
        StringBuilder normalized = new(path.Length);
        int start = 0;
        if (_windows && path.Length > 0 && IsSeparator(path[0]))
        {
            normalized.Append(Separator);
            start = 1;
        }

        for (int i = start; i < path.Length; i++)
        {
            if (!IsSeparator(path[i]))
            {
                normalized.Append(path[i]);
            }
            else if (i + 1 == path.Length || !IsSeparator(path[i + 1]))
            {
                normalized.Append(Separator);
            }
        }

        return normalized.ToString();
    }

    /// <summary>
    /// Whether a path is one <c>System.IO</c> treats as no path at all: empty, or under Windows
    /// rules made only of spaces.
    /// </summary>
    private bool IsEffectivelyEmpty(ReadOnlySpan<char> path) =>
        path.IsEmpty || (_windows && !path.ContainsAnyExcept(' '));

    /// <summary>The names of a virtual absolute path after lexical folding.</summary>
    private List<string> Components(string absolute)
    {
        List<string> names = [];
        int root = RootLength(absolute);
        int start = root;
        for (int i = root; i <= absolute.Length; i++)
        {
            if (i < absolute.Length && !IsSeparator(absolute[i]))
            {
                continue;
            }

            string component = absolute[start..i];
            start = i + 1;
            if (component.Length == 0 || component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (names.Count > 0)
                {
                    names.RemoveAt(names.Count - 1);
                }

                continue;
            }

            names.Add(component);
        }

        return names;
    }

    /// <summary>
    /// How many leading characters are the virtual root, or zero. Exactly one root is
    /// removed: a second leading separator stays in the remainder, where the
    /// <see cref="Cap.Std.Dir"/> refuses it as absolute.
    /// </summary>
    private int RootLength(ReadOnlySpan<char> path)
    {
        if (_drive is char drive)
        {
            if (path.Length >= 3 && char.ToUpperInvariant(path[0]) == drive && path[1] == ':' && IsSeparator(path[2]))
            {
                return 3;
            }

            // Root-relative on the virtual drive, which is the only drive there is. Two leading
            // separators begin a UNC or device path, which is not.
            return path.Length >= 1 && IsSeparator(path[0]) && !(path.Length >= 2 && IsSeparator(path[1])) ? 1 : 0;
        }

        return path.Length >= 1 && IsSeparator(path[0]) ? 1 : 0;
    }

    /// <summary>
    /// Whether a path is rooted in a way that is not the virtual root: under Windows rules,
    /// one that begins with a drive letter, or with two separators when the root is a drive.
    /// </summary>
    private bool IsForeignRooted(ReadOnlySpan<char> path)
    {
        if (!_windows)
        {
            return false;
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return true;
        }

        return _drive is not null && path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);
    }

    /// <summary>
    /// A search pattern as <c>System.IO</c> reads one under <see cref="System.IO.MatchType.Win32"/>,
    /// translated to the expression <see cref="System.IO.Enumeration.FileSystemName"/> matches.
    /// </summary>
    /// <remarks>
    /// <c>""</c>, <c>.</c> and <c>*.*</c> match every name, as they always have. Where <c>\</c> is
    /// not a separator, it and <c>"</c>, <c>&lt;</c> and <c>&gt;</c> are characters a name can hold,
    /// so they are escaped to match only themselves rather than act as the escape and the DOS
    /// wildcards they are in a Win32 expression.
    /// </remarks>
    public string Win32Pattern(string pattern)
    {
        if (pattern is "" or "." or "*.*")
        {
            return "*";
        }

        if (!_windows && pattern.AsSpan().IndexOfAny("\\\"<>") >= 0)
        {
            pattern = pattern
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace(">", "\\>", StringComparison.Ordinal)
                .Replace("<", "\\<", StringComparison.Ordinal);
        }

        return System.IO.Enumeration.FileSystemName.TranslateWin32Expression(pattern);
    }

    private char[] Separators() => _windows ? ['/', '\\'] : ['/'];
}

/// <summary>A caller's path, spelled out in full, and what the <see cref="Cap.Std.Dir"/> is given.</summary>
/// <param name="Virtual">The virtual absolute path, as the caller's components; used in messages.</param>
/// <param name="Relative">What the <see cref="Cap.Std.Dir"/> resolves.</param>
/// <param name="IsRoot">
/// Whether <paramref name="Relative"/> names the root itself, which the adapter answers for
/// from the handle it holds rather than passing an empty path on.
/// </param>
internal readonly record struct Request(string Virtual, string Relative, bool IsRoot);
