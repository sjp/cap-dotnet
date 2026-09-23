using Cap.Primitives;
using Cap.Std;
using PluginHost.Contracts;

[assembly: CapabilityStrict]

namespace PluginHost.Plugins.Notes;

/// <summary>
/// A well-behaved plugin: it keeps a file of notes in the directory it was given.
/// </summary>
public sealed class NotesPlugin : IPlugin
{
    /// <inheritdoc/>
    public string Name => "notes";

    /// <inheritdoc/>
    public void Run(Dir data, TextWriter log)
    {
        data.WriteAllBytes("notes.txt", "private to the notes plugin"u8);
        log.WriteLine($"wrote notes.txt ({data.GetMetadata("notes.txt").Length} bytes)");
    }
}
