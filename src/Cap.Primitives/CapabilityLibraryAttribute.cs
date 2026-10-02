using System.ComponentModel;

namespace Cap.Primitives;

/// <summary>
/// Marks an assembly as one of cap-dotnet's own, so the analyzer can tell this library's members
/// apart from a consumer's.
/// </summary>
/// <remarks>
/// <para>
/// The rules about using the library rather than reaching around it, <c>CAP0004</c> (a raw
/// handle taken out of a capability) and <c>CAP0005</c> (a path joined at the call), report
/// only members declared in an assembly that carries this attribute. The assembly's name is no
/// guide: a consumer is free to name its own assemblies <c>Cap.Something</c>, and its own
/// <c>UnsafeGetHandle</c> or <c>path</c> parameter is not this library's business.
/// </para>
/// <para>
/// Every assembly the repository ships applies it from the build, so a new package is covered
/// without having to remember. It is public only so that those assemblies can apply it; nothing
/// else should.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class CapabilityLibraryAttribute : Attribute
{
}
