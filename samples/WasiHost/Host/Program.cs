using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cap.Primitives;
using Cap.Rand;
using Cap.Std;
using Cap.Time;
using Wasmtime;
using WasiHost.Preview1;

namespace WasiHost;

/// <summary>
/// A WASI host whose guests' filesystem is a set of <see cref="Dir"/> handles.
/// </summary>
/// <remarks>
/// <para>
/// This is where authority enters: each preopened directory is opened here, and the clock
/// and entropy source are taken here, then all of it is handed to the adapter, which can
/// reach nothing else. The guest reaches only what the adapter was handed.
/// </para>
/// <para>
/// With no arguments, it runs a demonstration and exits non-zero if the guest read anything
/// outside its directory. <c>run</c> runs a module of your own:
/// </para>
/// <code>
/// WasiHost run [--dir HOST::GUEST]... [--env NAME=VALUE]... module.wasm [argument]...
/// </code>
/// </remarks>
internal static partial class Program
{
    private const string Secret = "a file beside the guest's directory";

    internal static int Main(string[] args) =>
        args is ["run", .. string[] rest] ? RunModule(rest)
        : args.Length == 0 ? Demonstrate()
        : Usage();

    private static int Usage()
    {
        Console.Error.WriteLine("usage: WasiHost run [--dir HOST::GUEST]... [--env NAME=VALUE]... module.wasm [argument]...");
        return 2;
    }

    /// <summary>
    /// Runs a guest that tries to read a list of paths, some inside the directory it was given
    /// and some reaching out of it by every route that works against a string check.
    /// </summary>
    private static int Demonstrate()
    {
        string scratch = Directory.CreateTempSubdirectory("cap-wasi-host-").FullName;
        try
        {
            string guestRoot = Directory.CreateDirectory(Path.Join(scratch, "guest")).FullName;
            string secretPath = Path.Join(scratch, "secret.txt");
            File.WriteAllText(secretPath, Secret);
            File.WriteAllText(Path.Join(guestRoot, "notes.txt"), "the guest's own notes");
            Directory.CreateDirectory(Path.Join(guestRoot, "docs"));
            File.WriteAllText(Path.Join(guestRoot, "docs", "readme.txt"), "a document the guest was given");

            List<string> attempts =
            [
                "notes.txt",
                "docs/readme.txt",
                "../secret.txt",
                "docs/../../secret.txt",
                secretPath,
            ];

            // Windows grants the right to create links to administrators and to accounts with
            // Developer Mode on, and to nobody else; without it, the link attempts are left out.
            try
            {
                File.CreateSymbolicLink(Path.Join(guestRoot, "shortcut"), Path.Join("..", "secret.txt"));
                File.CreateSymbolicLink(Path.Join(guestRoot, "absolute"), secretPath);
                attempts.Add("shortcut");
                attempts.Add("absolute");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                Console.WriteLine($"(This account cannot create symbolic links here, so none are tried: {e.Message})");
            }

            string guest = new StreamReader(
                typeof(Program).Assembly.GetManifestResourceStream("read-each-argument.wat")!).ReadToEnd();

            using Engine engine = new();
            using Module module = Module.FromText(engine, "read-each-argument", guest);
            MemoryStream output = new();
            int code = WasiProgram.Run(engine, module, new WasiOptions
            {
                Arguments = ["read-each-argument", .. attempts],
                Clock = CapClock.System(AmbientAuthority.Acquire()),
                Random = CapRandom.System(AmbientAuthority.Acquire()),
                Preopens = [("/", Dir.Open(guestRoot, AmbientAuthority.Acquire()))],
                StandardOutput = output,
            });

            string printed = Encoding.UTF8.GetString(output.ToArray());
            Console.WriteLine($"The guest was given {guestRoot} as '/', and tried to read:");
            foreach (string line in printed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Console.WriteLine("  " + ErrnoNumber().Replace(line, Name));
            }

            if (code != 0 || printed.Contains(Secret, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("The guest read a file outside the directory it was given.");
                return 1;
            }

            return 0;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Runs a module with the directories, environment and arguments given.</summary>
    private static int RunModule(string[] args)
    {
        List<(string, Dir)> preopens = [];
        List<KeyValuePair<string, string>> environment = [];
        int i = 0;
        for (; i < args.Length - 1 && args[i] is "--dir" or "--env"; i += 2)
        {
            string value = args[i + 1];
            if (args[i] == "--dir")
            {
                int separator = value.IndexOf("::", StringComparison.Ordinal);
                string host = separator < 0 ? value : value[..separator];
                string guestName = separator < 0 ? value : value[(separator + 2)..];
                preopens.Add((guestName, Dir.Open(host, AmbientAuthority.Acquire())));
            }
            else
            {
                int equals = value.IndexOf('=', StringComparison.Ordinal);
                environment.Add(equals < 0 ? new(value, string.Empty) : new(value[..equals], value[(equals + 1)..]));
            }
        }

        if (i >= args.Length)
        {
            return Usage();
        }

        using Engine engine = new();
        using Module module = Module.FromFile(engine, args[i]);
        try
        {
            return WasiProgram.Run(engine, module, new WasiOptions
            {
                Arguments = [Path.GetFileName(args[i]), .. args[(i + 1)..]],
                Environment = environment,
                Clock = CapClock.System(AmbientAuthority.Acquire()),
                Random = CapRandom.System(AmbientAuthority.Acquire()),
                Preopens = preopens,
                StandardInput = Console.OpenStandardInput(),
                StandardOutput = Console.OpenStandardOutput(),
                StandardError = Console.OpenStandardError(),
            });
        }
        catch (TrapException e)
        {
            Console.Error.WriteLine(e.Message);
            return 134;
        }
    }

    /// <summary>The C name of an error code the guest printed as a number: 76 is ENOTCAPABLE.</summary>
    private static string Name(Match number)
    {
        Errno errno = (Errno)int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture);
        return "E" + errno.ToString().ToUpperInvariant();
    }

    [GeneratedRegex(@"errno (\d+)")]
    private static partial Regex ErrnoNumber();
}
