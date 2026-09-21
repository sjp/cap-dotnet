namespace Cap.Primitives;

/// <summary>
/// A caller-supplied path that has been checked to be relative and free of anything that
/// would mean something different by the time it reached the kernel.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because the framework's own path helpers cannot be used for a
/// containment decision. <c>GetFullPath</c> resolves against the process working directory
/// and collapses <c>..</c> as string arithmetic, which gives the wrong answer whenever a
/// component is a symlink; on Windows the normalisation layer strips trailing dots and
/// spaces and maps device names, all after any validation a caller has written. In both
/// cases the string that was checked is not the string that gets opened, and that gap is the
/// whole attack.
/// </para>
/// <para>
/// So nothing here rewrites a path. Parsing classifies and refuses; it never repairs. The
/// only thing dropped is a <c>.</c> component and a repeated separator, neither of which can
/// change which file is named. What survives is the caller's own characters, split into
/// components, ready to be handed to a resolver one at a time.
/// </para>
/// <para>
/// A parsed path is not a promise that the file exists, that it is reachable, or that it is
/// inside anything. It is a promise about the <em>string</em>: relative, well-formed, and
/// containing no component that the platform will reinterpret. Containment is decided later,
/// by resolving against a directory handle.
/// </para>
/// <para>
/// Instances are cheap and hold a reference to the caller's string rather than a copy, so
/// parsing allocates nothing. The type is immutable; a <see langword="default"/> instance
/// behaves as an empty path and resolves to nothing.
/// </para>
/// </remarks>
public readonly struct CapPath
{
    /// <summary>
    /// The longest path this parser will look at, in characters.
    /// </summary>
    /// <remarks>
    /// This is a bound on our own work, not a statement about what the filesystem accepts.
    /// The kernel's limits are stricter, expressed in bytes rather than characters, and vary
    /// by platform and filesystem; a path under this limit may still be refused when it is
    /// opened. The number matches the widest path Windows will accept through its
    /// extended-length form, which makes it comfortably larger than anything legitimate and
    /// small enough that a hostile caller cannot use length alone to cost us anything.
    /// </remarks>
    public const int MaxLength = 32767;

    /// <summary>
    /// The longest single component this parser will look at, in characters.
    /// </summary>
    /// <remarks>
    /// Unix caps a filename at 255 <em>bytes</em> and Windows at 255 characters. A component
    /// of more than 255 characters is always more than 255 bytes, so refusing here never
    /// refuses a name the kernel would have accepted — it only declines to carry one that
    /// is certain to fail, and does so before any syscall.
    /// </remarks>
    public const int MaxComponentLength = 255;

    private readonly string? _raw;
    private readonly CapPathSyntax _syntax;
    private readonly int _componentCount;
    private readonly Flags _flags;

    private CapPath(string raw, CapPathSyntax syntax, int componentCount, Flags flags)
    {
        _raw = raw;
        _syntax = syntax;
        _componentCount = componentCount;
        _flags = flags;
    }

    [System.Flags]
    private enum Flags : byte
    {
        None = 0,
        ContainsParentLink = 1 << 0,
        RequiresDirectory = 1 << 1,
    }

    /// <summary>The rules this path was parsed under.</summary>
    public CapPathSyntax Syntax => _syntax;

    /// <summary>The caller's original characters, unmodified.</summary>
    public ReadOnlySpan<char> Raw => _raw;

    /// <summary>
    /// The caller's own string, for code that has to keep the path across calls rather than
    /// read it in one pass.
    /// </summary>
    /// <remarks>
    /// The resolver needs this: it walks components one at a time with syscalls in between,
    /// and a span cannot survive that. It is the original instance and not a copy, so
    /// reaching for it costs nothing.
    /// </remarks>
    internal string Text => _raw ?? string.Empty;

    /// <summary>
    /// How many components the path names, after <c>.</c> and repeated separators are
    /// dropped. Any <c>..</c> that survived parsing counts as one.
    /// </summary>
    public int ComponentCount => _componentCount;

    /// <summary>
    /// Whether the path contains a <c>..</c> component. Only ever <see langword="true"/> on
    /// a path parsed under <see cref="ParentLinkPolicy.Preserve"/>; the default policy
    /// refuses such a path outright.
    /// </summary>
    /// <remarks>
    /// A resolver that sees this set must move up by taking an actual step against a real
    /// directory handle and re-checking the result against the sandbox root. It must not
    /// remove the component and the one before it from the list, which is the same lexical
    /// collapse this type refuses to do, merely performed later.
    /// </remarks>
    public bool ContainsParentLink => (_flags & Flags.ContainsParentLink) != 0;

    /// <summary>
    /// Whether the path ends in a way that requires its target to be a directory — a
    /// trailing separator, or a final component of <c>.</c> or <c>..</c>.
    /// </summary>
    /// <remarks>
    /// <c>foo/</c> and <c>foo</c> are not the same request: the first must fail if
    /// <c>foo</c> is a regular file. Recording it here keeps the distinction from being
    /// quietly lost at the point where the path stops being a string, since once components
    /// have been split out there is nothing left to recover it from.
    /// </remarks>
    public bool RequiresDirectory => (_flags & Flags.RequiresDirectory) != 0;

    /// <summary>
    /// Whether the path is exactly one ordinary filename, so resolving it is a single
    /// lookup against the directory handle rather than a walk.
    /// </summary>
    /// <remarks>
    /// This is the common case by a wide margin and skipping the loop for it is worth doing.
    /// It is deliberately narrow: a path with a <c>..</c> in it, or one that insists on a
    /// directory, is not a single lookup even when it has one component, so neither reports
    /// as one. A resolver can act on this without any further checks.
    /// </remarks>
    public bool IsSingleComponent =>
        _componentCount == 1 && _flags == Flags.None;

    /// <summary>
    /// Parses <paramref name="raw"/> under the rules of the running platform, refusing
    /// <c>..</c>.
    /// </summary>
    /// <remarks>
    /// This is the overload for a boundary where a path first arrives from a caller. It
    /// allocates nothing: the returned path refers to <paramref name="raw"/> itself.
    /// </remarks>
    public static bool TryParse(string raw, out CapPath path, out CapPathError error) =>
        TryParse(raw, HostSyntax, ParentLinkPolicy.Reject, out path, out error);

    /// <summary>
    /// Parses <paramref name="raw"/> under an explicit syntax and <c>..</c> policy.
    /// </summary>
    public static bool TryParse(
        string raw,
        CapPathSyntax syntax,
        ParentLinkPolicy parentLinks,
        out CapPath path,
        out CapPathError error)
    {
        ArgumentNullException.ThrowIfNull(raw);

        error = Scan(raw, syntax, parentLinks, out int componentCount, out Flags flags);
        if (error != CapPathError.None)
        {
            path = default;
            return false;
        }

        path = new CapPath(raw, syntax, componentCount, flags);
        return true;
    }

    /// <summary>
    /// Runs the same checks as <see cref="TryParse(string, out CapPath, out CapPathError)"/>
    /// over characters that are not already a string, without producing a
    /// <see cref="CapPath"/>.
    /// </summary>
    /// <remarks>
    /// A <see cref="CapPath"/> has to hold a string, so building one from a span would mean
    /// copying. This overload exists so a caller holding a span can get the answer without
    /// paying for that — the verdict is all most callers want, and the components can be
    /// walked from the span directly afterwards.
    /// </remarks>
    public static CapPathError Validate(
        ReadOnlySpan<char> raw,
        CapPathSyntax syntax,
        ParentLinkPolicy parentLinks) =>
        Scan(raw, syntax, parentLinks, out _, out _);

    /// <summary>The syntax the running platform uses.</summary>
    public static CapPathSyntax HostSyntax =>
        OperatingSystem.IsWindows() ? CapPathSyntax.Windows : CapPathSyntax.Unix;

    /// <summary>
    /// Walks the path's components in order, without allocating.
    /// </summary>
    /// <remarks>
    /// <c>.</c> components and repeated separators are skipped. Everything else is yielded
    /// exactly as the caller wrote it, including any <c>..</c>, which is never collapsed and
    /// never silently removed.
    /// </remarks>
    public ComponentEnumerator EnumerateComponents() => new(_raw, _syntax);

    /// <inheritdoc/>
    public override string ToString() => _raw ?? string.Empty;

    /// <summary>
    /// Whether <paramref name="c"/> separates components under <paramref name="syntax"/>.
    /// </summary>
    /// <remarks>
    /// Shared with the resolver rather than restated there. Two definitions of what divides
    /// a path would eventually differ, and the difference would be a name this type had
    /// checked as one component being split into two by whatever opened it.
    /// </remarks>
    internal static bool IsSeparator(char c, CapPathSyntax syntax) =>
        c == '/' || (syntax == CapPathSyntax.Windows && c == '\\');

    /// <summary>
    /// The single pass that decides everything: prefix shape first, then each component.
    /// </summary>
    private static CapPathError Scan(
        ReadOnlySpan<char> raw,
        CapPathSyntax syntax,
        ParentLinkPolicy parentLinks,
        out int componentCount,
        out Flags flags)
    {
        componentCount = 0;
        flags = Flags.None;

        if (raw.IsEmpty)
        {
            return CapPathError.Empty;
        }

        if (raw.Length > MaxLength)
        {
            return CapPathError.TooLong;
        }

        CapPathError prefix = ClassifyPrefix(raw, syntax);
        if (prefix != CapPathError.None)
        {
            return prefix;
        }

        int start = 0;
        for (int i = 0; i <= raw.Length; i++)
        {
            if (i < raw.Length && !IsSeparator(raw[i], syntax))
            {
                continue;
            }

            ReadOnlySpan<char> component = raw[start..i];
            start = i + 1;

            // An empty component is a repeated or trailing separator. The kernel treats
            // `a//b` as `a/b`, so refusing it would only push callers into stitching paths
            // together more carefully than the OS requires. Dropping it is safe because it
            // names nothing: every component that remains is still checked on its own, so
            // there is no check to be skipped past.
            if (component.IsEmpty || component.SequenceEqual("."))
            {
                continue;
            }

            if (component.SequenceEqual(".."))
            {
                if (parentLinks == ParentLinkPolicy.Reject)
                {
                    return CapPathError.ParentLink;
                }

                flags |= Flags.ContainsParentLink;
                componentCount++;
                continue;
            }

            if (component.Length > MaxComponentLength)
            {
                return CapPathError.TooLong;
            }

            CapPathError componentError = syntax == CapPathSyntax.Windows
                ? WindowsNames.ValidateComponent(component)
                : ValidateUnixComponent(component);

            if (componentError != CapPathError.None)
            {
                return componentError;
            }

            componentCount++;
        }

        if (componentCount == 0)
        {
            // Nothing but separators and `.`. The caller has named the directory it is
            // already holding, which no resolution step can express.
            return CapPathError.Empty;
        }

        if (RequiresDirectoryTarget(raw, syntax))
        {
            flags |= Flags.RequiresDirectory;
        }

        return CapPathError.None;
    }

    /// <summary>
    /// Rejects every path shape but a plain relative one, each with its own reason.
    /// </summary>
    private static CapPathError ClassifyPrefix(ReadOnlySpan<char> raw, CapPathSyntax syntax)
    {
        if (syntax == CapPathSyntax.Unix)
        {
            return raw[0] == '/' ? CapPathError.Absolute : CapPathError.None;
        }

        bool firstIsSeparator = IsSeparator(raw[0], syntax);
        if (firstIsSeparator)
        {
            if (raw.Length < 2 || !IsSeparator(raw[1], syntax))
            {
                // `\file` is relative to whichever drive the process is currently on, which
                // is ambient state, so it names no fixed location at all.
                return CapPathError.RootRelative;
            }

            // `\\?\` and `\\.\` hand the rest of the string to the object manager with Win32
            // normalisation skipped entirely. Their forward-slash spellings reach the same
            // place, so both separators are accepted when looking for them.
            if (raw.Length >= 4 && raw[2] is '?' or '.' && IsSeparator(raw[3], syntax))
            {
                return CapPathError.DeviceNamespace;
            }

            return CapPathError.Unc;
        }

        if (raw.Length >= 2 && raw[1] == ':' && IsDriveLetter(raw[0]))
        {
            // `C:\dir` is absolute. `C:dir` is worse than absolute: it looks relative, and
            // resolves against a per-drive working directory the process carries around.
            return raw.Length >= 3 && IsSeparator(raw[2], syntax)
                ? CapPathError.Absolute
                : CapPathError.DriveRelative;
        }

        return CapPathError.None;
    }

    private static bool IsDriveLetter(char c) => char.IsAsciiLetter(c);

    /// <summary>
    /// Whether the path insists its target be a directory: it ends in a separator, or its
    /// last component is <c>.</c> or <c>..</c>.
    /// </summary>
    private static bool RequiresDirectoryTarget(ReadOnlySpan<char> raw, CapPathSyntax syntax)
    {
        if (IsSeparator(raw[^1], syntax))
        {
            return true;
        }

        int lastSeparator = -1;
        for (int i = raw.Length - 1; i >= 0; i--)
        {
            if (IsSeparator(raw[i], syntax))
            {
                lastSeparator = i;
                break;
            }
        }

        ReadOnlySpan<char> last = raw[(lastSeparator + 1)..];
        return last.SequenceEqual(".") || last.SequenceEqual("..");
    }

    /// <summary>
    /// Validates one component under POSIX rules.
    /// </summary>
    /// <remarks>
    /// There is very little to say. A Unix filename may contain any byte but <c>/</c>, which
    /// has already been consumed as a separator, and <c>U+0000</c>, which terminates the
    /// string where it is handed to the kernel and so would silently truncate the name.
    /// Newlines, backslashes, leading dashes and names Windows reserves are all ordinary
    /// filenames here, and refusing them would make real files unreachable for no gain.
    /// </remarks>
    private static CapPathError ValidateUnixComponent(ReadOnlySpan<char> component) =>
        component.Contains('\0') ? CapPathError.InvalidCharacter : CapPathError.None;

    /// <summary>
    /// Yields a path's components as spans into the original string.
    /// </summary>
    public ref struct ComponentEnumerator
    {
        private readonly CapPathSyntax _syntax;
        private ReadOnlySpan<char> _remaining;
        private ReadOnlySpan<char> _current;

        internal ComponentEnumerator(ReadOnlySpan<char> raw, CapPathSyntax syntax)
        {
            _remaining = raw;
            _syntax = syntax;
            _current = default;
        }

        /// <summary>The component most recently reached.</summary>
        public readonly ReadOnlySpan<char> Current => _current;

        /// <summary>Supports <c>foreach</c>; the enumerator is its own source.</summary>
        public readonly ComponentEnumerator GetEnumerator() => this;

        /// <summary>Advances to the next component, skipping <c>.</c> and empty ones.</summary>
        public bool MoveNext()
        {
            while (!_remaining.IsEmpty)
            {
                int separator = IndexOfSeparator(_remaining, _syntax);
                ReadOnlySpan<char> component;
                if (separator < 0)
                {
                    component = _remaining;
                    _remaining = default;
                }
                else
                {
                    component = _remaining[..separator];
                    _remaining = _remaining[(separator + 1)..];
                }

                if (component.IsEmpty || component.SequenceEqual("."))
                {
                    continue;
                }

                _current = component;
                return true;
            }

            _current = default;
            return false;
        }

        private static int IndexOfSeparator(ReadOnlySpan<char> value, CapPathSyntax syntax) =>
            syntax == CapPathSyntax.Windows
                ? value.IndexOfAny('/', '\\')
                : value.IndexOf('/');
    }
}
