using System.Globalization;

namespace Cap.Fuzz.Tests;

/// <summary>
/// How many cases each property is tried on, and where the fuzzer's starting inputs are written.
/// </summary>
/// <remarks>
/// Read from the environment, so that the nightly run is the same binary and the same tests as
/// the run on every change and differs only in being told to try more cases. A property that
/// holds for a few thousand random inputs is worth knowing on every pull request; one that
/// fails once in a million is only found overnight.
/// </remarks>
internal static class PropertySettings
{
    /// <summary>The environment variable giving the number of cases per property.</summary>
    public const string IterationsVariable = "CAPDOTNET_PROPERTY_ITERATIONS";

    /// <summary>
    /// The environment variable naming a directory to write the fuzzer's starting inputs to.
    /// </summary>
    public const string SeedDirectoryVariable = "CAPDOTNET_FUZZ_SEEDS";

    /// <summary>The number of cases when nothing says otherwise.</summary>
    public const long DefaultIterations = 2_000;

    /// <summary>The number of cases per property.</summary>
    public static long Iterations { get; } = ReadIterations();

    /// <summary>Where to write the starting inputs, if anywhere.</summary>
    public static string? SeedDirectory =>
        Environment.GetEnvironmentVariable(SeedDirectoryVariable) is { Length: > 0 } path ? path : null;

    private static long ReadIterations()
    {
        string? configured = Environment.GetEnvironmentVariable(IterationsVariable);
        if (string.IsNullOrEmpty(configured))
        {
            return DefaultIterations;
        }

        // A value that does not parse is a mistake in whoever set it. Running the default
        // instead would let a nightly job that meant to try a million cases quietly try two
        // thousand and pass as though it had tried the million.
        if (!long.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out long iterations) || iterations < 1)
        {
            throw new InvalidOperationException(
                $"{IterationsVariable} is '{configured}', which is not a positive whole number of cases.");
        }

        return iterations;
    }
}
