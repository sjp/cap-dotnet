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
    public async Task A_path_joined_at_the_call_is_reported(string call)
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(Wrap(call));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("CAP0005", DiagnosticSeverity.Warning), (diagnostic.Id, diagnostic.Severity));
    }

    [Theory]
    [InlineData("dir.ReadAllText(name)")]
    [InlineData("dir.ReadAllText(name + \".txt\")")]
    [InlineData("dir.ReadAllText(\"users/alice.txt\")")]
    [InlineData("dir.ReadAllText(Fixed + \"/\" + \"alice.txt\")")]
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

    private static string Wrap(string call) => $$"""
        using System.IO;
        using Cap.Primitives;
        using Cap.Std;

        public static class Uses
        {
            private const string Fixed = "users";

            public static void Call(Dir dir, string prefix, string name)
            {
                {{call}};
            }

            private static object Ignore(string value) => value;
        }
        """;
}
