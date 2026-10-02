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
    public async Task A_strict_assembly_still_reports_when_every_default_on_rule_is_off()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Strict("""
                using System.IO;

                public static class Uses
                {
                    public static string Filesystem() => File.ReadAllText("x");
                }
                """),
            severities: new Dictionary<string, ReportDiagnostic>
            {
                ["CAP0000"] = ReportDiagnostic.Suppress,
                ["CAP0003"] = ReportDiagnostic.Suppress,
                ["CAP0004"] = ReportDiagnostic.Suppress,
                ["CAP0005"] = ReportDiagnostic.Suppress,
                ["CAP0008"] = ReportDiagnostic.Suppress,
            });

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0001", DiagnosticSeverity.Error), (diagnostic.Id, diagnostic.Severity));
    }

    [Fact]
    public async Task The_rule_that_keeps_the_analyzer_running_is_never_reported()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Strict(EveryAmbientRoute),
            severities: new Dictionary<string, ReportDiagnostic> { ["CAP0000"] = ReportDiagnostic.Error });

        Assert.DoesNotContain(diagnostics, d => d.Id == "CAP0000");
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
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)", "CAP0001")]
    [InlineData("System.Reflection.Assembly.LoadFrom(\"x\")", "CAP0001")]
    [InlineData("System.Reflection.Assembly.LoadFile(\"x\")", "CAP0001")]
    [InlineData("System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(\"x\")", "CAP0001")]
    [InlineData("System.Runtime.InteropServices.NativeLibrary.Load(\"x\")", "CAP0001")]
    [InlineData("System.Xml.Linq.XDocument.Load(\"x\")", "CAP0001")]
    [InlineData("System.Xml.Linq.XElement.Load(\"x\", System.Xml.Linq.LoadOptions.None)", "CAP0001")]
    [InlineData("System.Xml.XmlReader.Create(\"x\")", "CAP0001")]
    [InlineData("System.Xml.XmlWriter.Create(\"x\")", "CAP0001")]
    [InlineData("new System.Xml.XmlTextReader(\"x\")", "CAP0001")]
    [InlineData("new System.Xml.XPath.XPathDocument(\"x\")", "CAP0001")]
    [InlineData("System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(\"x\")", "CAP0001")]
    [InlineData("new System.IO.Pipes.NamedPipeClientStream(\"x\")", "CAP0001")]
    [InlineData("new System.IO.Pipes.NamedPipeServerStream(\"x\")", "CAP0001")]
    [InlineData("System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(\"x\")", "CAP0001")]
    [InlineData("new System.Security.Cryptography.X509Certificates.X509Certificate2(\"x\")", "CAP0001")]
    [InlineData("System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(\"x\")", "CAP0001")]
    [InlineData("new TcpClient(\"example.com\", 80)", "CAP0002")]
    [InlineData("new TcpListener(IPAddress.Any, 80)", "CAP0002")]
    [InlineData("Dns.GetHostAddresses(\"example.com\")", "CAP0002")]
    [InlineData("new System.Net.Http.HttpClient()", "CAP0002")]
    [InlineData("new System.Net.Http.SocketsHttpHandler()", "CAP0002")]
    [InlineData("new System.Net.Http.HttpClientHandler()", "CAP0002")]
    [InlineData("new System.Net.WebSockets.ClientWebSocket()", "CAP0002")]
    [InlineData("new System.Net.Mail.SmtpClient(\"example.com\")", "CAP0002")]
    [InlineData("new System.Net.NetworkInformation.Ping()", "CAP0002")]
    [InlineData("System.Net.Quic.QuicConnection.ConnectAsync(null!)", "CAP0002")]
    [InlineData("System.Net.Quic.QuicListener.ListenAsync(null!)", "CAP0002")]
    [InlineData("new HttpListener()", "CAP0002")]
    [InlineData("new WebClient()", "CAP0002")]
    [InlineData("WebRequest.Create(\"https://example.com\")", "CAP0002")]
    [InlineData("WebRequest.CreateHttp(\"https://example.com\")", "CAP0002")]
    [InlineData("WebRequest.CreateDefault(new Uri(\"https://example.com\"))", "CAP0002")]
    [InlineData("DateTimeOffset.Now", "CAP0006")]
    [InlineData("TimeProvider.System", "CAP0006")]
    [InlineData("Task.Delay(10)", "CAP0006")]
    [InlineData("Task.CompletedTask.WaitAsync(TimeSpan.FromSeconds(1))", "CAP0006")]
    [InlineData("Task.FromResult(1).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)", "CAP0006")]
    [InlineData("new CancellationTokenSource(TimeSpan.FromSeconds(1))", "CAP0006")]
    [InlineData("new CancellationTokenSource(1000)", "CAP0006")]
    [InlineData("new PeriodicTimer(TimeSpan.FromSeconds(1))", "CAP0006")]
    [InlineData("new Timer(_ => { }, null, 1000, 1000)", "CAP0006")]
    [InlineData("new System.Timers.Timer(1000)", "CAP0006")]
    [InlineData("new Random()", "CAP0007")]
    [InlineData("Random.Shared.Next()", "CAP0007")]
    [InlineData("RandomNumberGenerator.GetInt32(10)", "CAP0007")]
    [InlineData("new RNGCryptoServiceProvider()", "CAP0007")]
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
    [InlineData("Process.GetCurrentProcess().Id")]
    [InlineData("System.Xml.Linq.XDocument.Load(Stream.Null)")]
    [InlineData("System.Xml.XmlReader.Create(Stream.Null)")]
    [InlineData("new System.IO.Pipes.NamedPipeServerStream(System.IO.Pipes.PipeDirection.In, false, false, new SafePipeHandle())")]
    [InlineData("System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(new SafeFileHandle(), null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, false)")]
    [InlineData("System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(new byte[0])")]
    [InlineData("Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System is var p ? p : p)")]
    [InlineData("Task.CompletedTask.WaitAsync(TimeSpan.FromSeconds(1), TimeProvider.System is var p ? p : p)")]
    [InlineData("new CancellationTokenSource(TimeSpan.FromSeconds(1), TimeProvider.System is var p ? p : p)")]
    [InlineData("new PeriodicTimer(TimeSpan.FromSeconds(1), TimeProvider.System is var p ? p : p)")]
    [InlineData("new CancellationTokenSource()")]
    [InlineData("Task.CompletedTask.WaitAsync(CancellationToken.None)")]
    [InlineData("Stopwatch.GetTimestamp()")]
    [InlineData("Environment.TickCount64")]
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
    public async Task Path_based_extraction_loading_and_sending_are_reported()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System.Diagnostics;
            using System.Formats.Tar;
            using System.IO.Compression;
            using System.Net.Sockets;
            using System.Xml.Linq;

            public static class Uses
            {
                public static void Reach(ZipArchive archive, TarEntry tar, XDocument document, Socket socket, string path)
                {
                    TarFile.ExtractToDirectory(path, path, false);
                    archive.ExtractToDirectory(path);
                    ZipFileExtensions.ExtractToDirectory(archive, path, true);
                    archive.Entries[0].ExtractToFile(path);
                    archive.CreateEntryFromFile(path, "entry");
                    tar.ExtractToFile(path, false);
                    document.Save(path);
                    Process.Start(path);
                    Process.Start(new ProcessStartInfo(path));
                    socket.SendFile(path);
                }
            }
            """,
            severities: AllAmbientRulesOn);

        Assert.Equal(
            [
                "TarFile.ExtractToDirectory(path, path, false)",
                "archive.ExtractToDirectory(path)",
                "ZipFileExtensions.ExtractToDirectory(archive, path, true)",
                "archive.Entries[0].ExtractToFile(path)",
                "archive.CreateEntryFromFile(path, \"entry\")",
                "tar.ExtractToFile(path, false)",
                "document.Save(path)",
                "Process.Start(path)",
                "Process.Start(new ProcessStartInfo(path))",
                "socket.SendFile(path)",
            ],
            diagnostics.Select(d => d.Flagged()));
        Assert.All(diagnostics, d => Assert.Equal("CAP0001", d.Id));
    }

    [Fact]
    public async Task A_by_name_client_is_reported_where_it_is_built_and_not_where_it_is_used()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System.Net.Http;
            using System.Net.WebSockets;
            using System.Threading;
            using System.Threading.Tasks;

            public static class Uses
            {
                public static HttpClient Build()
                {
                    var handler = new SocketsHttpHandler();
                    return new HttpClient(handler, disposeHandler: true);
                }

                public static Task<HttpResponseMessage> Get(HttpClient client) => client.GetAsync("https://example.com");

                public static Task Open(ClientWebSocket socket) =>
                    socket.ConnectAsync(new System.Uri("wss://example.com"), CancellationToken.None);

                public static HttpClient Wrap(HttpMessageHandler handler) => new HttpClient(handler);
            }
            """,
            severities: AllAmbientRulesOn);

        Assert.Collection(
            diagnostics,
            d => Assert.Equal(("CAP0002", "new SocketsHttpHandler()"), (d.Id, d.Flagged())));
    }

    [Fact]
    public async Task A_call_bound_at_run_time_through_dynamic_is_not_seen()
    {
        // Documented as a blind spot in docs/analyzers.md: the overload is chosen by the
        // runtime binder, so there is no member in the compilation to compare with the lists.
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            Wrap("File.Exists((dynamic)\"x\")"), severities: AllAmbientRulesOn);

        Assert.Empty(diagnostics);
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
    public async Task A_type_ban_covers_the_types_derived_from_it()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System;
            using System.Security.Cryptography;

            #pragma warning disable SYSLIB0023

            public sealed class Dice : Random
            {
                public override int Next() => 4;
            }

            public static class Uses
            {
                public static Dice Make() => new Dice();
                public static int Roll(Dice dice) => dice.Next();
                public static void Fill(RNGCryptoServiceProvider rng, byte[] buffer) => rng.GetBytes(buffer);
            }
            """,
            severities: AllAmbientRulesOn);

        Assert.Collection(
            diagnostics,
            d => Assert.Equal(("CAP0007", "new Dice()"), (d.Id, d.Flagged())),
            d => Assert.Equal(("CAP0007", "dice.Next()"), (d.Id, d.Flagged())),
            d => Assert.Equal(("CAP0007", "rng.GetBytes(buffer)"), (d.Id, d.Flagged())));
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
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Win32.SafeHandles;

        #pragma warning disable SYSLIB0014, SYSLIB0023

        public static class Uses
        {
            public static object Value() => {{expression}};
        }
        """;
}
