using System.Globalization;

namespace Cap.Stress.Tests;

/// <summary>
/// How long each race is run for, and where its results are written.
/// </summary>
/// <remarks>
/// <para>
/// A race that is lost once in a million attempts is not found by a hundred, so the count that
/// matters is the one the scheduled run uses, which is far too slow for every change. The same
/// tests therefore run at two sizes: small enough to finish in seconds on every pull request,
/// where they show that the harness still builds its races and still tells the outcomes apart,
/// and large on the nightly run, where the counts are what is being measured.
/// </para>
/// <para>
/// Read from the environment rather than from test parameters, so that the nightly run is the
/// same binary and the same tests as the per-change run, and differs only in being told to go on
/// for longer.
/// </para>
/// </remarks>
internal static class StressSettings
{
    /// <summary>The environment variable giving the number of attempts per race and backend.</summary>
    public const string IterationsVariable = "CAPDOTNET_STRESS_ITERATIONS";

    /// <summary>
    /// The environment variable naming a file to append the results to, as a Markdown table.
    /// </summary>
    /// <remarks>
    /// The nightly run points it at the job summary, so that the counts are read off the run
    /// rather than dug out of the test log.
    /// </remarks>
    public const string ReportVariable = "CAPDOTNET_STRESS_REPORT";

    /// <summary>The number of attempts when nothing says otherwise.</summary>
    public const int DefaultIterations = 10_000;

    /// <summary>
    /// The fewest rounds any race is run for, however small the configured count.
    /// </summary>
    /// <remarks>
    /// Some races are run as whole rounds that each cost many operations — building a tree and
    /// removing it under attack, say — and are scaled down from the iteration count. Scaled far
    /// enough they would round to nothing, and a race run zero times passes without having been
    /// fought.
    /// </remarks>
    public const int MinimumRounds = 10;

    /// <summary>The number of attempts per race and backend.</summary>
    public static int Iterations { get; } = ReadIterations();

    /// <summary>The file results are appended to, if one was named.</summary>
    public static string? ReportPath =>
        Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } path ? path : null;

    /// <summary>
    /// The number of rounds for a race whose every round is worth <paramref name="attemptsPerRound"/>
    /// single attempts.
    /// </summary>
    public static int Rounds(int attemptsPerRound) => Math.Max(MinimumRounds, Iterations / attemptsPerRound);

    private static int ReadIterations()
    {
        string? configured = Environment.GetEnvironmentVariable(IterationsVariable);
        if (string.IsNullOrEmpty(configured))
        {
            return DefaultIterations;
        }

        // A value that does not parse is a mistake in whoever set it, and running the default
        // instead would let a nightly job that meant to run a million attempts quietly run ten
        // thousand and report them as though they were the million.
        if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) || iterations < 1)
        {
            throw new InvalidOperationException(
                $"{IterationsVariable} is '{configured}', which is not a positive whole number of attempts.");
        }

        return iterations;
    }
}
