using Microsoft.CodeAnalysis;

namespace Cap.Analyzers;

/// <summary>
/// The diagnostics this analyzer reports, and the severity each one starts at.
/// </summary>
/// <remarks>
/// <para>
/// Two tiers. The rules that forbid a whole family of framework APIs — the filesystem, the
/// network, the clock, entropy — are off until a project turns them on, because referencing
/// the library must not break a build that uses <c>File</c> for reasons of its own, and a
/// test project uses exactly those APIs to set up what it then attacks. The rules that are
/// about using this library well are on from the start, since only code that already uses it
/// can trip them.
/// </para>
/// <para>
/// <c>[assembly: CapabilityStrict]</c> raises every rule to an error; see
/// <see cref="ToStrict"/>.
/// </para>
/// </remarks>
internal static class Rules
{
    private const string Category = "Capability";

    /// <summary>
    /// Where each rule's help link points: docs/analyzers.md, at an anchor named for the
    /// rule's ID in lower case.
    /// </summary>
    private const string HelpBase = "https://github.com/sjp/cap-dotnet/blob/main/docs/analyzers.md#";

    public const string CompositionRootOption = "cap_composition_root";

    /// <summary>
    /// A rule that is never reported, and exists so that the analyzer always runs.
    /// </summary>
    /// <remarks>
    /// The compiler skips an analyzer whose every rule is off, and an assembly may switch off
    /// all the rules that are on by default while relying on <c>[assembly: CapabilityStrict]</c>
    /// for the ones that are not. Nothing can switch this rule off, because it is marked not
    /// configurable, so the analyzer is still there to see the attribute.
    /// </remarks>
    public static readonly DiagnosticDescriptor Active = new(
        "CAP0000",
        "Capability analyzer is active",
        "The capability analyzer is active",
        Category,
        DiagnosticSeverity.Hidden,
        isEnabledByDefault: true,
        description:
            "Never reported. It keeps the analyzer running when every other rule is configured " +
            "off, so that [assembly: CapabilityStrict] still takes effect.",
        helpLinkUri: HelpBase + "cap0000",
        customTags: WellKnownDiagnosticTags.NotConfigurable);

    public static readonly DiagnosticDescriptor AmbientFilesystem = new(
        "CAP0001",
        "Ambient filesystem access",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: false,
        description:
            "Reaches the filesystem by path, against everything the process can reach, rather than " +
            "beneath a directory handle it was given.",
        helpLinkUri: HelpBase + "cap0001");

    public static readonly DiagnosticDescriptor AmbientNetwork = new(
        "CAP0002",
        "Ambient network access",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: false,
        description:
            "Binds, connects, sends or resolves a name without a pool of granted endpoints " +
            "standing behind the address.",
        helpLinkUri: HelpBase + "cap0002");

    public static readonly DiagnosticDescriptor AcquireOutsideCompositionRoot = new(
        "CAP0003",
        "Ambient authority taken outside a composition root",
        "AmbientAuthority.Acquire() is called in {0}, which is not a composition root: take " +
        "authority where the program is assembled and pass what it opens down, or mark this " +
        "place [CompositionRoot] if it is where the program is assembled",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Authority should enter a program in one place, so that everything else can be read " +
            "from what it was handed. The entry point, anything marked [CompositionRoot], and " +
            "files for which .editorconfig sets cap_composition_root = true are composition roots.",
        helpLinkUri: HelpBase + "cap0003");

    public static readonly DiagnosticDescriptor UnsafeHandle = new(
        "CAP0004",
        "Raw handle taken from a capability",
        "{0} hands out the operating-system handle behind a capability; anything done with " +
        "it is outside what this library can check",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description:
            "The raw handle carries the same authority as the capability it came from, and code " +
            "holding it can pass it anywhere or outlive the capability's disposal. Each use is " +
            "worth a reviewer's look.",
        helpLinkUri: HelpBase + "cap0004");

    public static readonly DiagnosticDescriptor ConcatenatedPath = new(
        "CAP0005",
        "Path built by concatenation",
        "The path passed to {0} is built by joining strings. A component that contains a " +
        "separator or '..' then reaches anywhere beneath the handle, not just where it was meant to; " +
        "open the directory it belongs in and pass the component on its own.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A directory handle confines a path to what is beneath it, and no further. Joining a " +
            "prefix to a component that arrived from elsewhere lets that component name any " +
            "sibling of the prefix; opening the prefix as its own handle first confines the " +
            "component to it.",
        helpLinkUri: HelpBase + "cap0005");

    public static readonly DiagnosticDescriptor AmbientClock = new(
        "CAP0006",
        "Ambient clock access",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: false,
        description: "Reads or waits on the system clock rather than on a TimeProvider that was passed in.",
        helpLinkUri: HelpBase + "cap0006");

    public static readonly DiagnosticDescriptor AmbientEntropy = new(
        "CAP0007",
        "Ambient entropy",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: false,
        description:
            "Takes randomness from the operating system, or from a generator standing in for it, " +
            "rather than from a source that was passed in.",
        helpLinkUri: HelpBase + "cap0007");

    public static readonly DiagnosticDescriptor ProjectBannedSymbol = new(
        "CAP0008",
        "Symbol banned by this project",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Named in a CapBannedSymbols.txt file supplied to this project as an AdditionalFile.",
        helpLinkUri: HelpBase + "cap0008");

    public static readonly DiagnosticDescriptor[] All =
    [
        AmbientFilesystem,
        AmbientNetwork,
        AcquireOutsideCompositionRoot,
        UnsafeHandle,
        ConcatenatedPath,
        AmbientClock,
        AmbientEntropy,
        ProjectBannedSymbol,
    ];

    /// <summary>
    /// The same rule, as an error that is on without being asked for.
    /// </summary>
    /// <remarks>
    /// Reported in place of the ordinary descriptor in an assembly marked
    /// <c>[assembly: CapabilityStrict]</c>. The compiler matches a reported diagnostic to the
    /// analyzer's supported rules by ID, and takes its default severity and enablement from
    /// the diagnostic itself, so the strict form is reported without the rule having been
    /// switched on anywhere. A severity configured for the ID still overrides it, which is
    /// what lets a strict assembly stand a single rule down.
    /// </remarks>
    public static DiagnosticDescriptor ToStrict(DiagnosticDescriptor rule) => new(
        rule.Id,
        rule.Title,
        rule.MessageFormat,
        rule.Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        rule.Description,
        rule.HelpLinkUri,
        [.. rule.CustomTags]);
}
