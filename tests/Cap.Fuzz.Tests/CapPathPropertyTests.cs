using Cap.Fuzz.Targets;
using Cap.Primitives;
using CsCheck;

namespace Cap.Fuzz.Tests;

/// <summary>
/// Path parsing, tried on generated strings rather than chosen ones.
/// </summary>
/// <remarks>
/// Each property runs the same checks as the parser's fuzz target, so what is asserted here
/// and what the fuzzer asserts overnight cannot drift apart. The difference is the inputs.
/// These are built from the characters and fragments that parsing rules are about, so a few
/// thousand of them reach every rule on every change. When one fails, it is shrunk to the
/// smallest string that still fails, which the fuzzer does not do.
/// </remarks>
public sealed class CapPathPropertyTests
{
    /// <summary>
    /// Characters every rule is about: both separators, the dot, the drive colon, the space
    /// that Windows strips, and the letters of the device names.
    /// </summary>
    private static readonly Gen<char> PathCharacter = Gen.Frequency(
        (8, Gen.Char["/\\.: aCcOoNnLlPpTtUuXxRr1239$"]),
        (2, Gen.Char["<>\"|?*\0\t\u001f¹²³ıİ "]),
        (1, Gen.Char));

    /// <summary>Fragments that mean something to one set of rules or the other.</summary>
    private static readonly Gen<string> PathFragment = Gen.OneOfConst(
        "/", "\\", "//", "\\\\", ".", "..", "...", "a", "b.txt", " ", "C:", "c:", "\\\\?\\", "//./", "\\??\\",
        "CON", "con", "Nul", "AUX.txt", "PRN .x", "COM1", "lpt9", "COM¹", "LPT²", "CONIN$", "conout$", "COM0",
        "cOm10", "CONIN", "file:stream", "::$DATA", "\0", "a.", "a ", " ");

    private static readonly Gen<string> CharacterPaths = Gen.String[PathCharacter, 0, 48];

    private static readonly Gen<string> FragmentPaths =
        PathFragment.Array[0, 12].Select(fragments => string.Concat(fragments));

    private static readonly Gen<CapPathSyntax> Syntaxes = Gen.Enum<CapPathSyntax>();

    private static readonly Gen<ParentLinkPolicy> ParentLinkPolicies = Gen.Enum<ParentLinkPolicy>();

    /// <summary>
    /// Any string is accepted or refused exactly as the rules say, and an accepted one keeps
    /// every promise parsing makes about it.
    /// </summary>
    [Fact]
    public void Any_string_is_classified_by_the_rules() =>
        Gen.Select(Gen.OneOf(CharacterPaths, FragmentPaths), Syntaxes, ParentLinkPolicies).Sample(
            (raw, syntax, parentLinks) => CapPathTarget.Check(raw, syntax, parentLinks),
            iter: PropertySettings.Iterations,
            print: Show);

    /// <summary>
    /// Paths at and around the length limits are classified by the rules too. Random strings
    /// are almost never long enough to reach them.
    /// </summary>
    [Fact]
    public void Paths_at_the_length_limits_are_classified_by_the_rules() =>
        Gen.Select(
            Gen.Int[CapPath.MaxComponentLength - 2, CapPath.MaxComponentLength + 2],
            Gen.Int[0, 3],
            Gen.Char["a/\\."],
            Syntaxes).Sample(
            (componentLength, extra, filler, syntax) =>
            {
                string component = new('n', componentLength);
                string raw = string.Join('/', Enumerable.Repeat(component, 4)) + new string(filler, extra);
                CapPathTarget.Check(raw, syntax, ParentLinkPolicy.Preserve);

                // Short names filling the whole length, so that the limit on the path is what
                // decides, and not the limit on a name.
                int total = CapPath.MaxLength - 1 + extra;
                string longest = string.Concat(Enumerable.Repeat(new string('n', 99) + "/", (total / 100) + 1))[..total];
                CapPathTarget.Check(longest, syntax, ParentLinkPolicy.Preserve);
            },
            iter: Math.Min(PropertySettings.Iterations, 500));

    /// <summary>
    /// A path put together from acceptable names, however it is punctuated, parses back to
    /// exactly those names; and rendering what was parsed and parsing it again gives the same
    /// names once more.
    /// </summary>
    /// <remarks>
    /// The punctuation is what a caller stitching paths together might produce: runs of
    /// separators, <c>.</c> components, a trailing separator, and on Windows either separator.
    /// None of it names anything, so none of it may change which names come out.
    /// </remarks>
    [Fact]
    public void A_path_built_from_names_parses_back_to_those_names()
    {
        Gen<string> name = Gen.String[Gen.Char["abcxyz0189_-~"], 1, 12];
        Gen<string> separator = Gen.OneOfConst("/", "//", "/./", "/./././", "\\", "\\.\\", "/\\");

        Gen.Select(name.Array[1, 8], separator.Array[8], Gen.Bool, Syntaxes).Sample(
            (names, separators, trailing, syntax) =>
            {
                string[] usable = syntax == CapPathSyntax.Windows
                    ? separators
                    : [.. separators.Select(text => text.Replace('\\', '/'))];

                string raw = names[0];
                for (int i = 1; i < names.Length; i++)
                {
                    raw += usable[i % usable.Length] + names[i];
                }

                raw += trailing ? usable[0] : string.Empty;

                Assert.True(
                    CapPath.TryParse(raw, syntax, ParentLinkPolicy.Reject, out CapPath path, out CapPathError error),
                    $"{raw} did not parse: {error}");

                List<string> components = [];
                foreach (ReadOnlySpan<char> component in path.EnumerateComponents())
                {
                    components.Add(component.ToString());
                }

                Assert.Equal(names, components);
                Assert.Equal(trailing, path.RequiresDirectory);
                CapPathTarget.Check(raw, syntax, ParentLinkPolicy.Reject);
            },
            iter: PropertySettings.Iterations);
    }

    private static string Show((string Raw, CapPathSyntax Syntax, ParentLinkPolicy ParentLinks) input) =>
        $"{InvariantViolation.Show(input.Raw)} under {input.Syntax} rules with {input.ParentLinks} for '..'";
}
