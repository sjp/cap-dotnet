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

    [Fact]
    public async Task A_project_type_ban_covers_the_types_derived_from_it()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System.IO;

            public static class Uses
            {
                public static Stream Open() => new MemoryStream();
            }
            """,
            additionalFiles: [("/project/CapBannedSymbols.txt", "T:System.IO.Stream;Take the stream you were handed.")]);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0008", "new MemoryStream()"), (diagnostic.Id, diagnostic.Flagged()));
    }

    [Fact]
    public async Task A_project_member_ban_covers_the_members_that_override_it()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System.IO;

            public static class Uses
            {
                public static void Flush(MemoryStream stream) => stream.Flush();
                public static void Seek(MemoryStream stream) => stream.Seek(0, SeekOrigin.Begin);
            }
            """,
            additionalFiles: [("/project/CapBannedSymbols.txt", "M:System.IO.Stream.Flush")]);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0008", "stream.Flush()"), (diagnostic.Id, diagnostic.Flagged()));
    }

    [Fact]
    public async Task A_project_ban_on_an_interface_does_not_cover_what_implements_it()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System;
            using System.IO;

            public static class Uses
            {
                public static void Close(MemoryStream stream) => stream.Dispose();
                public static void Close(IDisposable disposable) => disposable.Dispose();
            }
            """,
            additionalFiles: [("/project/CapBannedSymbols.txt", "T:System.IDisposable")]);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0008", "disposable.Dispose()"), (diagnostic.Id, diagnostic.Flagged()));
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
