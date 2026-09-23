using Cap.Fuzz.Targets;

namespace Cap.Fuzz.Tests;

/// <summary>
/// Every input the fuzzer has ever saved, run through its target again.
/// </summary>
/// <remarks>
/// <para>
/// When the scheduled run finds a fault it keeps the input that caused it, laid out as
/// <c>fuzz/regressions/&lt;target&gt;/&lt;file&gt;</c>. Committing that file is all it takes
/// to make it a test: this runs every file there on every change, so a fault once fixed cannot
/// come back without failing a pull request.
/// </para>
/// <para>
/// Each input also has to finish in good time. The fuzzer saves inputs that hung as well as
/// ones that crashed, and a hang that came back would otherwise show up only as a test run
/// that never ends.
/// </para>
/// </remarks>
public sealed class SavedInputTests
{
    /// <summary>
    /// How long one input may take. Far longer than any should need, since the point is to
    /// catch one that never finishes, not one that is slow on a busy build agent.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static string RegressionRoot => Path.Join(AppContext.BaseDirectory, "regressions");

    /// <summary>Every target has somewhere for its saved inputs to go.</summary>
    /// <remarks>
    /// A target without the directory would have its saved inputs copied nowhere, and the test
    /// below would pass over an empty list.
    /// </remarks>
    [Fact]
    public void Every_target_has_a_regression_directory()
    {
        foreach (string target in FuzzTargets.All.Keys)
        {
            Assert.True(Directory.Exists(Path.Join(RegressionRoot, target)), $"No saved-input directory for '{target}'.");
        }

        foreach (string directory in Directory.EnumerateDirectories(RegressionRoot))
        {
            Assert.True(
                FuzzTargets.All.ContainsKey(Path.GetFileName(directory)),
                $"'{Path.GetFileName(directory)}' holds saved inputs for a target that does not exist.");
        }
    }

    /// <summary>No saved input faults or hangs.</summary>
    [Fact]
    public async Task Every_saved_input_passes_its_target()
    {
        List<string> failures = [];
        foreach ((string target, FuzzTargets.Target run) in FuzzTargets.All)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Join(RegressionRoot, target)).Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(file).StartsWith('.'))
                {
                    continue;
                }

                byte[] input = File.ReadAllBytes(file);
                try
                {
                    await Task.Run(() => run(input), TestContext.Current.CancellationToken)
                        .WaitAsync(Deadline, TestContext.Current.CancellationToken);
                }
                catch (TimeoutException)
                {
                    failures.Add($"{target}/{Path.GetFileName(file)}: did not finish within {Deadline.TotalSeconds} seconds.");
                }
                catch (Exception fault) when (fault is not OperationCanceledException)
                {
                    failures.Add($"{target}/{Path.GetFileName(file)}: {fault}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, failures));
    }
}
