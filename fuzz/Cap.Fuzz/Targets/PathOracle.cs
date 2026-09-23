using Cap.Primitives;

namespace Cap.Fuzz.Targets;

/// <summary>
/// A second, deliberately naive statement of which paths the parser accepts and what
/// components it finds in them.
/// </summary>
/// <remarks>
/// <para>
/// The parser is written for speed and to allocate nothing: one pass over the characters,
/// indices instead of substrings, a switch on length to find device names. This is written
/// for obviousness instead: split the string, filter the pieces, look each one up in a set.
/// The two are meant to be hard to get wrong in the same way, so an input on which they
/// disagree is a bug in one of them, and the fuzzer's job is to find such an input.
/// </para>
/// <para>
/// Only the verdict and the components are compared, not which error was reported. A path
/// with two things wrong with it can be refused for either, and the parser is free to
/// report whichever it finds first. Whether it is refused at all is what containment
/// depends on.
/// </para>
/// <para>
/// Nothing here may call into the parser or share its helpers. An oracle that asked the
/// code under test for the answer would agree with it by construction.
/// </para>
/// </remarks>
internal static class PathOracle
{
    /// <summary>What the oracle concludes about one path.</summary>
    /// <param name="Accepted">Whether the path is acceptable.</param>
    /// <param name="Components">
    /// The components a resolver would be handed, in order, when accepted.
    /// </param>
    /// <param name="RequiresDirectory">
    /// Whether the path insists on naming a directory, when accepted.
    /// </param>
    internal sealed record Verdict(bool Accepted, IReadOnlyList<string> Components, bool RequiresDirectory);

    private static readonly Verdict Refused = new(false, [], false);

    /// <summary>
    /// Characters Windows forbids inside a filename: the wildcard characters its native open
    /// call interprets as patterns, and the colon that introduces an alternate data stream.
    /// </summary>
    private const string WindowsForbidden = "<>:\"|?*";

    /// <summary>
    /// Every spelling of a device name, in upper case. Windows reserves these as the part of
    /// a name before its first dot, and matches them without regard to ASCII case.
    /// </summary>
    private static readonly HashSet<string> WindowsDevices = BuildDeviceNames();

    /// <summary>Classifies <paramref name="raw"/> as the parser should.</summary>
    public static Verdict Classify(string raw, CapPathSyntax syntax, ParentLinkPolicy parentLinks)
    {
        if (raw.Length is 0 or > CapPath.MaxLength)
        {
            return Refused;
        }

        char[] separators = syntax == CapPathSyntax.Windows ? ['/', '\\'] : ['/'];
        if (IsRooted(raw, separators, syntax))
        {
            return Refused;
        }

        string[] segments = raw.Split(separators);
        List<string> components = [];
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parentLinks == ParentLinkPolicy.Reject)
                {
                    return Refused;
                }

                components.Add(segment);
                continue;
            }

            if (segment.Length > CapPath.MaxComponentLength || !IsAcceptableName(segment, syntax))
            {
                return Refused;
            }

            components.Add(segment);
        }

        if (components.Count == 0)
        {
            return Refused;
        }

        bool requiresDirectory = segments[^1] is "" or "." or "..";
        return new Verdict(true, components, requiresDirectory);
    }

    /// <summary>
    /// Whether the path starts from somewhere other than the directory it is resolved
    /// against. On Windows that is any leading separator, which covers a root-relative path,
    /// a network share and the device namespace alike, and any drive letter followed by a
    /// colon, which covers both an absolute path and one relative to a drive's working
    /// directory.
    /// </summary>
    private static bool IsRooted(string raw, char[] separators, CapPathSyntax syntax)
    {
        if (separators.Contains(raw[0]))
        {
            return true;
        }

        return syntax == CapPathSyntax.Windows &&
            raw.Length >= 2 && raw[1] == ':' && char.IsAsciiLetter(raw[0]);
    }

    private static bool IsAcceptableName(string name, CapPathSyntax syntax)
    {
        if (syntax == CapPathSyntax.Unix)
        {
            return !name.Contains('\0', StringComparison.Ordinal);
        }

        foreach (char c in name)
        {
            if (c < ' ' || WindowsForbidden.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return false;
        }

        string stem = name.Split('.')[0].TrimEnd(' ');
        return !WindowsDevices.Contains(AsciiUpper(stem));
    }

    /// <summary>
    /// Upper-cases ASCII letters and leaves every other character alone.
    /// </summary>
    /// <remarks>
    /// Not <see cref="string.ToUpperInvariant()"/>, which also maps characters outside ASCII
    /// onto ASCII letters: the dotless <c>ı</c> becomes <c>I</c>. The device table is matched
    /// by ASCII case only, so a name spelled with one of those is an ordinary name.
    /// </remarks>
    private static string AsciiUpper(string text) =>
        string.Create(text.Length, text, static (chars, source) =>
        {
            for (int i = 0; i < chars.Length; i++)
            {
                char c = source[i];
                chars[i] = c is >= 'a' and <= 'z' ? (char)(c - ('a' - 'A')) : c;
            }
        });

    private static HashSet<string> BuildDeviceNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal) { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };
        foreach (string port in new[] { "COM", "LPT" })
        {
            foreach (char digit in "0123456789¹²³")
            {
                _ = names.Add(port + digit);
            }
        }

        return names;
    }
}
