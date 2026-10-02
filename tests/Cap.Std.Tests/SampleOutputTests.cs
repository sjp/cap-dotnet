using System.Diagnostics;
using Cap.Tests;

namespace Cap.Std.Tests;

/// <summary>
/// That the output samples/README.md quotes for a sample is what the sample prints.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReadmeExampleTests"/> and <see cref="DocsExampleTests"/> hold the pages' code to
/// the samples, but the output a page shows beneath it is copied by hand, and drifts as soon as
/// a message the sample prints changes. So each sample is run here, as CI runs it, and every
/// line of the output its section quotes must be a line it printed.
/// </para>
/// <para>
/// A quoted block may leave lines out, such as the sandbox's path, which differs on every run,
/// so the check is that each quoted line was printed, not that the two are the same.
/// </para>
/// </remarks>
public sealed class SampleOutputTests
{
    /// <summary>What a sample exits with when this account cannot create symbolic links.</summary>
    private const int CannotLink = 3;

    [Theory]
    [InlineData("ContainmentCheck")]
    [InlineData("TestableComponent")]
    public void Every_quoted_output_line_is_printed_by_the_sample(string sample)
    {
        string readme = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "samples", "README.md")).ReplaceLineEndings("\n");
        List<string> quoted = QuotedOutput(Section(readme, sample));
        Assert.NotEmpty(quoted);

        (int exitCode, ChildOutput output) = Run(sample);
        if (exitCode == CannotLink)
        {
            Assert.Skip($"This account cannot create symbolic links, so {sample} has no scene to show.");
        }

        Assert.True(exitCode == 0, $"{sample} exited with {exitCode}.\n{output.Output}\n{output.Errors}");

        HashSet<string> printed = [.. output.Output.ReplaceLineEndings("\n").Split('\n')];
        string[] missing = [.. quoted.Where(line => !printed.Contains(line))];
        Assert.True(
            missing.Length == 0,
            $"samples/README.md quotes lines {sample} did not print:\n{string.Join('\n', missing)}\n\n" +
            $"It printed:\n{output.Output}");
    }

    /// <summary>The part of the page under the sample's own heading.</summary>
    private static string Section(string readme, string sample)
    {
        string heading = $"## `{sample}`\n";
        int start = readme.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"samples/README.md has no section for {sample}.");
        int end = readme.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    /// <summary>
    /// The lines of every unlabelled block in a section, which is how the page shows output;
    /// commands are in blocks labelled with their shell. Blank lines are left out.
    /// </summary>
    private static List<string> QuotedOutput(string section)
    {
        List<string> lines = [];
        string? fence = null;
        foreach (string line in section.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fence = fence is null ? line : null;
            }
            else if (fence == "```" && line.Length > 0)
            {
                lines.Add(line);
            }
        }

        Assert.True(fence is null, "A block is not closed.");
        return lines;
    }

    /// <summary>Runs the copy of the sample the build laid out beside this assembly.</summary>
    private static (int ExitCode, ChildOutput Output) Run(string sample)
    {
        string assembly = Path.Combine(AppContext.BaseDirectory, "samples", sample, sample + ".dll");
        Assert.True(File.Exists(assembly), $"{assembly} was not built beside the tests.");

        ProcessStartInfo start = new()
        {
            FileName = DotnetHost(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);

        using Process child = Process.Start(start) ??
            throw new InvalidOperationException($"{sample} did not start.");
        ChildOutput output = ChildProcessWait.Finish(child, sample);
        return (child.ExitCode, output);
    }

    /// <summary>
    /// The <c>dotnet</c> host: the one the CLI that started this run names, else this process
    /// if it is the host itself, else whichever is on the path.
    /// </summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } named)
        {
            return named;
        }

        return Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self) is "dotnet"
            ? self
            : "dotnet";
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
