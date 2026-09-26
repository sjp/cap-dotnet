namespace Cap.Std.Tests;

/// <summary>
/// That every C# block in the testing guide and the <c>IFileSystem</c> guide is code the
/// testable-component sample compiles and runs.
/// </summary>
/// <remarks>
/// A snippet on those pages that nobody compiles drifts: a member is renamed, an assertion
/// stops holding, and the page goes on showing it. So each block must appear verbatim as a
/// marked region of the sample or its tests, which the build compiles and CI runs on every
/// platform. A block with no region to match fails here, which is what keeps an unchecked
/// snippet from being added.
/// </remarks>
public sealed class DocsExampleTests
{
    [Theory]
    [InlineData("testing.md")]
    [InlineData("io-abstractions.md")]
    public void Every_csharp_block_is_a_region_of_the_sample(string page)
    {
        string repository = FindRepositoryRoot();
        string markdown = File.ReadAllText(Path.Combine(repository, "docs", page)).ReplaceLineEndings("\n");
        HashSet<string> regions = SampleRegions(Path.Combine(repository, "samples", "TestableComponent"));

        List<string> blocks = CSharpBlocks(markdown);
        Assert.NotEmpty(blocks);

        string[] unmatched = [.. blocks.Where(block => !regions.Contains(block))];
        Assert.True(
            unmatched.Length == 0,
            $"docs/{page} has C# blocks that are not a region of samples/TestableComponent:\n\n"
            + string.Join("\n---\n", unmatched));
    }

    private static List<string> CSharpBlocks(string markdown)
    {
        List<string> blocks = [];
        const string open = "```csharp\n";
        for (int start = markdown.IndexOf(open, StringComparison.Ordinal); start >= 0;
             start = markdown.IndexOf(open, start, StringComparison.Ordinal))
        {
            start += open.Length;
            int end = markdown.IndexOf("```\n", start, StringComparison.Ordinal);
            Assert.True(end >= 0, "A C# block is not closed.");
            blocks.Add(markdown[start..end]);
            start = end;
        }

        return blocks;
    }

    private static HashSet<string> SampleRegions(string sample)
    {
        HashSet<string> regions = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(sample, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sample, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            string[] lines = File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string marker = lines[i].Trim();
                if (!marker.StartsWith("// <", StringComparison.Ordinal) || marker.StartsWith("// </", StringComparison.Ordinal))
                {
                    continue;
                }

                string close = "// </" + marker["// <".Length..];
                int end = Array.FindIndex(lines, i + 1, line => line.Trim() == close);
                Assert.True(end >= 0, $"{relative}: the region {marker} is not closed.");
                regions.Add(Dedent(lines[(i + 1)..end]));
            }
        }

        return regions;
    }

    private static string Dedent(string[] lines)
    {
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
