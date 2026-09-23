using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Cap.Benchmarks;

/// <summary>
/// Fails a run whose hot-path benchmarks have grown more than ten percent slower or heavier
/// than the committed baseline for this platform.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Time is gated as a ratio, not as a duration.</strong> A hosted CI runner's speed
/// wanders from one run to the next by more than the ten percent being guarded, so a gate on
/// absolute time would fail at random. Every hot-path operation is measured in the same run,
/// on the same machine, as its <c>System.IO</c> baseline, and what is compared is the ratio
/// between the two: that moves when this library gets slower, and mostly does not when the
/// machine does. Where a class's baseline is itself a cap-dotnet method, the ratio is to that.
/// </para>
/// <para>
/// <strong>Allocation is gated as bytes per operation.</strong> It does not depend on the
/// machine, so it is compared directly, and an operation whose baseline allocates nothing fails
/// on its first byte: those are the rows whose whole point is that they allocate nothing.
/// </para>
/// <para>
/// Medians rather than means, so that one iteration interrupted by the runner doing something
/// else does not decide the verdict. Baselines are kept per operating system rather than per
/// architecture: the ratio between two ways of making the same syscalls moves with the kernel
/// and the filesystem far more than with the instruction set.
/// </para>
/// </remarks>
internal static class Gate
{
    /// <summary>How much worse than its baseline a row may get before the gate fails.</summary>
    public const double Tolerance = 0.10;

    /// <summary>The name of this platform's baseline file.</summary>
    public static string Platform =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : "other";

    /// <summary>
    /// Compares a finished run against the committed baseline, writes the verdict, and returns
    /// the process exit code.
    /// </summary>
    /// <param name="summaries">The hot-path run.</param>
    /// <param name="baselineDirectory">Where the committed per-platform baselines live.</param>
    /// <param name="measuredPath">Where to write this run's figures, in the baseline format.</param>
    /// <param name="update">Rewrite the committed baseline with this run's figures instead of gating.</param>
    public static int Evaluate(IEnumerable<Summary> summaries, string baselineDirectory, string measuredPath, bool update)
    {
        List<Row> rows = [];
        List<string> failures = [];
        foreach (Summary summary in summaries)
        {
            Collect(summary, rows, failures);
        }

        string baselinePath = Path.Join(baselineDirectory, Platform + ".json");
        BaselineFile committed = Load(baselinePath);
        BaselineFile measured = new()
        {
            MeasuredOn = Describe(),
            Benchmarks = rows.ToDictionary(r => r.Key, r => r.Figures, StringComparer.Ordinal),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(measuredPath))!);
        File.WriteAllText(measuredPath, JsonSerializer.Serialize(measured, GateJson.Default.BaselineFile) + "\n");

        if (update)
        {
            // Keep rows this host could not measure -- the confined-open job on a kernel without
            // it -- rather than deleting another machine's figures.
            foreach ((string key, Figures figures) in committed.Benchmarks)
            {
                measured.Benchmarks.TryAdd(key, figures);
            }

            Directory.CreateDirectory(baselineDirectory);
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(measured with
            {
                Benchmarks = new SortedDictionary<string, Figures>(measured.Benchmarks, StringComparer.Ordinal)
                    .ToDictionary(StringComparer.Ordinal),
            }, GateJson.Default.BaselineFile) + "\n");
            Console.WriteLine($"Wrote {rows.Count} rows to {baselinePath}.");
            return failures.Count == 0 ? 0 : 1;
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"### Benchmark gate ({Platform})");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Baseline: `{Path.GetFileName(baselinePath)}`, measured on {committed.MeasuredOn ?? "(none committed)"}. " +
            $"A row fails when its time ratio or its allocation grows more than {Tolerance:P0}.");
        report.AppendLine();
        report.AppendLine("| Benchmark | Ratio | Baseline ratio | Allocated | Baseline allocated | Verdict |");
        report.AppendLine("|---|---:|---:|---:|---:|---|");

        int ungated = 0;
        foreach (Row row in rows)
        {
            if (!committed.Benchmarks.TryGetValue(row.Key, out Figures? expected))
            {
                ungated++;
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {row.Key} | {Format(row.Figures.Ratio)} | – | {row.Figures.AllocatedBytes} B | – | not gated: no baseline |");
                continue;
            }

            List<string> problems = [];
            if (row.Figures.Ratio is double ratio && expected.Ratio is double expectedRatio
                && ratio > expectedRatio * (1 + Tolerance))
            {
                problems.Add($"time ratio {ratio:F3} against {expectedRatio:F3}");
            }

            long allowed = (long)Math.Floor(expected.AllocatedBytes * (1 + Tolerance));
            if (row.Figures.AllocatedBytes > allowed)
            {
                problems.Add($"{row.Figures.AllocatedBytes} B allocated against {expected.AllocatedBytes} B");
            }

            if (problems.Count > 0)
            {
                failures.Add($"{row.Key}: {string.Join("; ", problems)}");
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Key} | {Format(row.Figures.Ratio)} | {Format(expected.Ratio)} | " +
                $"{row.Figures.AllocatedBytes} B | {expected.AllocatedBytes} B | " +
                $"{(problems.Count == 0 ? "ok" : "**regressed**")} |");
        }

        report.AppendLine();
        if (ungated > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{ungated} row(s) have no committed baseline and were not gated. To start gating them, " +
                $"commit this run's figures (`{Path.GetFileName(measuredPath)}`) as `bench/baselines/{Platform}.json`.");
            report.AppendLine();
        }

        foreach (string failure in failures)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"- {failure}");
        }

        string text = report.ToString();
        Console.WriteLine(text);
        string? stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(stepSummary))
        {
            File.AppendAllText(stepSummary, text);
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static void Collect(Summary summary, List<Row> rows, List<string> failures)
    {
        foreach (BenchmarkReport report in summary.Reports)
        {
            BenchmarkCase benchmark = report.BenchmarkCase;
            string key = KeyOf(benchmark);

            if (!report.Success || report.ResultStatistics is null)
            {
                failures.Add($"{key}: did not produce a result");
                continue;
            }

            if (benchmark.Descriptor.WorkloadMethod.Name == Categories.SystemIOMethod)
            {
                continue;
            }

            double? ratio = null;
            if (!benchmark.Descriptor.Baseline)
            {
                BenchmarkReport? yardstick = summary.Reports.FirstOrDefault(other =>
                    other.BenchmarkCase.Descriptor.Baseline
                    && other.BenchmarkCase.Descriptor.Type == benchmark.Descriptor.Type
                    && other.BenchmarkCase.Job.Id == benchmark.Job.Id
                    && other.BenchmarkCase.Parameters.DisplayInfo == benchmark.Parameters.DisplayInfo);

                if (yardstick?.ResultStatistics is { } baseline)
                {
                    ratio = Math.Round(report.ResultStatistics.Median / baseline.Median, 4);
                }
            }

            long allocated = report.GcStats.GetBytesAllocatedPerOperation(benchmark) ?? 0;
            rows.Add(new Row(key, new Figures(ratio, allocated)));
        }
    }

    private static string KeyOf(BenchmarkCase benchmark)
    {
        string parameters = benchmark.HasParameters ? benchmark.Parameters.DisplayInfo : string.Empty;
        return $"{benchmark.Descriptor.Type.Name}.{benchmark.Descriptor.WorkloadMethod.Name}{parameters}/{benchmark.Job.Id}";
    }

    private static BaselineFile Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), GateJson.Default.BaselineFile) ?? new BaselineFile()
            : new BaselineFile();

    private static string Describe() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()} " +
        $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, " +
        $"{Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}";

    private static string Format(double? ratio) =>
        ratio is double value ? value.ToString("F3", CultureInfo.InvariantCulture) : "–";

    private sealed record Row(string Key, Figures Figures);

    /// <summary>One row of a baseline file.</summary>
    /// <param name="Ratio">Median time over the class's baseline's median time, or none for a baseline row.</param>
    /// <param name="AllocatedBytes">Bytes allocated per operation.</param>
    internal sealed record Figures(double? Ratio, long AllocatedBytes);

    /// <summary>The committed figures for one platform.</summary>
    internal sealed record BaselineFile
    {
        /// <summary>The machine the figures came from, for whoever wonders why they are what they are.</summary>
        public string? MeasuredOn { get; init; }

        public Dictionary<string, Figures> Benchmarks { get; init; } = new(StringComparer.Ordinal);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Gate.BaselineFile))]
internal sealed partial class GateJson : JsonSerializerContext;
