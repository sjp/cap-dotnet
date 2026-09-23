using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cap.Analyzers.Tests;

public sealed class CompositionRootTests
{
    [Fact]
    public async Task Taking_authority_in_the_entry_point_is_not_reported()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Cap.Primitives;

            public static class Program
            {
                public static async Task Main()
                {
                    AmbientAuthority authority = AmbientAuthority.Acquire();
                    await Task.Run(() => AmbientAuthority.Acquire());
                    Local();

                    static void Local() => AmbientAuthority.Acquire();
                }
            }
            """,
            OutputKind.ConsoleApplication);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Taking_authority_in_top_level_statements_is_not_reported()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using Cap.Primitives;

            AmbientAuthority authority = AmbientAuthority.Acquire();
            """,
            OutputKind.ConsoleApplication);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Taking_authority_anywhere_else_is_a_warning_that_names_the_place()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using Cap.Primitives;

            AmbientAuthority authority = AmbientAuthority.Acquire();

            public static class Worker
            {
                public static AmbientAuthority Reach() => AmbientAuthority.Acquire();
            }
            """,
            OutputKind.ConsoleApplication);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CAP0003", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Worker.Reach()", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_has_no_entry_point_and_so_no_implicit_composition_root()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using Cap.Primitives;

            public static class Program
            {
                public static void Main() => AmbientAuthority.Acquire();
            }
            """);

        Assert.Equal("CAP0003", Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("[CompositionRoot] public static class Startup { public static AmbientAuthority Take() => AmbientAuthority.Acquire(); }")]
    [InlineData("public static class Startup { [CompositionRoot] public static AmbientAuthority Take() => AmbientAuthority.Acquire(); }")]
    [InlineData("public sealed class Startup { [CompositionRoot] public Startup() => AmbientAuthority.Acquire(); }")]
    [InlineData("public static class Startup { [CompositionRoot] public static AmbientAuthority Token => AmbientAuthority.Acquire(); }")]
    [InlineData("[CompositionRoot] public static class Startup { public static class Inner { public static AmbientAuthority Take() => AmbientAuthority.Acquire(); } }")]
    public async Task Code_marked_as_a_composition_root_is_not_reported(string declaration)
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync("using Cap.Primitives;\n" + declaration);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_file_designated_in_editorconfig_is_a_composition_root()
    {
        const string Source = """
            using Cap.Primitives;

            public static class Startup
            {
                public static AmbientAuthority Take() => AmbientAuthority.Acquire();
            }
            """;

        var designated = await AnalyzerHarness.AnalyzeAsync(
            Source, editorConfig: new Dictionary<string, string> { ["cap_composition_root"] = "true" });
        var notDesignated = await AnalyzerHarness.AnalyzeAsync(
            Source, editorConfig: new Dictionary<string, string> { ["cap_composition_root"] = "false" });

        Assert.Empty(designated);
        Assert.Equal("CAP0003", Assert.Single(notDesignated).Id);
    }

    [Fact]
    public async Task A_strict_assembly_makes_it_an_error()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using Cap.Primitives;

            [assembly: CapabilityStrict]

            public static class Worker
            {
                public static AmbientAuthority Reach() => AmbientAuthority.Acquire();
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0003", DiagnosticSeverity.Error), (diagnostic.Id, diagnostic.Severity));
    }
}
