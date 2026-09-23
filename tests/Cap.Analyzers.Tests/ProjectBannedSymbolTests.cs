using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cap.Analyzers.Tests;

public sealed class ProjectBannedSymbolTests
{
    private const string Source = """
        using System;
        using System.IO;

        public static class Uses
        {
            public static void Print() => Console.WriteLine("hi");
            public static string Read() => File.ReadAllText("x");
            public static string Machine() => Environment.MachineName;
        }
        """;

    [Fact]
    public async Task A_project_can_add_its_own_bans_in_the_banned_symbols_format()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Source,
            additionalFiles:
            [
                ("/project/CapBannedSymbols.txt", """
                    # Output goes through the logger.
                    M:System.Console.WriteLine(System.String);Write through the ILogger that was passed in.

                    P:System.Environment.MachineName
                    T:Some.Assembly.This.Project.Does.Not.Reference
                    """),
            ]);

        Assert.Collection(
            diagnostics,
            d =>
            {
                Assert.Equal(("CAP0008", DiagnosticSeverity.Warning), (d.Id, d.Severity));
                Assert.Equal("Console.WriteLine(string?): Write through the ILogger that was passed in.", d.GetMessage(CultureInfo.InvariantCulture));
            },
            d => Assert.Equal(("CAP0008", "Environment.MachineName"), (d.Id, d.Flagged())));
    }

    [Fact]
    public async Task A_project_ban_on_a_framework_api_applies_without_turning_on_the_rule_that_covers_it()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Source,
            additionalFiles: [("/project/CapBannedSymbols.Io.txt", "T:System.IO.File;Not in this project.")]);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0008", "File.ReadAllText(\"x\")"), (diagnostic.Id, diagnostic.Flagged()));
    }

    [Theory]
    [InlineData("/project/CapBannedSymbols.txt", true)]
    [InlineData("/project/capbannedsymbols.txt", true)]
    [InlineData("/project/CapBannedSymbols.Network.txt", true)]
    [InlineData("/project/BannedSymbols.txt", false)]
    [InlineData("/project/CapBannedSymbolsExtra.txt", false)]
    [InlineData("/project/CapBannedSymbols.md", false)]
    public void Only_files_named_for_this_analyzer_are_read(string path, bool read) =>
        Assert.Equal(read, CapabilityAnalyzer.IsProjectList(path));
}
