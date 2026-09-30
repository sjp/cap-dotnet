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
/// machine does. Only a ratio to a <c>System.IO</c> baseline is gated. Where a class's baseline
/// is itself a cap-dotnet method, as for path parsing, the ratio is recorded and reported but
/// not gated: those are operations of tens of nanoseconds divided by one of about ten, and
/// the quotient moves by more than the tolerance between two runs of unchanged code, and moves
/// again with the processor, so gating it would fail at random. A class marked
/// <see cref="Categories.AllocationOnly"/> is reported the same way, for the same reason.
/// </para>
/// <para>
/// <strong>Time is gated on Linux alone.</strong> On the hosted Windows and macOS runners the
/// ratio to <c>System.IO</c> does not hold still either. A Windows runner agrees with itself
/// to under 1% within a run, but runners differ from one another by more than the tolerance.
/// A macOS runner scatters by a fifth within a single run. Both failed the gate on code they
/// had passed, so on those platforms the ratios are reported and every row is held to its
/// allocation alone.
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

    /// <summary>Whether this platform's runners hold a time ratio steady enough to gate it.</summary>
    public static bool GatesTime => OperatingSystem.IsLinux();

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
    /// <param name="allowedMissingJobs">
    /// Jobs whose committed rows may go unmeasured without failing the gate: a backend this host
    /// genuinely cannot run.
    /// </param>
    public static int Evaluate(
        IEnumerable<Summary> summaries, string baselineDirectory, string measuredPath, bool update,
        IReadOnlyCollection<string> allowedMissingJobs)
    {
        List<Row> rows = [];
        List<string> failures = [];
        HashSet<string> unmeasured = new(StringComparer.Ordinal);
        HashSet<string> ranJobs = new(StringComparer.Ordinal);
        foreach (Summary summary in summaries)
        {
            Collect(summary, rows, failures, unmeasured, ranJobs);
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
            Dictionary<string, Figures> merged = Merge(committed, rows, ranJobs, unmeasured);
            Directory.CreateDirectory(baselineDirectory);
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(measured with
            {
                Benchmarks = new SortedDictionary<string, Figures>(merged, StringComparer.Ordinal)
                    .ToDictionary(StringComparer.Ordinal),
            }, GateJson.Default.BaselineFile) + "\n");
            Console.WriteLine($"Wrote {rows.Count} rows to {baselinePath}.");
            return failures.Count == 0 ? 0 : 1;
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"### Benchmark gate ({Platform})");
        report.AppendLine();
        if (!File.Exists(baselinePath))
        {
            // A workflow command, so the run page carries a warning annotation rather than
            // a green job whose summary alone says nothing was compared.
            Console.WriteLine($"::warning::No baseline committed for {Platform}; nothing was gated.");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"> [!WARNING]\n> No baseline is committed for {Platform} (`bench/baselines/{Platform}.json`), " +
                $"so nothing on this platform was gated.");
            report.AppendLine();
        }

        string rule = GatesTime
            ? $"A row fails when its time ratio to `System.IO` or its allocation grows more than {Tolerance:P0}; " +
              $"a row marked allocation only is held to its allocation alone."
            : $"Time ratios are not gated on {Platform}, whose hosted runners move them by more than the tolerance; " +
              $"a row fails when its allocation grows more than {Tolerance:P0}.";
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Baseline: `{Path.GetFileName(baselinePath)}`, measured on {committed.MeasuredOn ?? "(none committed)"}. {rule}");
        report.AppendLine();

        Comparison comparison = Compare(committed, rows, unmeasured, allowedMissingJobs, report);
        failures.AddRange(comparison.Failures);

        if (comparison.Ungated > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{comparison.Ungated} row(s) have no committed baseline and were not gated. To start gating them, " +
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

    /// <summary>
    /// Holds every measured row to its committed figures, and every committed row to having been
    /// measured, appending the table to <paramref name="report"/>.
    /// </summary>
    /// <remarks>
    /// A committed row this run did not measure fails: otherwise deleting a benchmark, renaming
    /// it, dropping a parameter value or losing a whole backend would be the quietest way past
    /// the gate. A row whose job is in <paramref name="allowedMissingJobs"/> is reported but does
    /// not fail. A row that was attempted and produced no result has already failed, so it is
    /// not reported a second time.
    /// </remarks>
    /// <param name="committed">The baseline this run is held to.</param>
    /// <param name="rows">What this run measured.</param>
    /// <param name="unmeasured">Rows this run attempted that produced no result.</param>
    /// <param name="allowedMissingJobs">Jobs whose committed rows may go unmeasured.</param>
    /// <param name="report">Where the table is written.</param>
    internal static Comparison Compare(
        BaselineFile committed, IReadOnlyList<Row> rows, IReadOnlySet<string> unmeasured,
        IReadOnlyCollection<string> allowedMissingJobs, StringBuilder report)
    {
        List<string> failures = [];
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
            if (row.TimeGated && row.Figures.Ratio is double ratio && expected.Ratio is double expectedRatio
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
                $"{(problems.Count > 0 ? "**regressed**" : row.TimeGated ? "ok" : "ok (allocation only)")} |");
        }

        HashSet<string> measured = rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        int allowedMissing = 0;
        foreach ((string key, Figures expected) in committed.Benchmarks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (measured.Contains(key) || unmeasured.Contains(key))
            {
                continue;
            }

            bool allowed = allowedMissingJobs.Contains(JobOf(key), StringComparer.Ordinal);
            if (allowed)
            {
                allowedMissing++;
            }
            else
            {
                failures.Add($"{key}: in the baseline but not measured");
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {key} | – | {Format(expected.Ratio)} | – | {expected.AllocatedBytes} B | " +
                $"{(allowed ? "missing (allowed)" : "**missing**")} |");
        }

        report.AppendLine();
        if (allowedMissing > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{allowedMissing} committed row(s) were not measured and were let pass, since their job was allowed " +
                $"to be missing on this host ({string.Join(", ", allowedMissingJobs.Order(StringComparer.Ordinal))}).");
            report.AppendLine();
        }

        return new Comparison(failures, ungated);
    }

    /// <summary>
    /// The baseline <c>--update</c> writes: this run's rows, plus the committed rows this run had
    /// no chance to measure.
    /// </summary>
    /// <remarks>
    /// A committed row is kept when its job did not run here -- the confined-open job on a kernel
    /// without it -- so as not to delete another machine's figures, or when it was attempted and
    /// failed, so that a broken run does not erase the line it failed to reach. A committed row
    /// of a job that did run, and that this run did not produce at all, is a benchmark that no
    /// longer exists, and is dropped.
    /// </remarks>
    /// <param name="committed">The baseline being replaced.</param>
    /// <param name="rows">What this run measured.</param>
    /// <param name="ranJobs">The jobs this run ran.</param>
    /// <param name="unmeasured">Rows this run attempted that produced no result.</param>
    internal static Dictionary<string, Figures> Merge(
        BaselineFile committed, IReadOnlyList<Row> rows, IReadOnlySet<string> ranJobs, IReadOnlySet<string> unmeasured)
    {
        Dictionary<string, Figures> merged = rows.ToDictionary(r => r.Key, r => r.Figures, StringComparer.Ordinal);
        foreach ((string key, Figures figures) in committed.Benchmarks)
        {
            if (!ranJobs.Contains(JobOf(key)) || unmeasured.Contains(key))
            {
                merged.TryAdd(key, figures);
            }
        }

        return merged;
    }

    private static void Collect(
        Summary summary, List<Row> rows, List<string> failures, HashSet<string> unmeasured, HashSet<string> ranJobs)
    {
        foreach (BenchmarkReport report in summary.Reports)
        {
            BenchmarkCase benchmark = report.BenchmarkCase;
            string key = KeyOf(benchmark);
            ranJobs.Add(benchmark.Job.Id);

            if (!report.Success || report.ResultStatistics is null)
            {
                failures.Add($"{key}: did not produce a result");
                unmeasured.Add(key);
                continue;
            }

            if (benchmark.Descriptor.WorkloadMethod.Name == Categories.SystemIOMethod)
            {
                continue;
            }

            double? ratio = null;
            bool timeGated = false;
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
                    timeGated = GatesTime
                        && yardstick.BenchmarkCase.Descriptor.WorkloadMethod.Name == Categories.SystemIOMethod
                        && !benchmark.Descriptor.HasCategory(Categories.AllocationOnly);
                }
            }

            long allocated = report.GcStats.GetBytesAllocatedPerOperation(benchmark) ?? 0;
            rows.Add(new Row(key, new Figures(ratio, allocated), timeGated));
        }
    }

    private static string KeyOf(BenchmarkCase benchmark)
    {
        string parameters = benchmark.HasParameters ? benchmark.Parameters.DisplayInfo : string.Empty;
        return $"{benchmark.Descriptor.Type.Name}.{benchmark.Descriptor.WorkloadMethod.Name}{parameters}/{benchmark.Job.Id}";
    }

    /// <summary>The job a row's key names, the part after its last slash.</summary>
    internal static string JobOf(string key) => key[(key.LastIndexOf('/') + 1)..];

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

    /// <param name="Key">The row's name in the baseline file.</param>
    /// <param name="Figures">What this run measured.</param>
    /// <param name="TimeGated">
    /// Whether the ratio is to a <c>System.IO</c> baseline in a class not marked allocation-only,
    /// and so held to the tolerance.
    /// </param>
    internal sealed record Row(string Key, Figures Figures, bool TimeGated);

    /// <param name="Failures">One line per row that regressed or went missing.</param>
    /// <param name="Ungated">How many measured rows had no committed figures to be held to.</param>
    internal sealed record Comparison(IReadOnlyList<string> Failures, int Ungated);

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
