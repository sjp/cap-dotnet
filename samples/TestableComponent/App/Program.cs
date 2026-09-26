using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using Cap.IO.Abstractions;
using Cap.Primitives;
using Cap.Std;

namespace TestableComponent;

/// <summary>
/// The composition root for two components: <see cref="ReportStore"/>, written against
/// <see cref="IDir"/>, and <see cref="UploadStore"/>, written against System.IO.Abstractions'
/// <see cref="IFileSystem"/> and confined by being handed a <see cref="DirFileSystem"/>.
/// </summary>
/// <remarks>
/// <para>
/// The block marked below is the one the documentation shows. Everything else builds a scene
/// for it to run in: a data directory, and beside it a private directory the application must
/// never reach, with a link inside the uploads directory pointing at it, as an extracted
/// archive or a careless deployment might leave.
/// </para>
/// <para>
/// Exits 0 when the reports round-trip, an ordinary upload is stored, and every upload name
/// that leads outside is refused. Anything else exits non-zero.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Secret = "the contents of a file outside the data directory";

    internal static int Main()
    {
        string scratch = Directory.CreateTempSubdirectory("cap-testable-component-").FullName;
        try
        {
            string dataPath = Directory.CreateDirectory(Path.Combine(scratch, "data")).FullName;
            string privatePath = Directory.CreateDirectory(Path.Combine(scratch, "private")).FullName;
            File.WriteAllText(Path.Combine(privatePath, "secret.txt"), Secret);
            string uploadsPath = Directory.CreateDirectory(Path.Combine(dataPath, "uploads")).FullName;

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(uploadsPath, "shared"), privatePath);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Windows grants the right to create links to administrators and to accounts
                // with Developer Mode on, and to nobody else.
                Console.Error.WriteLine($"This account cannot create symbolic links here: {e.Message}");
                return 3;
            }

            bool confined = Run(dataPath);
            bool untouched = Directory.GetFileSystemEntries(privatePath).Length == 1
                && File.ReadAllText(Path.Combine(privatePath, "secret.txt")) == Secret
                && !File.Exists(Path.Combine(scratch, "escaped.txt"));

            Console.WriteLine();
            Console.WriteLine(untouched ? "private directory: untouched" : "private directory: CHANGED");
            return confined && untouched ? 0 : 1;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static bool Run(string dataPath)
    {
        // <composition-root>
        using Dir data = Dir.Open(dataPath, AmbientAuthority.Acquire());
        using Dir reportsDir = data.OpenOrCreateDir("reports");
        using Dir uploadsDir = data.OpenDir("uploads");

        var reports = new ReportStore(reportsDir);
        var uploads = new UploadStore(new DirFileSystem(uploadsDir), "/");
        // </composition-root>

        bool ok = true;

        DateOnly day = new(2026, 9, 1);
        reports.Save(day, """{ "total": 3 }""");
        reports.Save(day.AddDays(1), """{ "total": 5 }""");
        string listed = string.Join(", ", reports.ListDays().Select(d => d.ToString("O", CultureInfo.InvariantCulture)));
        Console.WriteLine($"reports:  {listed}; {day:O} holds {reports.Load(day)}");
        ok &= reports.ListDays().Count == 2 && reports.Load(day) == """{ "total": 3 }""";

        uploads.Save("avatar.png", [0x89, 0x50, 0x4E, 0x47]);
        Console.WriteLine($"upload:   avatar.png stored, {uploads.Load("avatar.png").Length} bytes");

        ok &= Refused("load shared/secret.txt", () => Encoding.UTF8.GetString(uploads.Load("shared/secret.txt")));
        ok &= Refused("save shared/planted.txt", () => uploads.Save("shared/planted.txt", [1]));
        ok &= Refused("save ../../escaped.txt", () => uploads.Save("../../escaped.txt", [1]));
        return ok;
    }

    private static bool Refused(string what, Func<object?> attempt)
    {
        try
        {
            object? result = attempt();
            Console.WriteLine($"upload:   {what}: succeeded (unexpected: this reached outside) {result}");
            return false;
        }
        catch (SandboxEscapeException e)
        {
            Console.WriteLine($"upload:   {what}: refused: {e.Message}");
            return true;
        }
    }

    private static bool Refused(string what, Action attempt) => Refused(what, () =>
    {
        attempt();
        return null;
    });
}
