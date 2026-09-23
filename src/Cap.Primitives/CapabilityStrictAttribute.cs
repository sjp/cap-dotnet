namespace Cap.Primitives;

/// <summary>
/// Declares that this assembly holds itself to the capability discipline: every diagnostic
/// the cap-dotnet analyzer reports in it is an error.
/// </summary>
/// <remarks>
/// <para>
/// <c>[assembly: CapabilityStrict]</c> turns on the rules that are off until a project asks
/// for them — reaching the filesystem, the network, the clock or the operating system's
/// entropy by any route other than a capability — and raises the ones that are on already
/// to errors. It is the single line that makes the ambient APIs a build failure rather than
/// something a reviewer has to notice.
/// </para>
/// <para>
/// A severity written explicitly for one rule in <c>.editorconfig</c> still wins, so a
/// strict assembly can stand one rule down by name — <c>dotnet_diagnostic.CAP0004.severity
/// = none</c> — without giving up the rest.
/// </para>
/// <para>
/// This is a statement about the code in this assembly, checked when it is compiled. It is
/// not a runtime restriction and not a security boundary: code that is not compiled with
/// the analyzer, or that suppresses it, is not stopped by anything.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class CapabilityStrictAttribute : Attribute
{
}
