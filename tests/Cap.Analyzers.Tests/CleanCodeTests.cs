using Microsoft.CodeAnalysis;

namespace Cap.Analyzers.Tests;

public sealed class CleanCodeTests
{
    [Fact]
    public async Task A_program_written_against_capabilities_draws_no_diagnostics_even_when_strict()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System;
            using System.IO;
            using Cap.Primitives;
            using Cap.Std;

            [assembly: CapabilityStrict]

            using Dir root = Dir.Open(args[0], AmbientAuthority.Acquire());
            using Dir users = root.OpenDir("users");
            Console.WriteLine(Reports.Summarise(users, args[1]));

            public static class Reports
            {
                public static string Summarise(Dir users, string name)
                {
                    using Dir user = users.OpenDir(name);
                    string text = user.ReadAllText("report.txt");
                    using CapFile log = user.OpenFile("audit.log", FileMode.Append, FileAccess.Write);
                    return Path.GetFileName(name) + ": " + text.Length;
                }
            }
            """,
            OutputKind.ConsoleApplication);

        Assert.Empty(diagnostics);
    }
}
