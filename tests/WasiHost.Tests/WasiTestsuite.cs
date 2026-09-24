using System.Text.Json;

namespace WasiHost.Tests;

/// <summary>One program from the WebAssembly WASI test suite, and how it is to be run.</summary>
/// <param name="Name">The suite it belongs to and its own name, such as <c>rust/fd_readdir</c>.</param>
/// <param name="Module">The compiled program.</param>
/// <param name="Root">The directory to copy and preopen as <c>/</c>.</param>
/// <param name="Arguments">Its arguments, after the program name.</param>
/// <param name="Environment">Its environment.</param>
/// <param name="ExitCode">The exit code that means it passed.</param>
internal sealed record TestsuiteCase(
    string Name,
    string Module,
    string Root,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    int ExitCode);

/// <summary>
/// The filesystem cases of the WebAssembly WASI test suite's preview 1 programs.
/// </summary>
/// <remarks>
/// <para>
/// The suite is fetched rather than kept in this repository: its compiled programs come to
/// about a hundred megabytes. <c>build/ci/fetch-wasi-testsuite.sh</c> fetches the commit the
/// expectations here were written against and prints the directory, which is handed to the
/// tests in <see cref="LocationVariable"/>. Without it the cases skip, saying so, unless
/// <see cref="ExpectedVariable"/> says the run was meant to have it: then a missing suite fails
/// the run rather than letting it pass having tested nothing.
/// </para>
/// <para>
/// A case is a filesystem case when its configuration gives it a directory to preopen. The
/// others test clocks, randomness, polling and sockets, which are not what this adapter is
/// for.
/// </para>
/// </remarks>
internal static class WasiTestsuite
{
    /// <summary>The variable naming the fetched suite's directory.</summary>
    public const string LocationVariable = "CAPDOTNET_WASI_TESTSUITE";

    /// <summary>Set to 1 where the suite was fetched, so that losing it fails the run.</summary>
    public const string ExpectedVariable = "CAPDOTNET_EXPECT_WASI_TESTSUITE";

    /// <summary>The suites of preview 1 programs, by the language they were written in.</summary>
    private static readonly string[] Suites = ["rust", "c", "assemblyscript"];

    /// <summary>The fetched suite, or null when it was not fetched.</summary>
    public static string? Location =>
        Environment.GetEnvironmentVariable(LocationVariable) is { Length: > 0 } location ? location : null;

    /// <summary>Every filesystem case in the fetched suite; empty when it was not fetched.</summary>
    public static IReadOnlyList<TestsuiteCase> Cases { get; } = Discover();

    public static TestsuiteCase Named(string name) =>
        Cases.FirstOrDefault(entry => entry.Name == name)
            ?? throw new ArgumentOutOfRangeException(nameof(name), name, "No such case in the fetched suite.");

    /// <summary>
    /// Skips the calling test when the suite was not fetched, and fails it when it should
    /// have been.
    /// </summary>
    public static void RequirePresent()
    {
        if (Location is not null)
        {
            return;
        }

        string reason =
            $"The WASI test suite is not fetched. Run build/ci/fetch-wasi-testsuite.sh and set " +
            $"{LocationVariable} to the directory it prints.";
        Assert.False(Environment.GetEnvironmentVariable(ExpectedVariable) == "1", reason);
        Assert.Skip(reason);
    }

    private static List<TestsuiteCase> Discover()
    {
        List<TestsuiteCase> cases = [];
        if (Location is not { } location)
        {
            return cases;
        }

        foreach (string suite in Suites)
        {
            string directory = Path.Join(location, "tests", suite, "testsuite", "wasm32-wasip1");
            foreach (string module in Directory.GetFiles(directory, "*.wasm").Order(StringComparer.Ordinal))
            {
                string configuration = Path.ChangeExtension(module, ".json");
                if (!File.Exists(configuration))
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configuration));
                JsonElement json = document.RootElement;
                if (!json.TryGetProperty("root", out JsonElement root))
                {
                    continue;
                }

                cases.Add(new TestsuiteCase(
                    $"{suite}/{Path.GetFileNameWithoutExtension(module)}",
                    module,
                    Path.Join(directory, root.GetString()),
                    json.TryGetProperty("args", out JsonElement args)
                        ? [.. args.EnumerateArray().Select(arg => arg.GetString()!)]
                        : [],
                    json.TryGetProperty("env", out JsonElement env)
                        ? [.. env.EnumerateObject().Select(pair => KeyValuePair.Create(pair.Name, pair.Value.GetString()!))]
                        : [],
                    json.TryGetProperty("exit_code", out JsonElement exit) ? exit.GetInt32() : 0));
            }
        }

        return cases;
    }
}
