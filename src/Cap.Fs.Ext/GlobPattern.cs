using Cap.Primitives;

namespace Cap.Fs.Ext;

/// <summary>
/// A pattern matched against the names in a tree, level by level.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It is matched against names, never against paths.</strong> The pattern is divided
/// into one piece per level when it is parsed, and each piece is matched against the single
/// names a directory read produced — so matching happens as the walk descends, through the
/// handles the walk is holding, and nothing is ever assembled into a string that something
/// else could resolve. That is also what makes it fast on a large tree: a directory that no
/// remaining piece of the pattern could match is not entered at all.
/// </para>
/// <para>
/// The syntax is the one every tool spells the same way, and no more than that:
/// </para>
/// <list type="table">
///   <item><term><c>?</c></term><description>Any one character.</description></item>
///   <item><term><c>*</c></term><description>Any run of characters, including none, within a single name.</description></item>
///   <item><term><c>**</c></term><description>As a whole piece: any number of levels, including none.</description></item>
///   <item><term><c>[abc]</c></term><description>Any one of the characters listed.</description></item>
///   <item><term><c>[a-z]</c></term><description>Any one character in the range.</description></item>
///   <item><term><c>[!abc]</c></term><description>Any one character not listed. <c>^</c> may be used for <c>!</c>.</description></item>
/// </list>
/// <para>
/// There is no escape character, so a name containing one of the special characters cannot be
/// matched literally. Adding one would mean choosing a character to carry the meaning, and on
/// Windows every plausible choice — the backslash above all — already divides one level from
/// the next. A caller who needs to act on an awkward name has the walk, where names arrive as
/// names and a condition is written in code.
/// </para>
/// <para>
/// <strong>Case is compared exactly unless the pattern says otherwise.</strong> Whether a
/// filesystem folds case is a property of how it was made and mounted rather than of the
/// platform, so a default that guessed would be wrong somewhere; a pattern that must match
/// regardless of spelling asks for that when it is parsed.
/// </para>
/// <para>
/// <strong>Immutable once parsed.</strong> Matching keeps its progress in the search rather
/// than in the pattern, so one instance can drive any number of searches at once, on any
/// number of threads.
/// </para>
/// <para>
/// <strong>Symbolic links.</strong> A pattern is matched against the names a directory read
/// produced, and the name of a link is matched like any other name: a link whose name the
/// pattern describes is a match, whatever it points at. Whether a search continues through a
/// link to a directory is not the pattern's decision but the search's — see
/// <see cref="WalkOptions.FollowSymlinks"/>. A piece in the middle of a pattern that matches a
/// link's name therefore leads nowhere unless links are being followed, unlike a shell, where
/// the path is resolved and the link followed.
/// </para>
/// </remarks>
public sealed class GlobPattern
{
    /// <summary>The piece that matches any number of levels.</summary>
    private const string Crossing = "**";

    private readonly string[] _segments;
    private readonly bool _ignoreCase;
    private readonly string _text;

    private GlobPattern(string[] segments, bool ignoreCase, string text)
    {
        _segments = segments;
        _ignoreCase = ignoreCase;
        _text = text;
    }

    /// <summary>
    /// Reads a pattern.
    /// </summary>
    /// <param name="pattern">The pattern text.</param>
    /// <param name="ignoreCase">
    /// Whether letters match regardless of spelling. Compared with the invariant culture's
    /// rules rather than the running machine's, so a pattern means the same thing wherever it
    /// runs.
    /// </param>
    /// <returns>The parsed pattern, ready to match against a tree.</returns>
    /// <remarks>
    /// <para>
    /// The pattern is relative, like every other name this library takes. A pattern that
    /// begins at a root, or that asks to climb above one, is refused rather than resolved:
    /// there is nothing above the directory a handle grants, and a pattern that reached there
    /// would be asking for authority nobody handed out.
    /// </para>
    /// <para>
    /// Parsing touches no filesystem, so no link is looked at here. Safe to call from any
    /// thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The pattern names no level, begins at a root, or contains a piece that climbs.
    /// </exception>
    public static GlobPattern Parse(string pattern, bool ignoreCase = false) =>
        Parse(pattern, ignoreCase, CapPath.HostSyntax);

    /// <summary>
    /// Reads a pattern whose levels are divided as <paramref name="syntax"/> divides a path.
    /// </summary>
    /// <remarks>
    /// For a pattern given as text to a search beneath a handle, which is divided as that
    /// handle divides a path. A handle on a filesystem held in memory may read paths as
    /// another platform does, and a pattern divided by the running platform's rules instead
    /// would search for names the handle cannot hold.
    /// </remarks>
    internal static GlobPattern Parse(string pattern, bool ignoreCase, CapPathSyntax syntax)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length > 0 && CapPath.IsSeparator(pattern[0], syntax))
        {
            throw new ArgumentException(
                $"'{pattern}' begins at a root. A pattern is matched against the names " +
                $"beneath a directory handle, and there is nothing above that handle for it " +
                $"to start from.",
                nameof(pattern));
        }

        List<string> segments = [];
        int start = 0;
        for (int i = 0; i <= pattern.Length; i++)
        {
            if (i < pattern.Length && !CapPath.IsSeparator(pattern[i], syntax))
            {
                continue;
            }

            ReadOnlySpan<char> piece = pattern.AsSpan(start, i - start);
            start = i + 1;

            if (piece.IsEmpty || piece.SequenceEqual("."))
            {
                continue;
            }

            if (piece.SequenceEqual(".."))
            {
                throw new ArgumentException(
                    $"'{pattern}' contains a piece that climbs above the directory it would " +
                    $"be matched against. A pattern only ever describes names beneath a " +
                    $"handle.",
                    nameof(pattern));
            }

            segments.Add(new string(piece));
        }

        if (segments.Count == 0)
        {
            throw new ArgumentException(
                $"'{pattern}' names no level to match against.", nameof(pattern));
        }

        return new GlobPattern([.. segments], ignoreCase, pattern);
    }

    /// <summary>The pattern as it was written.</summary>
    /// <returns>The text given to <see cref="Parse(string, bool)"/>, unchanged.</returns>
    /// <remarks>Safe to call from any thread.</remarks>
    public override string ToString() => _text;

    /// <summary>
    /// The pieces that are live before any name has been looked at.
    /// </summary>
    /// <remarks>
    /// A state is "this is the piece the next name has to match". More than one can be live at
    /// once, because a piece that crosses levels also lets the piece after it start here.
    /// </remarks>
    internal int[] Start()
    {
        List<int> states = [];
        Extend(0, states);
        return [.. states];
    }

    /// <summary>
    /// Advances the live pieces past one name.
    /// </summary>
    /// <param name="states">The pieces live in the directory the name was read from.</param>
    /// <param name="name">The name.</param>
    /// <param name="matched">Whether the name is one the whole pattern describes.</param>
    /// <returns>
    /// The pieces that would be live inside this name, or null when nothing the pattern could
    /// still match lies beneath it — which is how a walk driven by a pattern avoids entering
    /// most of a tree.
    /// </returns>
    internal int[]? Step(ReadOnlySpan<int> states, string name, out bool matched)
    {
        matched = false;
        int last = _segments.Length - 1;
        List<int>? children = null;

        foreach (int state in states)
        {
            if (_segments[state] == Crossing)
            {
                // A crossing piece matches this name whatever it is, and stays live inside it.
                // The piece after it is already live here, because it was added when this one
                // was, so there is nothing else to do for it.
                matched |= state == last;
                Extend(state, children ??= []);
                continue;
            }

            if (!Matches(_segments[state].AsSpan(), name.AsSpan(), _ignoreCase))
            {
                continue;
            }

            if (state == last)
            {
                matched = true;
            }
            else
            {
                Extend(state + 1, children ??= []);
            }
        }

        return children is null ? null : [.. children];
    }

    /// <summary>
    /// Adds a piece to the live set, along with whatever a crossing piece lets start after it.
    /// </summary>
    private void Extend(int index, List<int> states)
    {
        while (index < _segments.Length)
        {
            if (!states.Contains(index))
            {
                states.Add(index);
            }

            if (_segments[index] != Crossing)
            {
                return;
            }

            index++;
        }
    }

    /// <summary>
    /// Whether one piece of a pattern describes one name.
    /// </summary>
    /// <remarks>
    /// Written as a loop with one remembered point to return to rather than as a recursion.
    /// The pattern and the name both come from outside — one from a caller, one from a
    /// directory somebody else filled — so a matcher that called itself per character would
    /// turn a long name against a pattern full of runs into a stack overflow, which is a way
    /// of ending the process rather than of refusing an input.
    /// </remarks>
    private static bool Matches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name, bool ignoreCase)
    {
        int p = 0;
        int n = 0;
        int run = -1;
        int resume = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                // Remembered rather than explored: the run is assumed to match nothing, and if
                // the rest of the pattern then fails it is given one more character and tried
                // again from here.
                run = p++;
                resume = n;
                continue;
            }

            if (p < pattern.Length)
            {
                int next = AtomEnd(pattern, p);
                if (Matches(pattern[p..next], name[n], ignoreCase))
                {
                    p = next;
                    n++;
                    continue;
                }
            }

            if (run < 0)
            {
                return false;
            }

            p = run + 1;
            n = ++resume;
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>Where the smallest matchable piece of a pattern starting at an index ends.</summary>
    /// <remarks>
    /// A character class is one piece however long it is. An unterminated one is treated as
    /// the literal characters it is made of, which is what every shell does and is the least
    /// surprising reading of a pattern somebody mistyped.
    /// </remarks>
    private static int AtomEnd(ReadOnlySpan<char> pattern, int start)
    {
        if (pattern[start] != '[')
        {
            return start + 1;
        }

        int close = pattern[(start + 1)..].IndexOf(']');
        return close < 0 ? start + 1 : start + close + 2;
    }

    /// <summary>Whether one piece of a pattern describes one character.</summary>
    private static bool Matches(ReadOnlySpan<char> atom, char c, bool ignoreCase)
    {
        if (atom.Length == 1)
        {
            return atom[0] == '?' || Same(atom[0], c, ignoreCase);
        }

        if (atom[0] != '[')
        {
            return Same(atom[0], c, ignoreCase);
        }

        ReadOnlySpan<char> members = atom[1..^1];
        bool negated = members.Length > 0 && members[0] is '!' or '^';
        if (negated)
        {
            members = members[1..];
        }

        bool found = false;
        for (int i = 0; i < members.Length; i++)
        {
            if (i + 2 < members.Length && members[i + 1] == '-')
            {
                found |= InRange(members[i], members[i + 2], c, ignoreCase);
                i += 2;
                continue;
            }

            found |= Same(members[i], c, ignoreCase);
        }

        return found != negated;
    }

    /// <summary>Whether a character falls between two others.</summary>
    /// <remarks>
    /// Compared by code point. A range written the wrong way round matches nothing rather than
    /// being reversed, because a reversed range is a mistake and quietly fixing it would hide
    /// the mistake in a pattern that then matched far more than its author meant.
    /// </remarks>
    private static bool InRange(char low, char high, char c, bool ignoreCase)
    {
        if (c >= low && c <= high)
        {
            return true;
        }

        if (!ignoreCase)
        {
            return false;
        }

        char folded = char.ToUpperInvariant(c);
        if (folded >= low && folded <= high)
        {
            return true;
        }

        folded = char.ToLowerInvariant(c);
        return folded >= low && folded <= high;
    }

    /// <summary>Whether two characters are the same, by the pattern's rule about spelling.</summary>
    private static bool Same(char a, char b, bool ignoreCase) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
}
