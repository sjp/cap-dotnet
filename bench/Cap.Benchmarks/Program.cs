using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Cap.Benchmarks;

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
    // two backends; long enough that the median of a microsecond operation settles. The time
    // is gated as a ratio, so a slow machine does not need more iterations to pass.
    Job template = Job.Default.WithWarmupCount(4).WithIterationCount(20);

    IConfig config = Configure(template).AddFilter(new BenchmarkDotNet.Filters.AnyCategoriesFilter([Categories.HotPath]));
    Summary[] summaries = BenchmarkRunner.Run(typeof(Program).Assembly, config);
    return Gate.Evaluate(summaries, baselines, measured, update);
}

static IConfig Configure(Job template)
{
    ManualConfig config = ManualConfig.Create(DefaultConfig.Instance);
    foreach (Job job in Backend.Jobs(template, Console.WriteLine))
    {
        config.AddJob(job);
    }

    return config;
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
