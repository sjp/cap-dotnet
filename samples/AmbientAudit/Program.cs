using Cap.Primitives;
using Cap.Std;

namespace AmbientAudit;

/// <summary>
/// Prints every place this process reached outside itself.
/// </summary>
/// <remarks>
/// <para>
/// The program is a report writer, arranged the way an application using this library is
/// meant to be: authority enters once, at the top, and what it produces — a directory handle
/// — is passed down to the code that does the work. <see cref="WriteReport"/> can write into
/// the workspace and cannot write anywhere else, and it needs no permission checks to be
/// sure of that, because it was never handed the ability to reach further.
/// </para>
/// <para>
/// It also contains one piece of code that does not play along. <see cref="Templates"/>
/// reaches for the system temporary directory on its own, every time it is called — which is
/// the interesting case, because that is what a dependency does, and no amount of care taken
/// in this file prevents it. The dump at the end of the run names it anyway.
/// </para>
/// <para>
/// The recording is off unless asked for; this program asks for it in its project file. See
/// <c>docs/ambient-authority.md</c>.
/// </para>
/// </remarks>
internal static class Program
{
    internal static void Main()
    {
        // Ordinary ambient .NET, outside the capability graph: somewhere for the sample to
        // work, created and removed by the sample itself.
        string workspace = Directory.CreateTempSubdirectory("cap-ambient-audit-").FullName;

        try
        {
            // The composition root. The one line in this program that turns a path into
            // authority, and the reason it is spelt out rather than implied.
            using Dir reports = Dir.Open(workspace, AmbientAuthority.Acquire());

            foreach (string month in (string[])["january", "february", "march"])
            {
                WriteReport(reports, month);
            }

            IEnumerable<string> written = reports
                .EnumerateEntries()
                .Select(entry => entry.Name)
                .Order(StringComparer.Ordinal);

            Console.WriteLine($"Wrote {string.Join(", ", written)} into the workspace.");
            Console.WriteLine();
            Console.WriteLine(AmbientAuthority.DescribeRecordedSites());
            Console.WriteLine();
            Console.WriteLine(
                "The second entry is the one worth noticing: nothing in Main took that authority, " +
                "and nothing in Main could have stopped it.");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// Writes one report. Takes no authority: it was handed some.
    /// </summary>
    /// <remarks>
    /// Nothing here appears in the dump, which is the point. A name arriving from outside —
    /// <c>../../etc/passwd</c>, say — resolves beneath the handle or not at all.
    /// </remarks>
    private static void WriteReport(Dir reports, string month)
    {
        reports.WriteAllBytes($"{month}.txt", Templates.Render(month));
    }
}

/// <summary>
/// The part of the program that reaches outside on its own.
/// </summary>
/// <remarks>
/// Stands in for a dependency: it looks in the system temporary directory for a template to
/// override the built-in one, and takes the authority to do that itself, once per call. It
/// is not doing anything unusual — this is how most libraries are written — and that is
/// precisely why an application wants the list.
/// </remarks>
internal static class Templates
{
    internal static byte[] Render(string month)
    {
        using Dir shared = Dir.Open(Path.GetTempPath(), AmbientAuthority.Acquire());

        string template = shared.Exists("cap-report-template.txt")
            ? shared.ReadAllText("cap-report-template.txt")
            : "Report for {month}.";

        return System.Text.Encoding.UTF8.GetBytes(template.Replace("{month}", month, StringComparison.Ordinal));
    }
}
