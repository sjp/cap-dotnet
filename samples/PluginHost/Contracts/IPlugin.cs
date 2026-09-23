using Cap.Std;

namespace PluginHost.Contracts;

/// <summary>
/// What the host knows about a plugin, and everything a plugin is given.
/// </summary>
/// <remarks>
/// The parameter list is the plugin's whole reach into the filesystem: one directory, its
/// own. There is no path to join onto and no root to climb out to, so what a plugin can
/// touch can be read off this signature without reading the plugin.
/// </remarks>
public interface IPlugin
{
    /// <summary>A short name, used for the plugin's directory and in the log.</summary>
    string Name { get; }

    /// <summary>Does the plugin's work.</summary>
    /// <param name="data">The plugin's own directory, which it may use as it likes.</param>
    /// <param name="log">Where to report what happened.</param>
    void Run(Dir data, TextWriter log);
}
