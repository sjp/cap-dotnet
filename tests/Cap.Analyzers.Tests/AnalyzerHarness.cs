using System.Collections.Immutable;
using Cap.Primitives;
using Cap.Std;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cap.Analyzers.Tests;

/// <summary>
/// Compiles a consumer's source against this library and returns what the analyzer reports.
/// </summary>
/// <remarks>
/// Diagnostics come back after the compiler has applied severities and enablement, the same
/// filtering a build applies, so a rule that is off by default is absent here exactly when it
/// would be absent from a build.
/// </remarks>
internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

    private static readonly ImmutableArray<MetadataReference> ReferencesWithFileSystemAbstractions =
        [.. References, .. LoadFileSystemAbstractions()];

    public static ImmutableArray<MetadataReference> FrameworkAndLibrary => References;

    /// <summary>
    /// <see cref="FrameworkAndLibrary"/>, plus System.IO.Abstractions, Testably.Abstractions and
    /// Cap.IO.Abstractions.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithFileSystemAbstractions => ReferencesWithFileSystemAbstractions;

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary,
        IReadOnlyDictionary<string, ReportDiagnostic>? severities = null,
        IReadOnlyDictionary<string, string>? editorConfig = null,
        IReadOnlyList<(string Path, string Text)>? additionalFiles = null,
        ImmutableArray<MetadataReference>? references = null)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: "/project/Source.cs");

        var options = new CSharpCompilationOptions(
            outputKind,
            nullableContextOptions: NullableContextOptions.Enable,
            specificDiagnosticOptions: severities);

        CSharpCompilation compilation = CSharpCompilation.Create("Consumer", [tree], references ?? References, options);

        ImmutableArray<Diagnostic> errors = [.. compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.IsEmpty, "The test source does not compile:\n" + string.Join("\n", errors));

        var analyzerOptions = new AnalyzerOptions(
            [.. (additionalFiles ?? []).Select(f => (AdditionalText)new InMemoryText(f.Path, f.Text))],
            new ConfigOptionsProvider(editorConfig ?? new Dictionary<string, string>()));

        CompilationWithAnalyzers analysis = compilation.WithAnalyzers(
            [new CapabilityAnalyzer()],
            new CompilationWithAnalyzersOptions(
                analyzerOptions,
                onAnalyzerException: null,
                concurrentAnalysis: false,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false));

        ImmutableArray<Diagnostic> diagnostics = await analysis.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. diagnostics.OrderBy(d => d.Location.SourceSpan.Start)];
    }

    /// <summary>
    /// The text a diagnostic points at, so that a test can say which call was reported rather
    /// than which column it started in.
    /// </summary>
    public static string Flagged(this Diagnostic diagnostic) =>
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        string framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        IEnumerable<string> frameworkAssemblies = Directory.EnumerateFiles(framework, "*.dll")
            .Where(IsManagedAssembly);

        return
        [
            .. frameworkAssemblies.Select(p => MetadataReference.CreateFromFile(p)),
            MetadataReference.CreateFromFile(typeof(AmbientAuthority).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dir).Assembly.Location),
        ];
    }

    private static IEnumerable<MetadataReference> LoadFileSystemAbstractions()
    {
        Type[] fromEach =
        [
            typeof(System.IO.Abstractions.FileSystem),
            typeof(System.IO.Abstractions.IFileSystem),
            typeof(Testably.Abstractions.RealFileSystem),
            typeof(Testably.Abstractions.ITimeSystem),
            typeof(Cap.IO.Abstractions.DirFileSystem),
        ];

        // TestableIO.System.IO.Abstractions holds no types of its own to name, only forwards
        // to the assemblies above, so it is found beside them.
        return fromEach
            .Select(type => type.Assembly.Location)
            .Append(Path.Combine(AppContext.BaseDirectory, "TestableIO.System.IO.Abstractions.dll"))
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class ConfigOptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        private readonly Options _options = new(values);

        public override AnalyzerConfigOptions GlobalOptions => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

        private sealed class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
        {
            public static readonly Options Empty = new(new Dictionary<string, string>());

            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
                values.TryGetValue(key, out value);
        }
    }
}
