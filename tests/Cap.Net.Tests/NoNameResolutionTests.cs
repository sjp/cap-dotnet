using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using Cap.Std;

namespace Cap.Net.Tests;

/// <summary>
/// That nothing here turns a name into an address.
/// </summary>
/// <remarks>
/// <para>
/// A check on an allowlist is only worth as much as the relationship between the value that
/// was checked and the value that was used. Resolving a name and then connecting are two
/// separate lookups of the same name, and whoever answers them can answer differently: the
/// first with an address the pool grants, the second with one it does not. An allowlist
/// checked that way refuses nothing.
/// </para>
/// <para>
/// The way out is not a more careful resolution, it is no resolution. Every member here takes
/// an address, the address that was checked is the address that is connected to, and a caller
/// who starts from a name performs that step in their own code where it is visible for what
/// it is — a question asked of a server, whose answer somebody else controls.
/// </para>
/// <para>
/// Asserted about the public surface rather than about behaviour, because the guarantee is an
/// absence. There is no call that demonstrates it; what demonstrates it is that no such call
/// exists, and a member added later that took a host name would pass every behavioural test
/// in this assembly.
/// </para>
/// </remarks>
public sealed class NoNameResolutionTests
{
    /// <summary>
    /// The only strings the public surface accepts are paths beneath a directory handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host name and a path are both strings, so the check is on what accompanies them: a
    /// path arrives with the handle that confines it, and anything else taking a string is
    /// either a host name or on its way to becoming one.
    /// </para>
    /// <para>
    /// Exceptions are left out. The string one of those takes is the sentence it prints, and
    /// nothing resolves a sentence.
    /// </para>
    /// </remarks>
    [Fact]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject of the test is the whole exported surface, including members " +
                        "nobody has written yet, which is exactly what cannot be named statically. " +
                        "The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "As above: the types being reflected over are whatever the assembly exports.")]
    public void No_public_member_takes_a_string_that_is_not_a_path_beneath_a_handle()
    {
        List<string> offending = [];

        foreach (MethodBase member in PublicMembers())
        {
            ParameterInfo[] parameters = member.GetParameters();
            if (!parameters.Any(parameter => parameter.ParameterType == typeof(string)))
            {
                continue;
            }

            if (!parameters.Any(parameter => parameter.ParameterType == typeof(Dir)))
            {
                offending.Add($"{member.DeclaringType!.Name}.{member.Name}");
            }
        }

        Assert.Empty(offending);
    }

    /// <summary>Nothing in the public surface deals in the framework's host-and-port type.</summary>
    /// <remarks>
    /// It holds a host name as text alongside a port, so accepting one would be accepting a
    /// name however carefully the member that took it was written.
    /// </remarks>
    [Fact]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject of the test is the whole exported surface, including members " +
                        "nobody has written yet, which is exactly what cannot be named statically. " +
                        "The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "As above: the types being reflected over are whatever the assembly exports.")]
    public void No_public_member_deals_in_a_host_and_port()
    {
        List<string> offending = [];

        foreach (MethodBase member in PublicMembers())
        {
            bool mentions = member.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(DnsEndPoint));

            if (mentions || (member as MethodInfo)?.ReturnType == typeof(DnsEndPoint))
            {
                offending.Add($"{member.DeclaringType!.Name}.{member.Name}");
            }
        }

        Assert.Empty(offending);
    }

    /// <summary>The assembly is not built against the framework's resolver at all.</summary>
    /// <remarks>
    /// The strongest form of the guarantee: not that the resolver is used carefully, but that
    /// a compiled reference to it is absent, so there is nothing to use carefully.
    /// </remarks>
    [Fact]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject of the test is the whole exported surface, including members " +
                        "nobody has written yet, which is exactly what cannot be named statically. " +
                        "The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "As above: the types being reflected over are whatever the assembly exports.")]
    public void The_assembly_does_not_reference_the_resolver()
    {
        Assembly assembly = typeof(Pool).Assembly;

        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name == "System.Net.NameResolution");
    }

    /// <summary>
    /// Every public method and constructor of the assembly, apart from the exceptions.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject of the test is the whole exported surface, including members " +
                        "nobody has written yet, which is exactly what cannot be named statically. " +
                        "The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "As above: the types being reflected over are whatever the assembly exports.")]
    private static IEnumerable<MethodBase> PublicMembers()
    {
        foreach (Type type in typeof(Pool).Assembly.GetExportedTypes())
        {
            if (typeof(Exception).IsAssignableFrom(type))
            {
                continue;
            }

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                yield return method;
            }

            foreach (ConstructorInfo constructor in type.GetConstructors())
            {
                yield return constructor;
            }
        }
    }
}
