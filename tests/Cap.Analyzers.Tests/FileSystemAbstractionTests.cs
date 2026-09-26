using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cap.Analyzers.Tests;

/// <summary>
/// CAP0001 over System.IO.Abstractions and Testably.Abstractions: constructing their ambient
/// implementations is the filesystem reached by path, and taking an <c>IFileSystem</c> is not.
/// </summary>
public sealed class FileSystemAbstractionTests
{
    private static readonly Dictionary<string, ReportDiagnostic> FilesystemRuleOn = new()
    {
        ["CAP0001"] = ReportDiagnostic.Error,
    };

    [Theory]
    [InlineData("new FileSystem()")]
    [InlineData("new FileWrapper(fs)")]
    [InlineData("new DirectoryWrapper(fs)")]
    [InlineData("new PathWrapper(fs)")]
    [InlineData("new FileSystemWatcherWrapper(fs)")]
    [InlineData("new FileSystemWatcherWrapper(fs, \"/srv\", \"*.txt\")")]
    [InlineData("new FileSystemWatcherFactory(fs)")]
    [InlineData("new Testably.Abstractions.RealFileSystem()")]
    public async Task Constructing_an_ambient_implementation_is_reported(string creation)
    {
        var diagnostics = await Analyze($$"""
            public static class Uses
            {
                public static object Create(IFileSystem fs) => {{creation}};
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0001", creation), (diagnostic.Id, diagnostic.Flagged()));
    }

    [Theory]
    [InlineData("FileInfo", "new FileInfoWrapper(fs, info)")]
    [InlineData("DirectoryInfo", "new DirectoryInfoWrapper(fs, info)")]
    [InlineData("DriveInfo", "new DriveInfoWrapper(fs, info)")]
    public async Task Wrapping_a_path_based_info_object_is_reported(string infoType, string creation)
    {
        var diagnostics = await Analyze($$"""
            public static class Uses
            {
                public static object Wrap(IFileSystem fs, {{infoType}} info) => {{creation}};
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0001", creation), (diagnostic.Id, diagnostic.Flagged()));
    }

    [Theory]
    [InlineData("new FileSystem()")]
    [InlineData("new Testably.Abstractions.RealFileSystem()")]
    public async Task The_message_points_at_DirFileSystem(string creation)
    {
        var diagnostics = await Analyze($$"""
            public static class Uses
            {
                public static IFileSystem Create() => {{creation}};
            }
            """);

        string message = Assert.Single(diagnostics).GetMessage(CultureInfo.InvariantCulture);
        Assert.Contains("Cap.IO.Abstractions.DirFileSystem", message, StringComparison.Ordinal);
        Assert.Contains("Cap.Std.Dir", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Taking_an_IFileSystem_and_building_a_DirFileSystem_are_not_reported()
    {
        var diagnostics = await Analyze("""
            using Cap.IO.Abstractions;
            using Cap.Std;

            public sealed class Reports(IFileSystem fs)
            {
                public static Reports Over(Dir root) => new(new DirFileSystem(root));

                public string Read(string name)
                {
                    IFileInfo info = fs.FileInfo.New(fs.Path.Combine("reports", name));
                    fs.Directory.CreateDirectory("archive");
                    return info.Exists ? fs.File.ReadAllText(info.FullName) : string.Empty;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task The_entries_match_by_name_whichever_assembly_defines_the_type()
    {
        // No reference to System.IO.Abstractions at all: the type is declared here, under the
        // same name. A project that sees these types through some other assembly, or a copy
        // of the library the analyzer was not built against, is covered all the same.
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            namespace System.IO.Abstractions
            {
                public class FileSystem { }
            }

            public static class Uses
            {
                public static object Create() => new System.IO.Abstractions.FileSystem();
            }
            """,
            severities: FilesystemRuleOn);

        Assert.Equal("CAP0001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task Nothing_is_reported_while_CAP0001_is_off()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System.IO.Abstractions;

            public static class Uses
            {
                public static IFileSystem Create() => new FileSystem();
            }
            """,
            references: AnalyzerHarness.WithFileSystemAbstractions);

        Assert.Empty(diagnostics);
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Analyze(string source) =>
        AnalyzerHarness.AnalyzeAsync(
            "using System.IO;\nusing System.IO.Abstractions;\n\n" + source,
            severities: FilesystemRuleOn,
            references: AnalyzerHarness.WithFileSystemAbstractions);
}
