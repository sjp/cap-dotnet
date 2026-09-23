using System.Reflection;
using Cap.Primitives;
using Cap.Std;
using PluginHost;
using PluginHost.Contracts;

// Hands each plugin a Dir on a directory of its own, and nothing else.
//
//   PluginHost              runs the plugins over a scratch directory, and fails if one of them
//                           read something that was not its own
//   PluginHost <directory>  runs them over a real one
//
// This file is the composition root: the only place in the program that opens a directory by
// an ordinary path. Everything a plugin can reach is derived from the handle opened here.

string? scratch = args.Length == 0 ? Directory.CreateTempSubdirectory("cap-plugin-host-").FullName : null;
string root = args.Length == 0 ? scratch! : args[0];

const string HostSecret = "the host's own secret";
const string NotesSecret = "private to the notes plugin";

try
{
    using Dir data = Dir.Open(root, AmbientAuthority.Acquire());
    data.WriteAllBytes("host-secret.txt", System.Text.Encoding.UTF8.GetBytes(HostSecret));
    using Dir pluginData = data.OpenOrCreateDir("plugins");

    Console.WriteLine($"plugin data under {root}");
    foreach (IPlugin plugin in LoadPlugins(Path.Combine(AppContext.BaseDirectory, "plugins")))
    {
        Console.WriteLine();
        Console.WriteLine($"[{plugin.Name}]");

        // The plugin's own directory, restricted so that no symbolic link inside it is
        // followed: a plugin has no business leaving links for itself, and a link left by one
        // should not be something another component later trips over.
        using Dir own = pluginData.OpenOrCreateDir(plugin.Name);
        using Dir restricted = own.Restrict(SymlinkPolicy.Deny);

        using IndentedWriter log = new(Console.Out);
        plugin.Run(restricted, log);
    }

    // The snoop plugin writes whatever it managed to read into its own directory. Neither secret
    // may be there.
    string loot = pluginData.TryOpenDir("snoop", out Dir? snoop)
        ? Using(snoop, d => d.Exists("loot.txt") ? d.ReadAllText("loot.txt") : string.Empty)
        : string.Empty;

    bool leaked = loot.Contains(HostSecret, StringComparison.Ordinal) ||
                  loot.Contains(NotesSecret, StringComparison.Ordinal);

    Console.WriteLine();
    Console.WriteLine(leaked
        ? "A plugin read something outside its own directory."
        : "No plugin read anything outside its own directory.");
    return leaked ? 1 : 0;
}
finally
{
    if (scratch is not null)
    {
        Directory.Delete(scratch, recursive: true);
    }
}

static IEnumerable<IPlugin> LoadPlugins(string directory)
{
    foreach (string pluginDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
    {
        string assemblyPath = Path.Combine(pluginDirectory, Path.GetFileName(pluginDirectory) + ".dll");
        Assembly assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);

        foreach (Type type in assembly.GetExportedTypes())
        {
            if (typeof(IPlugin).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            {
                yield return (IPlugin)Activator.CreateInstance(type)!;
            }
        }
    }
}

static T Using<T>(Dir dir, Func<Dir, T> use)
{
    using (dir)
    {
        return use(dir);
    }
}

/// <summary>Indents what a plugin writes, so the log reads as belonging to it.</summary>
internal sealed class IndentedWriter(TextWriter inner) : TextWriter
{
    public override System.Text.Encoding Encoding => inner.Encoding;

    public override void WriteLine(string? value) => inner.WriteLine("  " + value);

    public override void Write(char value) => inner.Write(value);
}
