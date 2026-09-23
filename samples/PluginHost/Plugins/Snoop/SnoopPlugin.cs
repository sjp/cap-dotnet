using System.Text;
using Cap.Primitives;
using Cap.Std;
using PluginHost.Contracts;

[assembly: CapabilityStrict]

namespace PluginHost.Plugins.Snoop;

/// <summary>
/// A plugin that tries to read what is not its own, through the only handle it has.
/// </summary>
/// <remarks>
/// <para>
/// It cannot reach for anything else. With <c>CapabilityStrict</c> on, a call such as
/// <c>File.ReadAllText("/etc/passwd")</c> or <c>AmbientAuthority.Acquire()</c> here fails the
/// build, so the attempts below are the ones left to it: names handed to the directory it was
/// given, spelled to lead somewhere else.
/// </para>
/// <para>
/// Whatever it does manage to read, it writes to <c>loot.txt</c> in its own directory, where
/// the host looks for it afterwards.
/// </para>
/// </remarks>
public sealed class SnoopPlugin : IPlugin
{
    private static readonly string[] Targets =
    [
        "../notes/notes.txt",
        "../host-secret.txt",
        "../../host-secret.txt",
        "/etc/passwd",
        "C:/Windows/win.ini",
    ];

    /// <inheritdoc/>
    public string Name => "snoop";

    /// <inheritdoc/>
    public void Run(Dir data, TextWriter log)
    {
        StringBuilder loot = new();

        foreach (string target in Targets)
        {
            try
            {
                loot.AppendLine(data.ReadAllText(target));
                log.WriteLine($"read     {target}");
            }
            catch (SandboxEscapeException)
            {
                log.WriteLine($"refused  {target}  (outside this plugin's directory)");
            }
            catch (IOException e)
            {
                log.WriteLine($"failed   {target}  ({e.GetType().Name})");
            }
        }

        data.WriteAllBytes("loot.txt", Encoding.UTF8.GetBytes(loot.ToString()));
    }
}
