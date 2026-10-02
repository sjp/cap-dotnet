using Microsoft.CodeAnalysis;

namespace Cap.Analyzers.Tests;

public sealed class ConcatenatedPathTests
{
    [Theory]
    [InlineData("dir.ReadAllText(\"users/\" + name)")]
    [InlineData("dir.ReadAllText(prefix + \"/\" + name)")]
    [InlineData("dir.ReadAllText(prefix + '/' + name)")]
    [InlineData("dir.ReadAllText(prefix + \"\\\\\" + name)")]
    [InlineData("dir.ReadAllText(prefix + Path.DirectorySeparatorChar + name)")]
    [InlineData("dir.ReadAllText(Path.Combine(prefix, name))")]
    [InlineData("dir.ReadAllText(Path.Join(prefix, name))")]
    [InlineData("dir.ReadAllText($\"users/{name}\")")]
    [InlineData("dir.ReadAllText($\"{prefix}{Path.DirectorySeparatorChar}{name}\")")]
    [InlineData("dir.ReadAllText(string.Concat(prefix, \"/\", name))")]
    [InlineData("dir.ReadAllText(string.Join(\"/\", prefix, name))")]
    [InlineData("dir.OpenDir(\"users/\" + name)")]
    [InlineData("dir.CreateSymlink(\"links/\" + name, \"target\")")]
    [InlineData("dir.Rename(\"a/\" + name, dir, \"b\")")]
    [InlineData("dir.Rename(\"a\", dir, \"b/\" + name)")]
    [InlineData("dir.ReadAllText(Path.Combine(\"users\", name))")]
    [InlineData("dir.ReadAllText(Path.Combine(\"users\", Path.Combine(\"alice\", name)))")]
    [InlineData("dir.ReadAllText(Path.Combine(\"a\", \"b\", \"c\", \"d\", name))")]
    [InlineData("dir.ReadAllText(\"users\" + Path.DirectorySeparatorChar + name)")]
    [InlineData("dir.ReadAllText(string.Concat(\"users\", Path.DirectorySeparatorChar.ToString(), name))")]
    public async Task A_path_joined_at_the_call_is_reported(string call)
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(Wrap(call));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0005", DiagnosticSeverity.Warning), (diagnostic.Id, diagnostic.Severity));
    }

    /// <summary>
    /// The interface a component takes in place of the handle is held to the same rule, since
    /// a path joined for it is joined for whatever handle is behind it.
    /// </summary>
    [Theory]
    [InlineData("dir.ReadAllText(\"users/\" + name)")]
    [InlineData("dir.OpenFile(Path.Combine(prefix, name))")]
    [InlineData("dir.Rename(\"a\", dir, $\"b/{name}\")")]
    [InlineData("entry.OpenDir().DeleteFile(prefix + \"/\" + name)")]
    public async Task A_path_joined_for_the_interface_is_reported(string call)
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(Wrap(call, "IDir dir, IDirEntry entry"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0005", DiagnosticSeverity.Warning), (diagnostic.Id, diagnostic.Severity));
    }

    [Theory]
    [InlineData("dir.ReadAllText(name)")]
    [InlineData("dir.ReadAllText(name + \".txt\")")]
    [InlineData("dir.ReadAllText(\"users/alice.txt\")")]
    [InlineData("dir.ReadAllText(Fixed + \"/\" + \"alice.txt\")")]
    [InlineData("dir.ReadAllText(Path.Combine(\"users\", \"alice.txt\"))")]
    [InlineData("dir.ReadAllText(Path.Join(\"users\", \"alice.txt\"))")]
    [InlineData("dir.ReadAllText(Path.Combine(Fixed, \"alice.txt\"))")]
    [InlineData("dir.ReadAllText(Path.Combine(\"a\", \"b\", \"c\", \"d\", \"e\"))")]
    [InlineData("dir.ReadAllText(Path.Combine(\"users\", Path.Combine(\"alice\", \"notes.txt\")))")]
    [InlineData("dir.ReadAllText(string.Concat(\"users\", \"/\", \"alice.txt\"))")]
    [InlineData("dir.ReadAllText(string.Join(\"/\", \"users\", \"alice.txt\"))")]
    [InlineData("dir.ReadAllText(\"users\" + Path.DirectorySeparatorChar + \"alice.txt\")")]
    [InlineData("dir.ReadAllText($\"users{Path.AltDirectorySeparatorChar}alice.txt\")")]
    [InlineData("dir.ReadAllText(string.Concat(\"users\", Path.DirectorySeparatorChar.ToString(), \"alice.txt\"))")]
    [InlineData("dir.ReadAllText($\"{Path.Combine(\"users\", \"alice\")}/notes.txt\")")]
    [InlineData("dir.ReadAllText($\"{name}.txt\")")]
    [InlineData("dir.OpenDir(\"users\").ReadAllText(name)")]
    [InlineData("dir.CreateSymlink(\"link\", \"../\" + name)")]
    [InlineData("Dir.Open(Path.Combine(prefix, name), AmbientAuthority.Acquire())")]
    [InlineData("Ignore(Path.Combine(prefix, name))")]
    public async Task Anything_else_is_not_reported(string call)
    {
        // Acquire is taken outside a composition root in one case here; that is a different
        // rule, and only this one is under test.
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(Wrap(call));

        Assert.DoesNotContain(diagnostics, d => d.Id == "CAP0005");
    }

    [Fact]
    public async Task A_consumer_method_with_a_path_parameter_is_not_reported()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            public static class Mine
            {
                public static void Take(string path) { }

                public static void Use(string prefix, string name) => Take(prefix + "/" + name);
            }
            """,
            assemblyName: "Cap.Consumer");

        Assert.DoesNotContain(diagnostics, d => d.Id == "CAP0005");
    }

    private static string Wrap(string call, string parameters = "Dir dir") => $$"""
        using System.IO;
        using Cap.Primitives;
        using Cap.Std;

        public static class Uses
        {
            private const string Fixed = "users";

            public static void Call({{parameters}}, string prefix, string name)
            {
                {{call}};
            }

            private static object Ignore(string value) => value;
        }
        """;
}
