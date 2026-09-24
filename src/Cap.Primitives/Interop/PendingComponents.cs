namespace Cap.Primitives.Interop;

/// <summary>
/// The components a walk has left to resolve, in the order it will meet them.
/// </summary>
/// <remarks>
/// <para>
/// A walk that meets a symbolic link has to resolve the link's target <em>before</em>
/// whatever was left of the path that reached it: <c>link/rest</c>, where <c>link</c> points
/// at <c>a/b</c>, is <c>a/b/rest</c>. So the remaining path is not a fixed list that can be
/// indexed through — it grows at the front, repeatedly, from strings that did not exist when
/// the walk started.
/// </para>
/// <para>
/// This models that as a stack of frames rather than as a list that is spliced into. Each
/// frame is a string and a position in it; components come from the topmost frame until it
/// runs out, and following a link pushes its target as a new frame. The result is the same
/// order a splice would give, without copying the remainder of the path every time a link
/// appears — which matters because the number of links is attacker-controlled and the length
/// of the path is too, so a walk that copies on every link does work in the product of the
/// two.
/// </para>
/// <para>
/// Nothing is allocated for a path with no links in it, which is nearly all of them: the
/// caller's own path is held inline and the frame array comes into existence only when a
/// link is actually followed.
/// </para>
/// </remarks>
internal ref struct PendingComponents
{
    private Frame _path;
    private Frame[]? _followed;
    private int _followedCount;

    /// <summary>Starts with the caller's path and nothing followed.</summary>
    public PendingComponents(scoped in CapPath path)
    {
        _path = new Frame(path.Text, path.Syntax, path.RequiresDirectory);
        _followed = null;
        _followedCount = 0;
    }

    /// <summary>
    /// Whether any component remains. False immediately after the last one is taken, which
    /// is how the walk knows it has reached the component the operation is actually about.
    /// </summary>
    public readonly bool HasMore
    {
        get
        {
            for (int i = _followedCount - 1; i >= 0; i--)
            {
                if (_followed![i].HasMore)
                {
                    return true;
                }
            }

            return _path.HasMore;
        }
    }

    /// <summary>
    /// Whether the component just taken has to turn out to be a directory, because a trailing
    /// separator or a final <c>.</c> or <c>..</c> follows it. Meaningful once
    /// <see cref="HasMore"/> is false, which is when the walk asks it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement belongs to whichever string had the separator, and a link's stored
    /// target can have one as well as the caller's path: <c>file/</c> stored in a link is a
    /// link that must lead to a directory, and following it to a regular file fails as the
    /// kernel's own resolution fails. Losing the flag when the target is spliced in would make
    /// the walk more permissive about a link's spelling than the platform it stands in for.
    /// </para>
    /// <para>
    /// Every frame still held when the last component is taken ended with that component,
    /// since the one it came from has nothing after it and each frame beneath was
    /// interrupted at a link that was its own last component. So the requirement is any of
    /// theirs: a caller's <c>link/</c> where the link stores <c>a/b</c> needs <c>b</c> to be a
    /// directory, and so does a caller's <c>link</c> where it stores <c>a/b/</c>.
    /// </para>
    /// </remarks>
    public readonly bool RequiresDirectory
    {
        get
        {
            for (int i = _followedCount - 1; i >= 0; i--)
            {
                if (_followed![i].RequiresDirectory)
                {
                    return true;
                }
            }

            return _path.RequiresDirectory;
        }
    }

    /// <summary>
    /// Takes the next component. <c>.</c> components and repeated separators are skipped,
    /// exactly as path parsing skips them; <c>..</c> is returned like any other name.
    /// </summary>
    public bool TryNext(out ReadOnlySpan<char> component)
    {
        while (_followedCount > 0)
        {
            if (_followed![_followedCount - 1].TryNext(out component))
            {
                return true;
            }

            _followed[--_followedCount] = default;
        }

        return _path.TryNext(out component);
    }

    /// <summary>
    /// Puts a followed link's target in front of everything still pending.
    /// </summary>
    /// <returns>
    /// False when too many links are already being resolved at once. The symlink budget
    /// normally stops a walk long before this, so reaching it means the budget was raised
    /// past what this can hold and the walk must stop rather than silently drop a frame.
    /// </returns>
    public bool TryFollow(scoped in CapPath target)
    {
        _followed ??= new Frame[MaxFollowedLinks];

        if (_followedCount == _followed.Length)
        {
            return false;
        }

        _followed[_followedCount++] = new Frame(target.Text, target.Syntax, target.RequiresDirectory);
        return true;
    }

    /// <summary>
    /// How many link targets may be part-resolved at once.
    /// </summary>
    /// <remarks>
    /// One frame per link whose target has been entered and not yet finished, which is the
    /// depth of link nesting rather than the number of links followed. It matches the
    /// symlink budget so that the budget is always the limit that is reached first and the
    /// one whose error the caller sees.
    /// </remarks>
    private const int MaxFollowedLinks = PortableResolver.MaxSymbolicLinks;

    /// <summary>One string being read a component at a time.</summary>
    private struct Frame
    {
        private readonly string _source;
        private readonly CapPathSyntax _syntax;
        private int _cursor;

        public Frame(string source, CapPathSyntax syntax, bool requiresDirectory)
        {
            _source = source;
            _syntax = syntax;
            RequiresDirectory = requiresDirectory;
            _cursor = 0;
            SkipNothingComponents();
        }

        /// <summary>
        /// True when a component remains. The cursor is kept on the first character of the
        /// next real component precisely so that this is a comparison and not a scan —
        /// asking it is the walk's test for "was that the last one", once per step.
        /// </summary>
        public readonly bool HasMore => _source is not null && _cursor < _source.Length;

        /// <summary>Whether the string's last component has to be a directory.</summary>
        public bool RequiresDirectory { get; }

        public bool TryNext(out ReadOnlySpan<char> component)
        {
            if (!HasMore)
            {
                component = default;
                return false;
            }

            int end = EndOfComponent(_cursor);
            component = _source.AsSpan(_cursor, end - _cursor);
            _cursor = end;
            SkipNothingComponents();
            return true;
        }

        /// <summary>
        /// Advances past separators and <c>.</c> components, neither of which names
        /// anything, so that the cursor sits on a real component or at the end.
        /// </summary>
        private void SkipNothingComponents()
        {
            while (_source is not null && _cursor < _source.Length)
            {
                if (CapPath.IsSeparator(_source[_cursor], _syntax))
                {
                    _cursor++;
                    continue;
                }

                int end = EndOfComponent(_cursor);
                if (end - _cursor == 1 && _source[_cursor] == '.')
                {
                    _cursor = end;
                    continue;
                }

                return;
            }
        }

        private readonly int EndOfComponent(int start)
        {
            int end = start;
            while (end < _source.Length && !CapPath.IsSeparator(_source[end], _syntax))
            {
                end++;
            }

            return end;
        }
    }
}
