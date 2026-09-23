# Fuzzing and property tests

The escape corpus holds the attacks someone thought of. This covers the ones nobody did: the
code that decides containment is run on inputs a machine chose, and checked for the things
that must hold whatever the input.

There are two tools for this, and they complement each other.

- **Fuzzing** runs libFuzzer against the code with branch coverage switched on. It keeps any
  input that reaches code no earlier input reached, and mutates from there. It finds deep and
  odd paths, but its findings are raw bytes, and it needs an hour to get anywhere. It runs
  nightly, never on a pull request.
- **Property tests** generate structured inputs with CsCheck — strings built from the
  characters path rules are about, trees built from a handful of names — and shrink a failure
  to the smallest input that still fails. They run on every change at two thousand cases per
  property, and nightly at a million.

Both apply the same checks. The fuzz targets in `fuzz/Cap.Fuzz/Targets` are compiled into the
harness and into `tests/Cap.Fuzz.Tests`, and the property tests call them directly. So a check
added to a target is enforced by both from the next run.

## What is checked

**Path parsing** (`cap-path`). The parser is compared with a second statement of its rules in
`PathOracle`. That version is written to be obvious rather than fast: split the string, drop
the empty and `.` pieces, and look each name up in a set. The two must agree on every input
about whether the path is accepted, and on its components if it is. An accepted path must also
keep every promise `CapPath` makes about itself:

- it holds the caller's own string and never rewrites it;
- its component count, `..` flag, directory flag and single-lookup flag match its components;
- splitting off its last component divides the string without changing it;
- rendering its components and parsing the result gives the same components back;
- a path Windows rules accept, with no backslash in it, is also accepted by POSIX rules.

Only the verdict is compared, not the reason given for refusing. A path with two faults may be
refused for either one, and what containment depends on is that it is refused. See
[paths.md](paths.md) for the rules themselves.

**Reparse-point data** (`reparse-data`). The reader for the structure Windows stores in a
reparse point gets arbitrary bytes, well-formed links with one field changed to any value, and
links cut short at any point. It must never throw. It must reach the same verdict and the same
name as a plain reading of the documented layout. And its answer must not change when every
byte after the data the structure declares is changed, which shows it read nothing from there.
This runs on every platform, because it depends on bytes alone.

**The walk** (`resolution`). The component-at-a-time resolver runs over a simulated tree the
input builds: directories, files, symbolic links whose targets the input writes, mount points,
and reparse points that are not links. The sandbox sits beside a directory that stands for
everything outside it. Whatever the tree and whatever the path, the walk must:

- never look up a name in a directory outside the sandbox;
- hand back only an object that is inside the sandbox;
- leave everything outside the sandbox unchanged;
- close every handle it opened, whether it succeeded or failed;
- in a tree with no links, refuse any path whose `..` steps climb above where it started;
- never throw, and always finish. A cycle of links has to hit the limit on how many links
  are followed.

The property tests add a stronger check for trees without links. There, nothing can redirect a
walk, so the walk must agree exactly with reading the path as text. It must succeed exactly
when every name exists and is a directory and no `..` climbs above the start, and it must then
reach the directory the text names.

## Starting inputs and saved inputs

The fuzzer starts from the escape corpus. `tests/Cap.Fuzz.Tests/EscapeCorpusSeeds.cs` turns
each case into inputs for the three targets:

- its path goes to the parser, under both syntaxes;
- its tree goes to the walk, through each operation;
- its link targets go to the reparse reader, written out as the structure that would store
  them.

The same seeds run through their targets on every change, so a case that stops converting is
noticed.

When the fuzzer finds an input that makes a target throw or hang, it writes the input to
`fuzz/regressions/<target>/`. `SavedInputTests` runs every file there through its target on
every change, and fails if one faults or takes more than thirty seconds. Committing a file there
turns it into a regression test.

## Running it

The harness needs Linux, `clang` and the .NET SDK:

```bash
# Seed the working corpus from the escape corpus.
CAPDOTNET_FUZZ_SEEDS=$PWD/artifacts/fuzz/corpus \
  dotnet test tests/Cap.Fuzz.Tests --filter-method "*.Seeds_are_written_out_when_asked"

# Fuzz one target for ten minutes.
build/fuzz/run.sh cap-path 600
```

`build/fuzz/run.sh` does four things:

1. publishes the harness;
2. instruments `Cap.Primitives` with SharpFuzz, pinned in `.config/dotnet-tools.json`, so the
   fuzzer can see which branches an input reached;
3. builds the libFuzzer driver from source pinned by tag and SHA-256 digest;
4. runs the target.

The working corpus in `artifacts/fuzz/corpus` is a cache and is never committed. Any
libFuzzer flag after the corpus argument is passed through.

A saved input can be reproduced on any platform, without the driver and without
instrumentation, and under a debugger:

```bash
dotnet run --project fuzz/Cap.Fuzz -- replay cap-path fuzz/regressions/cap-path
```

To run the property tests at a larger size:

```bash
CAPDOTNET_PROPERTY_ITERATIONS=1000000 dotnet test tests/Cap.Fuzz.Tests
```

When a property fails, CsCheck prints the shrunk input and a seed. Setting the environment
variable `CsCheck_Seed` to that seed replays exactly that case.

## On the schedule

`.github/workflows/nightly.yml` runs each target for an hour and the property tests at a
million cases. Each target starts from the escape corpus plus the inputs earlier runs kept,
which are carried between runs in the Actions cache.

A run that finds a failing input fails. It uploads the input as an artifact laid out as
`fuzz/regressions/<target>/<file>`, which lands in the right place when unzipped at the root of
a checkout. The artifact is kept for a week. Nothing is committed automatically, and the job's
token is read-only.

**A finding that reaches outside the sandbox is a vulnerability.** Report it as
[SECURITY.md](../SECURITY.md) describes, and fix it before its input goes into a public pull
request. The input is the exploit.
