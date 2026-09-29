using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// That every interface the adapter implements is implemented whole.
/// </summary>
/// <remarks>
/// The upstream interfaces gain members in minor releases, and the package accepts any newer
/// version. A member the adapter lacks makes its type fail to load with
/// <see cref="TypeLoadException"/>, so this cannot fail against the version the tests were
/// compiled with. It names the failure when the CI job that builds against the newest
/// upstream versions runs it, and says what the rule is.
/// </remarks>
public sealed class ImplementationCompletenessTests
{
    [Fact]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject is every type in an assembly, which cannot be named statically, " +
                        "and the suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072:UnrecognizedReflectionPattern",
        Justification = "The types reflected over are whatever the assembly defines. The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "The types reflected over are whatever the assembly defines. The suite is not trimmed.")]
    public void Every_interface_member_has_an_implementation()
    {
        List<string> missing = [];
        foreach (Type type in typeof(DirFileSystem).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract)
            {
                continue;
            }

            foreach (Type iface in type.GetInterfaces())
            {
                InterfaceMapping map = type.GetInterfaceMap(iface);
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.TargetMethods[i] is null)
                    {
                        missing.Add($"{type.Name}: {iface.Name}.{map.InterfaceMethods[i].Name}");
                    }
                }
            }
        }

        Assert.Empty(missing);
    }
}
