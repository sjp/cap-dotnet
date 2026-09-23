namespace Cap.Std.Tests;

/// <summary>
/// That the code the README opens with is the code the containment-check sample runs.
/// </summary>
/// <remarks>
/// The README's first claim is that a particular string check can be walked past and that the
/// same read through a handle cannot. The sample is what proves it, in CI, on every platform;
/// but it proves it only about its own code. If the two drifted apart the README would be
/// showing something nobody had run, so each marked block of the sample must appear in the
/// README exactly as written there.
/// </remarks>
public sealed class ReadmeExampleTests
{
    [Theory]
    [InlineData("string-check")]
    [InlineData("with-dir")]
    public void The_readme_shows_the_sample_block_verbatim(string region)
    {
        string repository = FindRepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(repository, "README.md")).ReplaceLineEndings("\n");
        string sample = File.ReadAllText(
            Path.Combine(repository, "samples", "ContainmentCheck", "Program.cs")).ReplaceLineEndings("\n");

        string block = Dedent(Region(sample, region));

        Assert.Contains("```csharp\n" + block + "```\n", readme, StringComparison.Ordinal);
    }

    private static string Region(string source, string name)
    {
        string open = $"// <{name}>\n";
        string close = $"// </{name}>";
        int start = source.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The sample has no region '{name}'.");
        start += open.Length;
        int end = source.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"The sample's region '{name}' is not closed.");
        return source[start..source.LastIndexOf('\n', end)] + "\n";
    }

    private static string Dedent(string block)
    {
        string[] lines = block.TrimEnd('\n').Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart(' ').Length);
        return string.Concat(lines.Select(l => (l.Length >= indent ? l[indent..] : l.TrimStart(' ')) + "\n"));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CapDotnet.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test assembly.");
    }
}
