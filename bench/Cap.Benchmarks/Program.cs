using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Cap.Benchmarks;
using Perfolizer.Horology;

// Every cap-dotnet operation is measured against its System.IO baseline where the two do the
// same thing, one job per resolution backend this host has.
//
//   dotnet run -c Release --project bench/Cap.Benchmarks -- --filter '*'
//       The full suite, with BenchmarkDotNet's usual options.
//
//   dotnet run -c Release --project bench/Cap.Benchmarks -- gate [--update]
//       The hot-path subset, compared against bench/baselines/<os>.json. Exits non-zero when a
//       row has regressed by more than the gate's tolerance; --update rewrites the baseline
//       from this run instead.
if (args.Length > 0 && args[0] == "gate")
{
    return RunGate(args[1..]);
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, Configure(Job.Default));
return 0;

static int RunGate(string[] args)
{
    bool update = args.Contains("--update");
    string baselines = Option(args, "--baselines") ?? Path.Join(RepositoryRoot(), "bench", "baselines");
    string measured = Option(args, "--out")
        ?? Path.Join(Environment.CurrentDirectory, "BenchmarkDotNet.Artifacts", "gate", Gate.Platform + ".json");

    // Shorter than the default job, which would take the gate the better part of an hour across
    // two backends; long enough that the median of a microsecond operation settles. Each
    // iteration still runs tens of thousands of operations, so a fifth of the default
    // iteration time costs the median nothing. The time is gated as a ratio, so a slow machine
    // does not need more iterations to pass.
    Job timed = Job.Default
        .WithWarmupCount(4)
        .WithIterationCount(20)
        .WithIterationTime(TimeInterval.FromMilliseconds(100));

    // A class held to its allocation alone needs its figure only once: bytes per operation do
    // not settle over iterations the way time does. Its ratio is still reported, and is noisier
    // for the shorter run, but it is not gated.
    Job allocationOnly = Job.Default
        .WithWarmupCount(1)
        .WithIterationCount(3)
        .WithIterationTime(TimeInterval.FromMilliseconds(50));

    IConfig timedConfig = Configure(timed)
        .AddFilter(new BenchmarkDotNet.Filters.AnyCategoriesFilter([Categories.HotPath]))
        .AddFilter(new BenchmarkDotNet.Filters.SimpleFilter(b => !b.Descriptor.HasCategory(Categories.AllocationOnly)));
    IConfig allocationConfig = Configure(allocationOnly)
        .AddFilter(new BenchmarkDotNet.Filters.AllCategoriesFilter([Categories.HotPath, Categories.AllocationOnly]));

    Summary[] summaries =
    [
        .. BenchmarkRunner.Run(typeof(Program).Assembly, timedConfig),
        .. BenchmarkRunner.Run(typeof(Program).Assembly, allocationConfig),
    ];
    return Gate.Evaluate(summaries, baselines, measured, update);
}

static IConfig Configure(Job template)
{
    ManualConfig config = ManualConfig.Create(DefaultConfig.Instance);
    IReadOnlyList<Job> jobs = Backend.Jobs(template, Console.WriteLine);
    foreach (Job job in jobs)
    {
        config.AddJob(job);
    }

    return config.AddFilter(new BenchmarkDotNet.Filters.SimpleFilter(benchmark => Backend.Runs(benchmark, jobs)));
}

static string? Option(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string RepositoryRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "CapDotnet.slnx")))
            {
                return directory.FullName;
            }
        }
    }

    throw new InvalidOperationException("Run from inside the repository, or pass --baselines.");
}

/// <summary>Entry point marker for <see cref="BenchmarkSwitcher"/>.</summary>
public sealed partial class Program;
