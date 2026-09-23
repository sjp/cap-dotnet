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

On Linux every benchmark runs twice, as two BenchmarkDotNet jobs:

| Job | What it measures |
|---|---|
| `openat2` | The kernel's confined open: one syscall per path, however many names it has |
| `walk` | The name-at-a-time walk, selected with `CAPDOTNET_DISABLE_OPENAT2=1` — what a kernel older than 5.6, or a seccomp profile that refuses the syscall, gets |

macOS has only the walk, and Windows only its relative native open, so each runs a single job
named `walk` or `windows`.

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

RESULTS_PLACEHOLDER

## The regression gate

`gate` runs the classes marked ✓ above and compares them with the figures committed in
`bench/baselines/<os>.json`. CI runs it on every change on Linux, Windows and macOS, and fails
when any row has become more than 10% worse.

**Time is compared as a ratio, not as a duration.** A hosted CI runner's speed wanders from one
run to the next by more than the 10% being guarded, so a gate on absolute time would fail at
random. Every gated operation is measured in the same run, on the same machine, as its
`System.IO` baseline, and what the gate holds steady is the ratio between the two. That ratio
moves when this library gets slower and mostly does not when the machine does. The parser has no
`System.IO` row, so its rows are held to their ratio against the single-component parse. Medians
are used rather than means, so that one iteration interrupted by the runner doing something else
does not decide the verdict.

**Allocation is compared as bytes per operation**, which does not depend on the machine. A row
whose committed allocation is zero fails on its first byte.

The `System.IO` rows themselves are not gated: what the runtime allocates, or how its speed
moves between versions, is not a regression in this library.

Each gate run writes its own figures, in the same format as the committed file, to
`BenchmarkDotNet.Artifacts/gate/<os>.json`, and CI uploads them. That upload, run after run,
is the history; the committed file is the line a change must not cross.

### Moving the baseline

A change that makes an operation slower on purpose — a new check on the hot path, say — moves
the baseline in the same commit:

```bash
dotnet run -c Release --project bench/Cap.Benchmarks -- gate --update
```

This rewrites the current platform's file from a fresh run, keeping any rows the host could not
measure (the `openat2` job on a kernel without it). Take the figures from the platform's CI
runner where possible — the uploaded `benchmark-gate-<platform>` artifact holds them — since a
ratio measured on a laptop's filesystem is not quite the ratio a hosted runner sees. A platform
with no committed file is run and reported but not gated; committing its artifact's file turns
the gate on for it.
