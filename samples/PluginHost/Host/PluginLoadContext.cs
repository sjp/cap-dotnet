using System.Reflection;
using System.Runtime.Loader;

namespace PluginHost;

/// <summary>
/// Loads one plugin and the dependencies it brought with it.
/// </summary>
/// <remarks>
/// <para>
/// Anything the plugin did not bring — the contract, <c>Cap.Std</c>, the framework — comes
/// from the host, so that the <c>Dir</c> the host hands over is the type the plugin was
/// compiled against.
/// </para>
/// <para>
/// A load context separates assemblies. It is not a security boundary: code in it runs in
/// this process with everything the process can do. What keeps a plugin to its own directory
/// is that a <c>Dir</c> is all it is given; what keeps it from reaching around that is the
/// build-time rule its authors opted into, and for a plugin nobody trusts, an operating-system
/// sandbox around the whole process.
/// </para>
/// </remarks>
internal sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath))
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
