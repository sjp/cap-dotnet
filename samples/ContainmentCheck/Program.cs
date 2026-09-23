using Cap.Primitives;
using Cap.Std;

namespace ContainmentCheck;

/// <summary>
/// The containment check .NET code usually relies on, defeated by one symbolic link, and the
/// same read through a <see cref="Dir"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two blocks marked below are the ones the README opens with, character for character; a
/// test holds them to that. Everything else here only builds the scene they run in: a scratch
/// directory holding a sandbox and, beside it, a file the sandbox should never reveal, with a
/// link inside the sandbox pointing at the directory that holds it. Anyone who can write into
/// a directory can leave a link like that there — an upload, an extracted archive, a
/// tenant with a shell.
/// </para>
/// <para>
/// Exits 0 when both halves behave as the README says: the string check lets the read out,
/// and the handle refuses it. Anything else is a failure, and exits non-zero.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Secret = "the contents of a file outside the sandbox";

    internal static int Main()
    {
        string scratch = Directory.CreateTempSubdirectory("cap-containment-check-").FullName;
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(scratch, "sandbox")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(scratch, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "secret.txt"), Secret);

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "reports"), outside);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Windows grants the right to create links to administrators and to accounts
                // with Developer Mode on, and to nobody else.
                Console.Error.WriteLine($"This account cannot create symbolic links here: {e.Message}");
                return 3;
            }

            return Run(root);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static int Run(string root)
    {
        string userPath = "reports/secret.txt";
        Console.WriteLine($"sandbox:   {root}");
        Console.WriteLine($"requested: {userPath}, where 'reports' is a link to a directory outside");
        Console.WriteLine();

        bool leaked = StringCheck(root, userPath);
        bool refused = WithDir(root, userPath);

        return leaked && refused ? 0 : 1;
    }

    private static bool StringCheck(string root, string userPath)
    {
        try
        {
            // <string-check>
            string full = Path.GetFullPath(Path.Combine(root, userPath));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException();
            }
            string contents = File.ReadAllText(full);   // passes the check, reads outside
            // </string-check>

            Console.WriteLine($"string check: passed, and read \"{contents}\"");
            return contents == Secret;
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine("string check: refused (unexpected: the check was meant to be fooled)");
            return false;
        }
    }

    private static bool WithDir(string root, string userPath)
    {
        try
        {
            // <with-dir>
            using Dir dir = Dir.Open(root, AmbientAuthority.Acquire());
            string contents = dir.ReadAllText(userPath);   // throws SandboxEscapeException
            // </with-dir>

            Console.WriteLine($"Dir:          read \"{contents}\" (unexpected: this is an escape)");
            return false;
        }
        catch (SandboxEscapeException e)
        {
            Console.WriteLine($"Dir:          refused: {e.Message}");
            return true;
        }
    }
}
