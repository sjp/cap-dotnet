using BenchmarkDotNet.Running;

// Benchmarks land in 0027. Every cap-dotnet operation is measured against its System.IO
// baseline, and the openat2 and fallback backends are separate jobs so the cost of not
// having openat2 is a number we can quote rather than a guess.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry point marker for <see cref="BenchmarkSwitcher"/>.</summary>
public sealed partial class Program;
