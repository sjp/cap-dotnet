# Benchmarks

What does reaching a file through a handle cost, compared with reaching it through a path
and the process's ambient authority? The suite in `bench/Cap.Benchmarks` answers that for
each operation, on each platform and each resolution backend, with `System.IO` as the
baseline every time.

## Running them

```bash
# The full suite: every operation, both ways, one job per backend this host has.
dotnet run -c Release --project bench/Cap.Benchmarks -- --filter '*'

# One operation.
dotnet run -c Release --project bench/Cap.Benchmarks -- --filter '*OpenReadFiveComponents*'

# The hot-path subset against the committed baseline, as CI runs it.
dotnet run -c Release --project bench/Cap.Benchmarks -- gate
```

Anything after `--` other than `gate` is passed to BenchmarkDotNet, so its usual options
(`--list flat`, `--exporters json`, `--job short`) work. Reports land in
`BenchmarkDotNet.Artifacts/` under the directory the command was run from.

Every benchmark creates its own scratch tree under the system temporary directory and removes
it afterwards, so the numbers are for whatever filesystem that is. The enumeration and tree
benchmarks create a hundred and fifty thousand files between them per backend; expect the full
suite to take the better part of an hour.

## One job per backend

On Linux every benchmark that reaches the filesystem runs twice, as two BenchmarkDotNet jobs:

| Job | What it measures |
|---|---|
| `openat2` | The kernel's confined open: one syscall per path, however many names it has |
| `walk` | The name-at-a-time walk, selected with `CAPDOTNET_DISABLE_OPENAT2=1` — what a kernel older than 5.6, or a seccomp profile that refuses the syscall, gets |

macOS has only the walk, and Windows only its relative native open, so each runs a single job
named `walk` or `windows`.

None of the paths measured go through a symbolic link. A link in a path costs the Linux walk
two syscalls (`openat`, `readlinkat`) and the macOS walk three, because macOS reports a link
met by a directory open as "not a directory" and the backend has to `fstatat` the name to
tell it from a file; see
[What the component-by-component walk does](backends.md#what-the-component-by-component-walk-does).

Path parsing never touches the filesystem, so it measures the same thing under every backend and
runs under one job only: `walk` on Linux and macOS, `windows` on Windows.

The walk is a separate job, in a separate process, so that the cost of not having the confined
open is a row of its own that can be quoted rather than a guess. Each benchmark checks before it
measures anything that the process really resolves through the backend its job is named for,
and stops if it does not: a host that quietly fell back to the walk would otherwise publish the
walk's figures under the confined open's name. On a Linux host without the confined open the
`openat2` job is left out, and the run says why.

## What is measured

Each class is one operation. Its `SystemIO` method is the baseline and its `CapDotnet` method is
the same operation through a `Dir`, so the Ratio column is the price of the handle and the Alloc
Ratio column the price in garbage.

| Class | Operation | `System.IO` baseline | In the gate |
|---|---|---|:---:|
| `OpenReadSingleComponent` | Open and read a 4 KiB file named by one component | `File.ReadAllBytes` | ✓ |
| `OpenReadFiveComponents` | The same, five components down | `File.ReadAllBytes` | ✓ |
| `PositionalRead` | Read 4 KiB at an offset from an open file | `RandomAccess.Read` on a raw handle | ✓ |
| `StatFile` | When a file last changed | `File.GetLastWriteTimeUtc` | ✓ |
| `EnumerateDirectory` | List a directory of 100,000 entries | `Directory.EnumerateFiles` | |
| `WalkTree` | Walk a tree of 50,000 files, 100 directories two levels deep | `Directory.EnumerateFiles(…, AllDirectories)` | |
| `CreateDeleteFiles` | Create an empty file and delete it, 10,000 times, reported per file | `File.OpenHandle` / `File.Delete` | |
| `CapPathBenchmarks` | Parse and validate a path | none — see below | ✓ |

Two choices of baseline are worth explaining.

**Creating files is compared against `File.OpenHandle`, not `File.Create`.** A `CapFile` is a
handle with positional reads and writes. The `System.IO` object of that shape is a
`SafeFileHandle`; `File.Create` wraps one in a `FileStream`, which would add an allocation to
the baseline that the other side never makes and flatter the comparison.

**Path parsing has no `System.IO` baseline.** The obvious candidate, `Path.GetFullPath`, does a
different job with a different answer: it consults the process's working directory and rewrites
the string, collapsing `..` as text. Putting the two side by side would invite reading the gap
as a cost of safety rather than as two functions doing different things. The parser is measured
on its own, and what it has to show is an Allocated column of zero: a parsed path borrows the
caller's string, and walking its components yields spans into that same string.

## Results

One run of the full suite on each platform's hosted GitHub runner, from the `benchmarks.yml`
workflow on 2026-09-30. The tables are BenchmarkDotNet's GitHub-flavoured reports as that run
uploaded them, with the `EnvironmentVariables` column dropped since the Job column already names
the backend. On Linux every filesystem class has one group of rows per job, `openat2` and `walk`;
Windows and macOS each have a single job, so their tables have no Job column.

A Ratio is only comparable with another from the same run, and on Windows and macOS even that is
loose: see [the regression gate](#the-regression-gate) for how far their runners move on unchanged
code. The Allocated column does not depend on the machine. To refresh these tables, run the
workflow and replace each platform's section with its `benchmarks-<platform>` artifact's
`*-report-github.md` files.

### Linux x64 (`ubuntu-latest`, Ubuntu 24.04.5 LTS, .NET 10.0.12, 2026-09-30)

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
AMD EPYC 9V45 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]  : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  openat2 : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  walk    : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
```

#### `OpenReadSingleComponent`

| Method    | Job     | Mean     | Error     | StdDev    | Ratio | Gen0   | Allocated | Alloc Ratio |
|---------- |-------- |---------:|----------:|----------:|------:|-------:|----------:|------------:|
| SystemIO  | openat2 | 5.517 μs | 0.0306 μs | 0.0239 μs |  1.00 | 0.2441 |   4.09 KB |        1.00 |
| CapDotnet | openat2 | 5.301 μs | 0.0213 μs | 0.0199 μs |  0.96 | 0.2518 |   4.13 KB |        1.01 |
|           |         |          |           |           |       |        |           |             |
| SystemIO  | walk    | 5.691 μs | 0.0366 μs | 0.0325 μs |  1.00 | 0.2441 |   4.09 KB |        1.00 |
| CapDotnet | walk    | 5.182 μs | 0.0095 μs | 0.0084 μs |  0.91 | 0.2518 |   4.13 KB |        1.01 |

#### `OpenReadFiveComponents`

| Method    | Job     | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------- |-------- |----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| SystemIO  | openat2 |  5.877 μs | 0.0217 μs | 0.0192 μs |  1.00 |    0.00 | 0.2441 |   4.09 KB |        1.00 |
| CapDotnet | openat2 |  5.539 μs | 0.0196 μs | 0.0164 μs |  0.94 |    0.00 | 0.2518 |   4.13 KB |        1.01 |
|           |         |           |           |           |       |         |        |           |             |
| SystemIO  | walk    |  5.836 μs | 0.0587 μs | 0.0549 μs |  1.00 |    0.01 | 0.2441 |   4.09 KB |        1.00 |
| CapDotnet | walk    | 12.269 μs | 0.0436 μs | 0.0387 μs |  2.10 |    0.02 | 0.2594 |   4.31 KB |        1.06 |

#### `PositionalRead`

| Method    | Job     | Mean     | Error   | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |-------- |---------:|--------:|---------:|------:|--------:|----------:|------------:|
| SystemIO  | openat2 | 482.9 ns | 3.90 ns |  3.65 ns |  1.00 |    0.01 |         - |          NA |
| CapDotnet | openat2 | 490.0 ns | 9.79 ns | 11.27 ns |  1.01 |    0.02 |         - |          NA |
|           |         |          |         |          |       |         |           |             |
| SystemIO  | walk    | 488.3 ns | 1.67 ns |  1.40 ns |  1.00 |    0.00 |         - |          NA |
| CapDotnet | walk    | 511.0 ns | 2.70 ns |  2.40 ns |  1.05 |    0.01 |         - |          NA |

#### `StatFile`

| Method    | Job     | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |-------- |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| SystemIO  | openat2 | 1.236 μs | 0.0225 μs | 0.0210 μs |  1.00 |    0.02 |         - |          NA |
| CapDotnet | openat2 | 1.159 μs | 0.0210 μs | 0.0216 μs |  0.94 |    0.02 |         - |          NA |
|           |         |          |           |           |       |         |           |             |
| SystemIO  | walk    | 1.283 μs | 0.0117 μs | 0.0109 μs |  1.00 |    0.01 |         - |          NA |
| CapDotnet | walk    | 1.159 μs | 0.0144 μs | 0.0135 μs |  0.90 |    0.01 |         - |          NA |

#### `EnumerateDirectory`

| Method    | Job     | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|---------- |-------- |---------:|---------:|---------:|------:|--------:|---------:|----------:|------------:|
| SystemIO  | openat2 | 33.18 ms | 0.481 ms | 0.450 ms |  1.00 |    0.02 | 533.3333 |   9.16 MB |        1.00 |
| CapDotnet | openat2 | 30.19 ms | 0.497 ms | 0.465 ms |  0.91 |    0.02 | 218.7500 |   3.82 MB |        0.42 |
|           |         |          |          |          |       |         |          |           |             |
| SystemIO  | walk    | 33.43 ms | 0.642 ms | 0.659 ms |  1.00 |    0.03 | 533.3333 |   9.16 MB |        1.00 |
| CapDotnet | walk    | 30.26 ms | 0.592 ms | 0.705 ms |  0.91 |    0.03 | 218.7500 |   3.82 MB |        0.42 |

#### `WalkTree`

| Method    | Job     | Mean     | Error    | StdDev   | Ratio | Gen0     | Allocated | Alloc Ratio |
|---------- |-------- |---------:|---------:|---------:|------:|---------:|----------:|------------:|
| SystemIO  | openat2 | 15.75 ms | 0.168 ms | 0.157 ms |  1.00 | 281.2500 |   4.97 MB |        1.00 |
| CapDotnet | openat2 | 16.40 ms | 0.130 ms | 0.122 ms |  1.04 | 125.0000 |   2.01 MB |        0.41 |
|           |         |          |          |          |       |          |           |             |
| SystemIO  | walk    | 16.09 ms | 0.182 ms | 0.170 ms |  1.00 | 281.2500 |   4.97 MB |        1.00 |
| CapDotnet | walk    | 16.78 ms | 0.186 ms | 0.174 ms |  1.04 | 125.0000 |   2.01 MB |        0.41 |

#### `CreateDeleteFiles`

| Method    | Job     | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |-------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| SystemIO  | openat2 | 24.94 μs | 0.095 μs | 0.079 μs |  1.00 |    0.00 |      64 B |        1.00 |
| CapDotnet | openat2 | 22.87 μs | 0.176 μs | 0.147 μs |  0.92 |    0.01 |     104 B |        1.62 |
|           |         |          |          |          |       |         |           |             |
| SystemIO  | walk    | 25.94 μs | 0.496 μs | 0.487 μs |  1.00 |    0.03 |      64 B |        1.00 |
| CapDotnet | walk    | 22.81 μs | 0.213 μs | 0.188 μs |  0.88 |    0.02 |     104 B |        1.62 |

#### `CapPathBenchmarks`

| Method                | Syntax  | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------- |-------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| **ParseSingleComponent**  | **Unix**    |   **8.443 ns** | **0.1307 ns** | **0.1222 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| ParseShallow          | Unix    |  13.327 ns | 0.1868 ns | 0.1747 ns |  1.58 |    0.03 |         - |          NA |
| ParseDeep             | Unix    |  27.157 ns | 0.2835 ns | 0.2368 ns |  3.22 |    0.05 |         - |          NA |
| ParseAndWalkDeep      | Unix    |  69.616 ns | 0.2149 ns | 0.1795 ns |  8.25 |    0.12 |         - |          NA |
| ParseDeviceLookalikes | Unix    |  33.048 ns | 0.4826 ns | 0.4514 ns |  3.91 |    0.08 |         - |          NA |
| ValidateSpan          | Unix    |  26.869 ns | 0.2316 ns | 0.2053 ns |  3.18 |    0.05 |         - |          NA |
| ParseRejected         | Unix    |   1.508 ns | 0.0211 ns | 0.0187 ns |  0.18 |    0.00 |         - |          NA |
|                       |         |            |           |           |       |         |           |             |
| **ParseSingleComponent**  | **Windows** |  **32.483 ns** | **0.2724 ns** | **0.2415 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| ParseShallow          | Windows |  40.443 ns | 0.1982 ns | 0.1655 ns |  1.25 |    0.01 |         - |          NA |
| ParseDeep             | Windows |  92.281 ns | 0.0889 ns | 0.0743 ns |  2.84 |    0.02 |         - |          NA |
| ParseAndWalkDeep      | Windows | 110.296 ns | 0.5449 ns | 0.4551 ns |  3.40 |    0.03 |         - |          NA |
| ParseDeviceLookalikes | Windows |  97.905 ns | 0.1592 ns | 0.1412 ns |  3.01 |    0.02 |         - |          NA |
| ValidateSpan          | Windows |  71.612 ns | 0.1001 ns | 0.0887 ns |  2.20 |    0.02 |         - |          NA |
| ParseRejected         | Windows |   1.563 ns | 0.0109 ns | 0.0091 ns |  0.05 |    0.00 |         - |          NA |

### Linux arm64 (`ubuntu-24.04-arm`, Ubuntu 24.04.5 LTS, .NET 10.0.12, 2026-09-30)

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Neoverse-N2, 4 physical cores
.NET SDK 10.0.401
  [Host]  : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  openat2 : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  walk    : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
```

#### `OpenReadSingleComponent`

| Method    | Job     | Mean     | Error     | StdDev    | Ratio | Gen0   | Allocated | Alloc Ratio |
|---------- |-------- |---------:|----------:|----------:|------:|-------:|----------:|------------:|
| SystemIO  | openat2 | 3.857 μs | 0.0159 μs | 0.0149 μs |  1.00 | 0.0610 |   4.09 KB |        1.00 |
| CapDotnet | openat2 | 3.832 μs | 0.0129 μs | 0.0114 μs |  0.99 | 0.0610 |   4.13 KB |        1.01 |
|           |         |          |           |           |       |        |           |             |
| SystemIO  | walk    | 3.849 μs | 0.0355 μs | 0.0332 μs |  1.00 | 0.0610 |   4.09 KB |        1.00 |
| CapDotnet | walk    | 3.782 μs | 0.0104 μs | 0.0097 μs |  0.98 | 0.0610 |   4.13 KB |        1.01 |

#### `OpenReadFiveComponents`

| Method    | Job     | Mean     | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------- |-------- |---------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| SystemIO  | openat2 | 4.034 μs | 0.0344 μs | 0.0321 μs |  1.00 |    0.01 | 0.0610 |   4.09 KB |        1.00 |
| CapDotnet | openat2 | 4.034 μs | 0.0115 μs | 0.0102 μs |  1.00 |    0.01 | 0.0610 |   4.13 KB |        1.01 |
|           |         |          |           |           |       |         |        |           |             |
| SystemIO  | walk    | 4.000 μs | 0.0198 μs | 0.0186 μs |  1.00 |    0.01 | 0.0610 |   4.09 KB |        1.00 |
| CapDotnet | walk    | 8.625 μs | 0.0768 μs | 0.0719 μs |  2.16 |    0.02 | 0.0610 |   4.31 KB |        1.06 |

#### `PositionalRead`

| Method    | Job     | Mean     | Error   | StdDev  | Ratio | Allocated | Alloc Ratio |
|---------- |-------- |---------:|--------:|--------:|------:|----------:|------------:|
| SystemIO  | openat2 | 516.6 ns | 3.05 ns | 2.85 ns |  1.00 |         - |          NA |
| CapDotnet | openat2 | 518.8 ns | 2.38 ns | 2.23 ns |  1.00 |         - |          NA |
|           |         |          |         |         |       |           |             |
| SystemIO  | walk    | 515.7 ns | 2.24 ns | 2.10 ns |  1.00 |         - |          NA |
| CapDotnet | walk    | 513.8 ns | 3.04 ns | 2.84 ns |  1.00 |         - |          NA |

#### `StatFile`

| Method    | Job     | Mean     | Error   | StdDev  | Ratio | Allocated | Alloc Ratio |
|---------- |-------- |---------:|--------:|--------:|------:|----------:|------------:|
| SystemIO  | openat2 | 694.0 ns | 2.54 ns | 2.25 ns |  1.00 |         - |          NA |
| CapDotnet | openat2 | 731.8 ns | 3.82 ns | 3.19 ns |  1.05 |         - |          NA |
|           |         |          |         |         |       |           |             |
| SystemIO  | walk    | 686.8 ns | 2.54 ns | 1.98 ns |  1.00 |         - |          NA |
| CapDotnet | walk    | 742.3 ns | 5.30 ns | 4.96 ns |  1.08 |         - |          NA |

#### `EnumerateDirectory`

| Method    | Job     | Mean     | Error    | StdDev   | Ratio | Gen0     | Allocated | Alloc Ratio |
|---------- |-------- |---------:|---------:|---------:|------:|---------:|----------:|------------:|
| SystemIO  | openat2 | 27.29 ms | 0.063 ms | 0.059 ms |  1.00 | 125.0000 |   9.16 MB |        1.00 |
| CapDotnet | openat2 | 20.69 ms | 0.037 ms | 0.035 ms |  0.76 |  31.2500 |   3.82 MB |        0.42 |
|           |         |          |          |          |       |          |           |             |
| SystemIO  | walk    | 27.32 ms | 0.077 ms | 0.072 ms |  1.00 | 125.0000 |   9.16 MB |        1.00 |
| CapDotnet | walk    | 20.65 ms | 0.061 ms | 0.057 ms |  0.76 |  31.2500 |   3.82 MB |        0.42 |

#### `WalkTree`

| Method    | Job     | Mean     | Error    | StdDev   | Ratio | Gen0    | Allocated | Alloc Ratio |
|---------- |-------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| SystemIO  | openat2 | 13.76 ms | 0.027 ms | 0.024 ms |  1.00 | 62.5000 |   4.97 MB |        1.00 |
| CapDotnet | openat2 | 13.20 ms | 0.034 ms | 0.028 ms |  0.96 | 31.2500 |   2.01 MB |        0.41 |
|           |         |          |          |          |       |         |           |             |
| SystemIO  | walk    | 13.86 ms | 0.032 ms | 0.030 ms |  1.00 | 62.5000 |   4.97 MB |        1.00 |
| CapDotnet | walk    | 13.02 ms | 0.066 ms | 0.062 ms |  0.94 | 31.2500 |   2.01 MB |        0.41 |

#### `CreateDeleteFiles`

| Method    | Job     | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |-------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| SystemIO  | openat2 | 11.89 μs | 0.095 μs | 0.084 μs |  1.00 |    0.01 |      64 B |        1.00 |
| CapDotnet | openat2 | 10.90 μs | 0.190 μs | 0.178 μs |  0.92 |    0.02 |     104 B |        1.62 |
|           |         |          |          |          |       |         |           |             |
| SystemIO  | walk    | 11.93 μs | 0.128 μs | 0.119 μs |  1.00 |    0.01 |      64 B |        1.00 |
| CapDotnet | walk    | 10.95 μs | 0.137 μs | 0.128 μs |  0.92 |    0.01 |     104 B |        1.62 |

#### `CapPathBenchmarks`

| Method                | Syntax  | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------- |-------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| **ParseSingleComponent**  | **Unix**    |  **13.652 ns** | **0.0523 ns** | **0.0464 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| ParseShallow          | Unix    |  23.694 ns | 0.0305 ns | 0.0271 ns |  1.74 |    0.01 |         - |          NA |
| ParseDeep             | Unix    |  51.739 ns | 0.0344 ns | 0.0322 ns |  3.79 |    0.01 |         - |          NA |
| ParseAndWalkDeep      | Unix    | 115.307 ns | 0.0755 ns | 0.0706 ns |  8.45 |    0.03 |         - |          NA |
| ParseDeviceLookalikes | Unix    |  55.693 ns | 0.0347 ns | 0.0307 ns |  4.08 |    0.01 |         - |          NA |
| ValidateSpan          | Unix    |  52.186 ns | 0.0236 ns | 0.0197 ns |  3.82 |    0.01 |         - |          NA |
| ParseRejected         | Unix    |   2.486 ns | 0.0024 ns | 0.0022 ns |  0.18 |    0.00 |         - |          NA |
|                       |         |            |           |           |       |         |           |             |
| **ParseSingleComponent**  | **Windows** |  **64.128 ns** | **0.1903 ns** | **0.1780 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| ParseShallow          | Windows |  84.214 ns | 0.0343 ns | 0.0304 ns |  1.31 |    0.00 |         - |          NA |
| ParseDeep             | Windows | 160.987 ns | 0.9004 ns | 0.8423 ns |  2.51 |    0.01 |         - |          NA |
| ParseAndWalkDeep      | Windows | 208.673 ns | 0.3560 ns | 0.3330 ns |  3.25 |    0.01 |         - |          NA |
| ParseDeviceLookalikes | Windows | 210.376 ns | 0.2548 ns | 0.2259 ns |  3.28 |    0.01 |         - |          NA |
| ValidateSpan          | Windows | 138.500 ns | 0.1255 ns | 0.1113 ns |  2.16 |    0.01 |         - |          NA |
| ParseRejected         | Windows |   2.028 ns | 0.0006 ns | 0.0005 ns |  0.03 |    0.00 |         - |          NA |

### Windows x64 (`windows-latest`, Windows 11, .NET 10.0.12, 2026-09-30)

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.33438/24H2/2024Update/HudsonValley) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]  : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  windows : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
```

#### `OpenReadSingleComponent`

| Method    | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| SystemIO  | 24.90 μs | 0.378 μs | 0.316 μs |  1.00 |    0.02 | 0.2441 |   4.09 KB |        1.00 |
| CapDotnet | 30.97 μs | 0.509 μs | 0.451 μs |  1.24 |    0.02 | 0.2441 |   4.13 KB |        1.01 |

#### `OpenReadFiveComponents`

| Method    | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| SystemIO  | 25.09 μs | 0.264 μs | 0.247 μs |  1.00 |    0.01 | 0.2441 |   4.27 KB |        1.00 |
| CapDotnet | 98.60 μs | 1.969 μs | 2.491 μs |  3.93 |    0.10 | 0.2441 |   4.32 KB |        1.01 |

#### `PositionalRead`

| Method    | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| SystemIO  | 1.368 μs | 0.0269 μs | 0.0299 μs |  1.00 |    0.03 |         - |          NA |
| CapDotnet | 1.351 μs | 0.0268 μs | 0.0385 μs |  0.99 |    0.03 |         - |          NA |

#### `StatFile`

| Method    | Mean     | Error    | StdDev   | Ratio | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|----------:|------------:|
| SystemIO  | 14.14 μs | 0.082 μs | 0.068 μs |  1.00 |         - |          NA |
| CapDotnet | 21.54 μs | 0.181 μs | 0.169 μs |  1.52 |      48 B |          NA |

#### `EnumerateDirectory`

| Method    | Mean     | Error    | StdDev   | Ratio | Gen0      | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|----------:|----------:|------------:|
| SystemIO  | 33.98 ms | 0.378 ms | 0.354 ms |  1.00 | 1000.0000 |  16.78 MB |        1.00 |
| CapDotnet | 16.45 ms | 0.278 ms | 0.260 ms |  0.48 |  218.7500 |   3.82 MB |        0.23 |

#### `WalkTree`

| Method    | Mean     | Error    | StdDev   | Ratio | Gen0     | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|---------:|----------:|------------:|
| SystemIO  | 22.75 ms | 0.212 ms | 0.199 ms |  1.00 | 531.2500 |    8.8 MB |        1.00 |
| CapDotnet | 20.92 ms | 0.191 ms | 0.170 ms |  0.92 | 125.0000 |   2.01 MB |        0.23 |

#### `CreateDeleteFiles`

| Method    | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| SystemIO  | 464.3 μs | 13.64 μs | 39.35 μs |  1.01 |    0.12 |      72 B |        1.00 |
| CapDotnet | 500.1 μs | 10.61 μs | 30.96 μs |  1.08 |    0.11 |     112 B |        1.56 |

#### `CapPathBenchmarks`

| Method                | Syntax  | Mean       | Error     | StdDev    | Median     | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------- |-------- |-----------:|----------:|----------:|-----------:|------:|--------:|----------:|------------:|
| **ParseSingleComponent**  | **Unix**    |  **18.172 ns** | **0.2040 ns** | **0.1704 ns** |  **18.139 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| ParseShallow          | Unix    |  24.252 ns | 0.3130 ns | 0.2928 ns |  24.248 ns |  1.33 |    0.02 |         - |          NA |
| ParseDeep             | Unix    |  57.992 ns | 0.2965 ns | 0.2628 ns |  58.016 ns |  3.19 |    0.03 |         - |          NA |
| ParseAndWalkDeep      | Unix    | 116.899 ns | 1.0828 ns | 0.9599 ns | 116.650 ns |  6.43 |    0.08 |         - |          NA |
| ParseDeviceLookalikes | Unix    |  57.496 ns | 0.8041 ns | 0.7128 ns |  57.607 ns |  3.16 |    0.05 |         - |          NA |
| ValidateSpan          | Unix    |  58.800 ns | 1.3454 ns | 3.9668 ns |  56.433 ns |  3.24 |    0.22 |         - |          NA |
| ParseRejected         | Unix    |   4.153 ns | 0.0591 ns | 0.0493 ns |   4.141 ns |  0.23 |    0.00 |         - |          NA |
|                       |         |            |           |           |            |       |         |           |             |
| **ParseSingleComponent**  | **Windows** |  **61.170 ns** | **0.7113 ns** | **0.6653 ns** |  **61.178 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| ParseShallow          | Windows |  76.576 ns | 0.7280 ns | 0.6453 ns |  76.515 ns |  1.25 |    0.02 |         - |          NA |
| ParseDeep             | Windows | 145.589 ns | 1.4245 ns | 1.2628 ns | 145.162 ns |  2.38 |    0.03 |         - |          NA |
| ParseAndWalkDeep      | Windows | 217.333 ns | 3.3849 ns | 3.1663 ns | 216.736 ns |  3.55 |    0.06 |         - |          NA |
| ParseDeviceLookalikes | Windows | 215.749 ns | 3.5155 ns | 3.2884 ns | 215.006 ns |  3.53 |    0.06 |         - |          NA |
| ValidateSpan          | Windows | 152.471 ns | 2.8134 ns | 2.4940 ns | 152.686 ns |  2.49 |    0.05 |         - |          NA |
| ParseRejected         | Windows |   4.493 ns | 0.0569 ns | 0.0532 ns |   4.490 ns |  0.07 |    0.00 |         - |          NA |

### macOS arm64 (`macos-latest`, macOS Tahoe 26.6.2, .NET 10.0.12, 2026-09-30)

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0]
Apple M1 (Virtual), 1 CPU, 3 logical and 3 physical cores
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  walk   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
```

#### `OpenReadSingleComponent`

| Method    | Mean      | Error     | StdDev    | Median   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------- |----------:|----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| SystemIO  | 10.194 μs | 0.2822 μs | 0.8052 μs | 9.704 μs |  1.01 |    0.11 | 0.6561 |   4.09 KB |        1.00 |
| CapDotnet |  9.289 μs | 0.1828 μs | 0.2502 μs | 9.175 μs |  0.92 |    0.07 | 0.6714 |   4.13 KB |        1.01 |

#### `OpenReadFiveComponents`

| Method    | Mean      | Error     | StdDev    | Median    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------- |----------:|----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| SystemIO  |  9.978 μs | 0.1994 μs | 0.4073 μs |  9.783 μs |  1.00 |    0.06 | 0.6561 |   4.09 KB |        1.00 |
| CapDotnet | 33.917 μs | 0.1004 μs | 0.0784 μs | 33.920 μs |  3.40 |    0.13 | 0.6714 |   4.31 KB |        1.06 |

#### `PositionalRead`

| Method    | Mean     | Error   | StdDev  | Ratio | Allocated | Alloc Ratio |
|---------- |---------:|--------:|--------:|------:|----------:|------------:|
| SystemIO  | 509.2 ns | 1.64 ns | 1.37 ns |  1.00 |         - |          NA |
| CapDotnet | 509.3 ns | 5.05 ns | 3.94 ns |  1.00 |         - |          NA |

#### `StatFile`

| Method    | Mean       | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |-----------:|---------:|---------:|------:|--------:|----------:|------------:|
| SystemIO  | 1,672.0 ns | 29.64 ns | 27.72 ns |  1.00 |    0.02 |         - |          NA |
| CapDotnet |   961.1 ns | 18.69 ns | 21.52 ns |  0.57 |    0.02 |         - |          NA |

#### `EnumerateDirectory`

| Method    | Mean     | Error    | StdDev   | Median   | Ratio | RatioSD | Gen0      | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|---------:|------:|--------:|----------:|----------:|------------:|
| SystemIO  | 79.51 ms | 1.576 ms | 4.153 ms | 78.12 ms |  1.00 |    0.07 | 2857.1429 |  17.55 MB |        1.00 |
| CapDotnet | 73.36 ms | 0.705 ms | 0.550 ms | 73.12 ms |  0.92 |    0.05 |  571.4286 |   3.82 MB |        0.22 |

#### `WalkTree`

| Method    | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0      | Gen1    | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|--------:|----------:|--------:|----------:|------------:|
| SystemIO  | 20.38 ms | 0.278 ms | 0.217 ms |  1.00 |    0.01 | 1531.2500 | 31.2500 |   9.18 MB |        1.00 |
| CapDotnet | 25.26 ms | 0.763 ms | 2.249 ms |  1.24 |    0.11 |  343.7500 |       - |   2.12 MB |        0.23 |

#### `CreateDeleteFiles`

| Method    | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| SystemIO  | 59.20 μs | 2.788 μs | 7.864 μs |  1.02 |    0.18 |      64 B |        1.00 |
| CapDotnet | 64.34 μs | 3.362 μs | 9.859 μs |  1.10 |    0.22 |     104 B |        1.62 |

#### `CapPathBenchmarks`

| Method                | Syntax  | Mean      | Error    | StdDev   | Median    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------- |-------- |----------:|---------:|---------:|----------:|------:|--------:|----------:|------------:|
| **ParseSingleComponent**  | **Unix**    |  **17.83 ns** | **0.674 ns** | **1.911 ns** |  **17.28 ns** |  **1.01** |    **0.14** |         **-** |          **NA** |
| ParseShallow          | Unix    |  22.33 ns | 0.405 ns | 0.379 ns |  22.36 ns |  1.27 |    0.12 |         - |          NA |
| ParseDeep             | Unix    |  53.01 ns | 1.097 ns | 3.041 ns |  51.22 ns |  3.00 |    0.33 |         - |          NA |
| ParseAndWalkDeep      | Unix    | 111.52 ns | 3.156 ns | 9.306 ns | 111.72 ns |  6.32 |    0.80 |         - |          NA |
| ParseDeviceLookalikes | Unix    |  61.29 ns | 1.265 ns | 3.333 ns |  61.06 ns |  3.47 |    0.38 |         - |          NA |
| ValidateSpan          | Unix    |  60.33 ns | 1.160 ns | 2.207 ns |  59.40 ns |  3.42 |    0.35 |         - |          NA |
| ParseRejected         | Unix    |  29.10 ns | 0.567 ns | 1.023 ns |  28.90 ns |  1.65 |    0.17 |         - |          NA |
|                       |         |           |          |          |           |       |         |           |             |
| **ParseSingleComponent**  | **Windows** |  **57.01 ns** | **1.182 ns** | **3.334 ns** |  **56.68 ns** |  **1.00** |    **0.08** |         **-** |          **NA** |
| ParseShallow          | Windows |  71.77 ns | 1.477 ns | 2.021 ns |  71.35 ns |  1.26 |    0.08 |         - |          NA |
| ParseDeep             | Windows | 137.27 ns | 2.456 ns | 3.824 ns | 137.01 ns |  2.42 |    0.15 |         - |          NA |
| ParseAndWalkDeep      | Windows | 202.42 ns | 3.678 ns | 6.725 ns | 201.32 ns |  3.56 |    0.23 |         - |          NA |
| ParseDeviceLookalikes | Windows | 184.33 ns | 3.699 ns | 5.868 ns | 184.10 ns |  3.24 |    0.21 |         - |          NA |
| ValidateSpan          | Windows | 147.73 ns | 3.150 ns | 8.988 ns | 145.79 ns |  2.60 |    0.22 |         - |          NA |
| ParseRejected         | Windows |  26.88 ns | 0.572 ns | 1.603 ns |  26.85 ns |  0.47 |    0.04 |         - |          NA |

## The regression gate

`gate` runs the classes marked ✓ above and compares them with the figures committed in
`bench/baselines/<os>.json`. CI runs it on every change on Linux, Windows and macOS; on each
platform with a committed baseline it fails when any row has become more than 10% worse (on
Windows and macOS, in allocation only; see below). A
platform with no committed file is run and reported with a warning annotation, but not gated.

**Time is compared as a ratio, not as a duration.** A hosted CI runner's speed wanders from one
run to the next by more than the 10% being guarded, so a gate on absolute time would fail at
random. Every gated operation is measured in the same run, on the same machine, as its
`System.IO` baseline, and what the gate holds steady is the ratio between the two. That ratio
moves when this library gets slower and mostly does not when the machine does. Medians
are used rather than means, so that one iteration interrupted by the runner doing something else
does not decide the verdict.

**Allocation is compared as bytes per operation**, which does not depend on the machine. A row
whose committed allocation is zero fails on its first byte.

**The parser is gated on allocation only.** It has no `System.IO` row, so its time can only be
expressed against its own single-component parse, and that ratio is reported but not gated. The
operations take tens of nanoseconds and are divided by one that takes about ten, so the ratio
moves by more than 10% between two runs of unchanged code on the same machine, and further
between one processor and another. What the parser rows exist to show is an allocation of zero,
and that is held exactly.

**`StatFile` is gated on allocation only, too.** Both sides are one metadata syscall of about a
microsecond, so the ratio between them is mostly the kernel's cost of looking up one path
component against several. That moves by a fifth between hosted runners on unchanged code,
while holding to under 1% within a single run, so it measures the runner rather than the
library. The ratio is still reported. A class opts into this with the `AllocationOnly`
benchmark category, as the parser does.

**Windows and macOS are gated on allocation only.** Their time ratios are reported but not held
to the tolerance, because the hosted runners move them too far on unchanged code. A Windows
runner agrees with itself to under 1% within a run, but `OpenReadFiveComponents` has measured
3.96 on one runner and 4.41 on another. A macOS runner scatters by a fifth within a single run,
and `OpenReadSingleComponent` has measured 0.86 on one run and 1.51 on another. On Linux the
ratios hold, and every row not marked allocation only is gated on time as well.

**An allocation-only class gets a short run.** Bytes per operation come out the same after one
iteration as after twenty, so these classes run with one warmup and three short iterations
rather than the timed classes' four and twenty. Their reported ratios are noisier for it, which
costs nothing, since those are not gated; it is most of what keeps the gate to a couple of
minutes.

The `System.IO` rows themselves are not gated: what the runtime allocates, or how its speed
moves between versions, is not a regression in this library.

**Every committed row must be measured.** A row in the committed file that the run did not
produce — a benchmark deleted or renamed, a parameter value dropped, a class taken out of the
hot path, a whole backend job left out — is listed as **missing** and fails the gate, so that
removing a row is not a way past it. The one exception is a job the host cannot run:
`--allow-missing-job <id>` (repeatable) lets that job's rows be missing, and they are listed as
"missing (allowed)". On a Linux machine without the confined open, `gate` passes
`--allow-missing-job openat2` by itself, but only when the `CI` environment variable is not
`true`: a hosted runner that started refusing `openat2` fails the gate rather than quietly
halving what it holds.

Each gate run writes its own figures, in the same format as the committed file, to
`BenchmarkDotNet.Artifacts/gate/<os>.json`, and CI uploads them. That upload, run after run,
is the history; the committed file is the line a change must not cross.

### Moving the baseline

A change that makes an operation slower on purpose — a new check on the hot path, say — moves
the baseline in the same commit:

```bash
dotnet run -c Release --project bench/Cap.Benchmarks -- gate --update
```

This rewrites the current platform's file from a fresh run. It keeps the rows of any job the
host could not run (the `openat2` job on a kernel without it), and any row the run attempted but
got no result for; it drops the rows of a job that did run but no longer produces them, so
deleting or renaming a benchmark and running `--update` leaves no stale row behind to fail the
gate. Take the figures from the platform's CI
runner where possible — the uploaded `benchmark-gate-<platform>` artifact holds them — since a
ratio measured on a laptop's filesystem is not quite the ratio a hosted runner sees. A platform
with no committed file is run and reported but not gated; committing its artifact's file turns
the gate on for it.
