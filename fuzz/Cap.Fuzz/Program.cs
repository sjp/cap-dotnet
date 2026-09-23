using Cap.Fuzz.Targets;
using SharpFuzz;

// The fuzzer is a separate native driver that starts this process and feeds it inputs over
// shared memory; see docs/fuzzing.md for how to run it. Started by that driver, this names
// the target to run. Started by hand with `replay`, it runs saved inputs through a target
// once each, which is how a crash found overnight is reproduced on any machine and under a
// debugger, without the driver and without an instrumented build.
const string Usage = """
    Usage:
      Cap.Fuzz <target>                          run a target under the libFuzzer driver
      Cap.Fuzz replay <target> <file|dir>...     run saved inputs through a target once each
      Cap.Fuzz list                              name every target
    """;

if (args is ["list"])
{
    foreach (string name in FuzzTargets.All.Keys)
    {
        Console.WriteLine(name);
    }

    return 0;
}

if (args is ["replay", string replayed, .. string[] paths] && paths.Length > 0)
{
    return FuzzTargets.All.TryGetValue(replayed, out FuzzTargets.Target? replayTarget)
        ? Replay(replayTarget, paths)
        : Unknown(replayed);
}

if (args is [string named])
{
    if (!FuzzTargets.All.TryGetValue(named, out FuzzTargets.Target? target))
    {
        return Unknown(named);
    }

    Fuzzer.LibFuzzer.Run(input => target(input));
    return 0;
}

Console.Error.WriteLine(Usage);
return 2;

static int Unknown(string name)
{
    Console.Error.WriteLine($"No target is called '{name}'. The targets are: {string.Join(", ", FuzzTargets.All.Keys)}.");
    return 2;
}

static int Replay(FuzzTargets.Target target, string[] paths)
{
    IEnumerable<string> files = paths.SelectMany(path => Directory.Exists(path)
        ? Directory.EnumerateFiles(path).Where(file => !Path.GetFileName(file).StartsWith('.')).Order(StringComparer.Ordinal)
        : (IEnumerable<string>)[path]);

    int failures = 0;
    foreach (string file in files)
    {
        try
        {
            target(File.ReadAllBytes(file));
            Console.WriteLine($"ok    {file}");
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"FAIL  {file}");
            Console.WriteLine(exception);
        }
    }

    return failures == 0 ? 0 : 1;
}
