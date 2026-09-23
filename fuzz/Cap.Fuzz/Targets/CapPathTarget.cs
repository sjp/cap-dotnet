using System.Buffers.Binary;
using Cap.Primitives;
using static Cap.Fuzz.Targets.InvariantViolation;

namespace Cap.Fuzz.Targets;

/// <summary>
/// Path parsing, held to an independent statement of its rules and to its own promises.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is the first thing every path meets and the only thing that decides whether a
/// string is even shaped like something inside the sandbox. It is a pure function of a
/// string and two settings, which makes it the cheapest possible target: an input costs
/// microseconds, and a disagreement is a finding without any filesystem to interpret.
/// </para>
/// <para>
/// The input is one byte of settings followed by the path as UTF-16 code units. The low bit
/// of the settings byte picks Windows rules over POSIX ones, and the next bit keeps
/// <c>..</c> rather than refusing it.
/// </para>
/// </remarks>
internal static class CapPathTarget
{
    public const string Name = "cap-path";

    public static void Run(ReadOnlySpan<byte> data)
    {
        FuzzInput input = new(data);
        byte settings = input.NextByte();
        CapPathSyntax syntax = (settings & 1) != 0 ? CapPathSyntax.Windows : CapPathSyntax.Unix;
        ParentLinkPolicy parentLinks = (settings & 2) != 0 ? ParentLinkPolicy.Preserve : ParentLinkPolicy.Reject;
        Check(input.RestAsUtf16(), syntax, parentLinks);
    }

    /// <summary>Builds the input that <see cref="Run"/> reads back as these arguments.</summary>
    public static byte[] Encode(string raw, CapPathSyntax syntax, ParentLinkPolicy parentLinks)
    {
        byte[] data = new byte[1 + (raw.Length * sizeof(char))];
        data[0] = (byte)((syntax == CapPathSyntax.Windows ? 1 : 0) | (parentLinks == ParentLinkPolicy.Preserve ? 2 : 0));
        for (int i = 0; i < raw.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1 + (i * sizeof(char))), raw[i]);
        }

        return data;
    }

    /// <summary>
    /// Parses <paramref name="raw"/> and checks everything that must be true of the result.
    /// </summary>
    public static void Check(string raw, CapPathSyntax syntax, ParentLinkPolicy parentLinks)
    {
        string shown = $"{Show(raw)} under {syntax} rules with {parentLinks} for '..'";

        bool accepted = CapPath.TryParse(raw, syntax, parentLinks, out CapPath path, out CapPathError error);
        PathOracle.Verdict expected = PathOracle.Classify(raw, syntax, parentLinks);

        Require(
            accepted == expected.Accepted,
            $"{shown} was {(accepted ? "accepted" : $"refused as {error}")}, but the rules say it should be {(expected.Accepted ? "accepted" : "refused")}.");
        Require(
            CapPath.Validate(raw, syntax, parentLinks) == error,
            $"{shown}: validating the characters and parsing the string disagree.");

        if (!accepted)
        {
            Require(error != CapPathError.None, $"{shown} was refused without a reason.");
            Require(
                path.ComponentCount == 0 && path.Raw.IsEmpty,
                $"{shown} was refused but still produced a path.");
            return;
        }

        Require(error == CapPathError.None, $"{shown} was accepted with an error of {error}.");

        // Parsing never rewrites: what is held is the caller's own string, not a copy that
        // could have been edited on the way.
        Require(ReferenceEquals(path.ToString(), raw), $"{shown} is no longer the caller's string.");
        Require(path.Syntax == syntax, $"{shown} forgot the rules it was parsed under.");

        List<string> components = [];
        foreach (ReadOnlySpan<char> component in path.EnumerateComponents())
        {
            components.Add(component.ToString());
        }

        Require(
            components.SequenceEqual(expected.Components),
            $"{shown} yields [{string.Join(", ", components.Select(Show))}], but its components are [{string.Join(", ", expected.Components.Select(Show))}].");
        Require(path.ComponentCount == components.Count, $"{shown} counts {path.ComponentCount} components but yields {components.Count}.");

        bool hasParentLink = components.Contains("..");
        Require(path.ContainsParentLink == hasParentLink, $"{shown} misreports whether it contains '..'.");
        Require(
            parentLinks == ParentLinkPolicy.Preserve || !hasParentLink,
            $"{shown} kept a '..' that it was told to refuse.");
        Require(path.RequiresDirectory == expected.RequiresDirectory, $"{shown} misreports whether it must name a directory.");
        Require(
            path.IsSingleComponent == (components.Count == 1 && !hasParentLink && !expected.RequiresDirectory),
            $"{shown} misreports whether it is a single lookup.");

        CheckSplit(raw, path, components, shown);
        CheckRenderedForm(components, syntax, parentLinks, expected.RequiresDirectory, shown);

        // Windows rules refuse strictly more than POSIX ones, except that a backslash divides
        // a name there and not here, which can leave a long name short enough to pass.
        if (syntax == CapPathSyntax.Windows && !raw.Contains('\\', StringComparison.Ordinal))
        {
            Require(
                CapPath.TryParse(raw, CapPathSyntax.Unix, parentLinks, out _, out _),
                $"{shown} was accepted, but POSIX rules refuse it.");
        }
    }

    /// <summary>
    /// The split into everything ahead of the last component and that component, which the
    /// operations that act on a name use, must divide the string and not rewrite it.
    /// </summary>
    private static void CheckSplit(string raw, CapPath path, List<string> components, string shown)
    {
        Require(
            path.TrySplitLastComponent(out ReadOnlySpan<char> parent, out ReadOnlySpan<char> name),
            $"{shown} names components but could not be split.");
        Require(name.SequenceEqual(components[^1]), $"{shown} split off {Show(name.ToString())} as its last component.");

        int end = parent.Length + name.Length;
        Require(
            raw.AsSpan(0, end).SequenceEqual(string.Concat(parent, name)),
            $"{shown} was not split at a point in the caller's string.");

        foreach (string rest in raw[end..].Split('/', '\\'))
        {
            Require(rest is "" or ".", $"{shown} left a component behind after its last one.");
        }

        CapPathError parentError = CapPath.Validate(parent, path.Syntax, ParentLinkPolicy.Preserve);
        Require(
            components.Count == 1 ? parent.IsEmpty || parentError == CapPathError.Empty : parentError == CapPathError.None,
            $"{shown}: what comes ahead of the last component does not parse as the rest of the path.");
    }

    /// <summary>
    /// Rendering an accepted path from its components and parsing the rendering again gives
    /// back the same components. A component that had to be refused once it stood on its own
    /// was accepted only because of where it sat.
    /// </summary>
    private static void CheckRenderedForm(
        List<string> components,
        CapPathSyntax syntax,
        ParentLinkPolicy parentLinks,
        bool requiresDirectory,
        string shown)
    {
        string rendered = string.Join('/', components) + (requiresDirectory ? "/" : string.Empty);

        Require(
            CapPath.TryParse(rendered, syntax, parentLinks, out CapPath again, out CapPathError error),
            $"{shown} rendered as {Show(rendered)} does not parse again: {error}.");

        int index = 0;
        foreach (ReadOnlySpan<char> component in again.EnumerateComponents())
        {
            Require(
                index < components.Count && component.SequenceEqual(components[index]),
                $"{shown} rendered as {Show(rendered)} parses to different components.");
            index++;
        }

        Require(index == components.Count, $"{shown} rendered as {Show(rendered)} lost components.");
        Require(again.RequiresDirectory == requiresDirectory, $"{shown} rendered as {Show(rendered)} changed whether it names a directory.");
    }
}
