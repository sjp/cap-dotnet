using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cap.Analyzers.Tests;

public sealed class AmbientApiTests
{
    private static readonly Dictionary<string, ReportDiagnostic> AllAmbientRulesOn = new()
    {
        ["CAP0001"] = ReportDiagnostic.Error,
        ["CAP0002"] = ReportDiagnostic.Error,
        ["CAP0006"] = ReportDiagnostic.Error,
        ["CAP0007"] = ReportDiagnostic.Error,
    };

    private const string EveryAmbientRoute = """
        using System;
        using System.IO;
        using System.Net;
        using System.Net.Sockets;
        using System.Threading.Tasks;

        public static class Uses
        {
            public static string Filesystem() => File.ReadAllText("/etc/passwd");
            public static void Network(Socket socket) => socket.Connect(IPAddress.Loopback, 80);
            public static DateTime Clock() => DateTime.UtcNow;
            public static Guid Entropy() => Guid.NewGuid();
        }
        """;

    [Fact]
    public async Task The_ambient_rules_say_nothing_until_a_project_turns_them_on()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(EveryAmbientRoute);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Each_ambient_route_is_reported_under_its_own_rule_once_turned_on()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(EveryAmbientRoute, severities: AllAmbientRulesOn);

        Assert.Collection(
            diagnostics,
            d => Assert.Equal(("CAP0001", "File.ReadAllText(\"/etc/passwd\")"), (d.Id, d.Flagged())),
            d => Assert.Equal(("CAP0002", "socket.Connect(IPAddress.Loopback, 80)"), (d.Id, d.Flagged())),
            d => Assert.Equal(("CAP0006", "DateTime.UtcNow"), (d.Id, d.Flagged())),
            d => Assert.Equal(("CAP0007", "Guid.NewGuid()"), (d.Id, d.Flagged())));
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task A_strict_assembly_turns_every_ambient_rule_on_as_an_error()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Strict(EveryAmbientRoute));

        Assert.Equal(["CAP0001", "CAP0002", "CAP0006", "CAP0007"], diagnostics.Select(d => d.Id));
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task A_strict_assembly_can_still_stand_one_rule_down_by_name()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Strict(EveryAmbientRoute),
            severities: new Dictionary<string, ReportDiagnostic> { ["CAP0006"] = ReportDiagnostic.Suppress });

        Assert.Equal(["CAP0001", "CAP0002", "CAP0007"], diagnostics.Select(d => d.Id));
    }

    [Fact]
    public async Task The_message_names_the_member_and_says_what_to_use_instead()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(EveryAmbientRoute, severities: AllAmbientRulesOn);

        string message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
        Assert.StartsWith("File.ReadAllText(", message, StringComparison.Ordinal);
        Assert.Contains("Cap.Std.Dir", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("new FileInfo(\"x\").Length", "CAP0001")]
    [InlineData("new DirectoryInfo(\"x\").Exists", "CAP0001")]
    [InlineData("Directory.GetFiles(\"x\").Length", "CAP0001")]
    [InlineData("Environment.CurrentDirectory", "CAP0001")]
    [InlineData("Path.GetFullPath(\"x\")", "CAP0001")]
    [InlineData("Path.GetTempPath()", "CAP0001")]
    [InlineData("new FileStream(\"x\", FileMode.Open)", "CAP0001")]
    [InlineData("new StreamReader(\"x\")", "CAP0001")]
    [InlineData("new StreamWriter(\"x\", true)", "CAP0001")]
    [InlineData("new TcpClient(\"example.com\", 80)", "CAP0002")]
    [InlineData("new TcpListener(IPAddress.Any, 80)", "CAP0002")]
    [InlineData("Dns.GetHostAddresses(\"example.com\")", "CAP0002")]
    [InlineData("DateTimeOffset.Now", "CAP0006")]
    [InlineData("TimeProvider.System", "CAP0006")]
    [InlineData("Task.Delay(10)", "CAP0006")]
    [InlineData("new Random()", "CAP0007")]
    [InlineData("Random.Shared.Next()", "CAP0007")]
    [InlineData("RandomNumberGenerator.GetInt32(10)", "CAP0007")]
    [InlineData("Path.GetRandomFileName()", "CAP0007")]
    public async Task Each_listed_route_is_reported(string expression, string rule)
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(Wrap(expression), severities: AllAmbientRulesOn);

        // A chain such as new FileInfo(path).Length is two uses and is reported twice.
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal(rule, d.Id));
    }

    [Theory]
    [InlineData("Path.GetFileName(\"a\")")]
    [InlineData("new FileStream(new SafeFileHandle(), FileAccess.Read)")]
    [InlineData("new StreamReader(Stream.Null)")]
    [InlineData("Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System is var p ? p : p)")]
    [InlineData("Stopwatch.GetTimestamp()")]
    [InlineData("nameof(File.ReadAllText)")]
    [InlineData("nameof(Environment.CurrentDirectory)")]
    public async Task What_reaches_nothing_by_itself_is_not_reported(string expression)
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(Wrap(expression), severities: AllAmbientRulesOn);

        // TimeProvider.System is itself the clock, so the one case that names it expects that
        // single report and nothing for the overload of Task.Delay that takes a provider.
        Assert.All(diagnostics, d => Assert.Equal("TimeProvider.System", d.Flagged()));
    }

    [Fact]
    public async Task A_method_group_is_reported_as_a_call_would_be()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Wrap("(Func<string, string>)File.ReadAllText"), severities: AllAmbientRulesOn);

        Assert.Equal("CAP0001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task A_method_listed_without_parameters_stands_for_every_overload()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System;
            using System.Threading;

            public static class Uses
            {
                public static void Wait()
                {
                    Thread.Sleep(10);
                    Thread.Sleep(TimeSpan.FromMilliseconds(10));
                }
            }
            """,
            severities: AllAmbientRulesOn);

        Assert.Equal(["CAP0006", "CAP0006"], diagnostics.Select(d => d.Id));
    }

    [Fact]
    public async Task Every_entry_in_the_built_in_lists_names_something_that_exists()
    {
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "Probe", references: AnalyzerHarness.WithFileSystemAbstractions);

        string[] unresolved =
        [
            .. CapabilityAnalyzer.BuiltInSymbolLists
                .SelectMany(list => list.List.Entries.Select(entry => (list.Rule.Id, entry.Id)))
                .Where(entry => !SymbolList.Resolve(entry.Item2, compilation).Any())
                .Select(entry => $"{entry.Item1}: {entry.Item2}"),
        ];

        Assert.Empty(unresolved);
        Assert.All(
            CapabilityAnalyzer.BuiltInSymbolLists,
            list => Assert.All(list.List.Entries, entry => Assert.NotEqual(string.Empty, entry.Message)));
    }

    private static string Strict(string source) =>
        source.Replace("public static class", "[assembly: Cap.Primitives.CapabilityStrict]\n\npublic static class", StringComparison.Ordinal);

    private static string Wrap(string expression) => $$"""
        using System;
        using System.Diagnostics;
        using System.IO;
        using System.Net;
        using System.Net.Sockets;
        using System.Security.Cryptography;
        using System.Threading.Tasks;
        using Microsoft.Win32.SafeHandles;

        public static class Uses
        {
            public static object Value() => {{expression}};
        }
        """;
}
