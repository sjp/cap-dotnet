using System.Runtime.InteropServices;

namespace Cap.Tests;

/// <summary>
/// The architecture a run was set up to execute as, named in
/// <c>CAPDOTNET_EXPECT_PROCESS_ARCHITECTURE</c>.
/// </summary>
/// <remarks>
/// The macOS backend chooses its C library entry points by the architecture of the process,
/// and CI covers the Intel ones by running the suite as an Intel process under Rosetta on an
/// Apple silicon agent. Nothing about a passing run says which architecture it had: a host
/// that quietly started the native process instead would pass the same tests through the
/// other set of imports, and the leg would go on reporting coverage it no longer had. Where
/// the variable names an architecture, running as any other is a failure.
/// </remarks>
internal static class ExpectedProcessArchitecture
{
    public const string Variable = "CAPDOTNET_EXPECT_PROCESS_ARCHITECTURE";

    /// <summary>
    /// The architecture this run was set up to have; null when the variable is unset. A value
    /// that names no architecture is a mistake in whoever set it, and is refused rather than
    /// read as no expectation.
    /// </summary>
    public static Architecture? Value
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            if (!Enum.TryParse(configured.Trim(), ignoreCase: false, out Architecture architecture) ||
                !Enum.IsDefined(architecture))
            {
                throw new InvalidOperationException(
                    $"{Variable} is '{configured}', which is not an architecture. Name any of " +
                    $"{string.Join(", ", Enum.GetNames<Architecture>())}.");
            }

            return architecture;
        }
    }
}

public sealed class ProcessArchitectureGuard
{
    [Fact]
    public void Test_process_runs_as_the_expected_architecture()
    {
        Architecture? expected = ExpectedProcessArchitecture.Value;
        if (expected is null)
        {
            return;
        }

        Assert.True(
            RuntimeInformation.ProcessArchitecture == expected,
            $"{ExpectedProcessArchitecture.Variable} says this run was set up as {expected}, but the " +
            $"test process is {RuntimeInformation.ProcessArchitecture}. Whatever launched it did not " +
            "start the process under the architecture intended, so the tests would pass through the " +
            "other architecture's code paths.");
    }
}
