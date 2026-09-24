using System.Text;
using Cap.Primitives;
using Cap.Rand;
using Cap.Std;
using Microsoft.Extensions.Time.Testing;
using Wasmtime;
using WasiHost.Preview1;

namespace WasiHost.Tests;

/// <summary>
/// The WebAssembly WASI test suite's filesystem programs, run against the adapter.
/// </summary>
/// <remarks>
/// <para>
/// Each program gets a fresh copy of the directory its configuration names, preopened as
/// <c>/</c>, and passes when it exits with the code its configuration expects. A program that
/// fails an assertion panics, which ends in a trap, and the test reports what it printed.
/// </para>
/// <para>
/// Some programs fail because the library cannot yet express what WASI asks for, and the
/// adapter does not work around a missing capability; <see cref="KnownGaps"/> lists them with
/// the reason. Each of those is asserted to fail, so that closing a gap turns its entry red
/// until the entry is removed, and the list cannot outlive what it describes.
/// </para>
/// </remarks>
public sealed class WasiTestsuiteTests
{
    private const string Timestamps =
        "Neither Dir nor CapFile can set a file's times, so the adapter answers ENOTSUP.";

    private const string Appending =
        "Appending is a FileMode in .NET, which creates, allows only writing and cannot truncate, " +
        "so an open that appends and also reads or truncates cannot be expressed.";

    /// <summary>
    /// The programs that fail because of something the library does not offer, and what that
    /// is. A program listed here must fail; see the remarks on the class.
    /// </summary>
    internal static readonly Dictionary<string, string> KnownGaps = new()
    {
        ["rust/interesting_paths"] =
            "Dir refuses every path containing '..', even one whose resolution stays inside the " +
            "directory, and WASI resolves those.",
        ["rust/symlink_create"] =
            "Dir.CreateSymlink stores an absolute target, which no resolution beneath a handle " +
            "will ever follow; WASI refuses to create such a link.",
        ["rust/fd_filestat_set"] = Timestamps,
        ["rust/symlink_filestat"] = Timestamps,
        ["rust/fd_flags_set"] = Appending,
        ["rust/path_filestat"] = Appending,
        ["c/pwrite-with-append"] = Appending,
    };

    public static TheoryData<string> Cases
    {
        get
        {
            TheoryData<string> rows = [];
            foreach (TestsuiteCase entry in WasiTestsuite.Cases)
            {
                rows.Add(entry.Name);
            }

            // A row with no case behind it, so that a run without the suite reports a skip
            // saying why rather than a theory with no data.
            if (rows.Count == 0)
            {
                rows.Add(string.Empty);
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_filesystem_program_passes_unless_a_known_gap_stops_it(string name)
    {
        WasiTestsuite.RequirePresent();
        TestsuiteCase entry = WasiTestsuite.Named(name);

        (bool passed, string report) = Run(entry);

        if (KnownGaps.TryGetValue(name, out string? gap))
        {
            Assert.False(
                passed,
                $"{name} now passes, so what stopped it is no longer missing: '{gap}'. Remove its " +
                "entry from the known gaps.");
            return;
        }

        Assert.True(passed, $"{name} failed.\n{report}");
    }

    [Fact]
    public void Every_known_gap_names_a_program_in_the_suite()
    {
        WasiTestsuite.RequirePresent();

        foreach (string name in KnownGaps.Keys)
        {
            Assert.Contains(WasiTestsuite.Cases, entry => entry.Name == name);
        }
    }

    [Fact]
    public void The_suite_has_filesystem_programs_to_run()
    {
        WasiTestsuite.RequirePresent();

        // A layout change in the suite that hid every program would otherwise pass vacuously.
        Assert.True(WasiTestsuite.Cases.Count >= 40, $"Only {WasiTestsuite.Cases.Count} filesystem programs found.");
    }

    private static readonly Lazy<Engine> SharedEngine = new(() => new Engine());

    private static (bool Passed, string Report) Run(TestsuiteCase entry)
    {
        using ScratchTree scratch = new();
        Copy(entry.Root, scratch.HostPath);

        MemoryStream stdout = new();
        MemoryStream stderr = new();
        WasiOptions options = new()
        {
            Arguments = [Path.GetFileNameWithoutExtension(entry.Module), .. entry.Arguments],
            Environment = entry.Environment,
            Clock = new FakeTimeProvider(DateTimeOffset.UtcNow),
            Random = new InsecureDeterministicRandom(0),
            Preopens = [("/", Dir.Open(scratch.HostPath, AmbientAuthority.Acquire()))],
            StandardOutput = stdout,
            StandardError = stderr,
        };

        string outcome;
        bool passed;
        try
        {
            using Module module = Module.FromFile(SharedEngine.Value, entry.Module);
            int code = WasiProgram.Run(SharedEngine.Value, module, options);
            passed = code == entry.ExitCode;
            outcome = $"exited with {code}, expecting {entry.ExitCode}";
        }
        catch (WasmtimeException e)
        {
            passed = false;
            outcome = $"trapped: {e.Message}";
        }

        string report =
            $"{outcome}\n--- stdout ---\n{Encoding.UTF8.GetString(stdout.ToArray())}" +
            $"\n--- stderr ---\n{Encoding.UTF8.GetString(stderr.ToArray())}";
        return (passed, report);
    }

    /// <summary>Copies the case's starting tree, which the program may change, into the scratch directory.</summary>
    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Join(to, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.GetDirectories(from))
        {
            Copy(directory, Path.Join(to, Path.GetFileName(directory)));
        }
    }
}
