# Fuzzing and property tests

The escape corpus holds the attacks someone thought of. This covers the ones nobody did: the
code that decides containment is run on inputs a machine chose, and checked for the things
that must hold whatever the input.

There are two tools for this, and they complement each other.

- **Fuzzing** runs libFuzzer against the code with branch coverage switched on. It keeps any
  input that reaches code no earlier input reached, and mutates from there. It finds deep and
  odd paths, but its findings are raw bytes, and it needs an hour to get anywhere. It runs
  nightly. A pull request runs the harness only briefly, to check that it still builds and
  still gets coverage back (see [On every change](#on-every-change)).
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
and reparse points that redirect without being links. The sandbox sits beside a directory that stands for
everything outside it. The input also chooses the syntax, POSIX or Windows, that the path is
parsed by and the link targets are read by. Under Windows syntax, it chooses whether words are
joined with `\` or `/`. Windows falls back to this walk, so it is fuzzed under Windows rules as
well. A name the input spells out can be as long as the parser's 255-character limit, in any
characters. Whatever the tree and whatever the path, the walk must:

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

**The confined open in memory** (`resolution-memory`). `Cap.Std.Testing` has a second
resolver, `MemoryPathWalk`, which resolves a whole path in one call as `openat2` does under
`RESOLVE_BENEATH`. It keeps its own count of `..`, its own limit on links and its own
refusals of links and mount crossings. The `in-memory-confined` CI legs use it in place of the
kernel, and it ships to consumers in the `Cap.Std.Testing` package. If it were more permissive
than the kernel, those legs would pass while hiding an escape. This target builds the same
trees as `resolution`, under the same syntax, copied into an `InMemoryFileSystem` that resolves
by the confined open. Its names are case-sensitive under both syntaxes, because the simulation
the walk runs over is.
It runs each operation through the library's own choice of resolver, which is the path those
legs take, and holds the result to every check listed for the walk. That path gives no way to
watch each lookup, so the target also runs `MemoryPathWalk` directly with a lookup that
refuses to be asked about any directory outside the sandbox.

It must also **agree with the walk**. When nothing changes the tree during resolution, the
confined open and the walk are meant to reach the same verdict. So the same scenario goes
through the walk as well, and both must succeed and reach the same object, or both must fail
for the same reason. There is no list of permitted differences: when the two disagreed, the
code that differed from the kernel was fixed. The property tests run this over general trees,
over trees made mostly of links (where the two resolvers keep their accounts differently), and
over trees without links, compared with reading the path as text.

## Starting inputs and saved inputs

The fuzzer starts from the escape corpus. `tests/Cap.Fuzz.Tests/EscapeCorpusSeeds.cs` turns
each case into inputs for the four targets:

- its path goes to the parser, under both syntaxes;
- its tree goes to the walk and to the confined open in memory, through each operation and
  under both syntaxes;
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
2. instruments `Cap.Primitives` and `Cap.Std.Testing` with SharpFuzz, pinned in
   `.config/dotnet-tools.json`, so the fuzzer can see which branches an input reached;
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

## On every change

The `fuzz-smoke` job in `.github/workflows/ci.yml` runs `build/fuzz/run.sh` for 45 seconds
each on `cap-path` and `resolution-memory`. Between them, those two targets drive both
instrumented assemblies. The job is there to catch a broken harness: a wrong driver digest, a
clang flag the runner's compiler refuses, a SharpFuzz release that no longer works with the
SDK, or a change to `Cap.Primitives` or `Cap.Std.Testing` that SharpFuzz cannot instrument.
The driver is built from source on every run, so its digest is checked every time.

Each target starts from an empty corpus, and the job fails unless libFuzzer's final
`stat::new_units_added` is above zero. libFuzzer keeps an input only when it reaches a branch
that no earlier input reached. The only branches it can see in the target are the ones
SharpFuzz instrumented, so with no instrumentation it keeps nothing. Do not use the `cov:`
figure for this check. It counts the driver's own code and stays the same whether the target
is instrumented or not.

## On the schedule

`.github/workflows/nightly.yml` runs each target for an hour and the property tests at a
million cases. Each target starts from the escape corpus plus the inputs earlier runs kept,
which are carried between runs in the Actions cache. The cache drops an entry that goes
unused for a week, and drops the oldest entries once the repository's 10 GB is full. So the
Sunday run also uploads each target's corpus as an artifact named `fuzz-corpus-<target>`,
kept for 90 days. When the cache restores nothing, the run downloads the newest such artifact
from a run on `main`. When there is neither, it starts from the escape corpus alone and says
so in a warning.

A run that finds a failing input fails. It uploads the input as an artifact laid out as
`fuzz/regressions/<target>/<file>`, which lands in the right place when unzipped at the root of
a checkout. The artifact is kept for 90 days. Nothing is committed automatically, and the job's
token is read-only. Anyone who can see the repository can download a run's artifacts. So once
the input for a finding that reaches outside the sandbox is saved somewhere private, delete
that artifact from the run.

A failed night also opens an issue labelled `nightly`, or comments on the one already open,
with a link to the run and the jobs that failed. It carries no input, only the run link, and
no green night closes it: close it once its failures have been triaged.

**A finding that reaches outside the sandbox is a vulnerability.** Report it as
[SECURITY.md](../SECURITY.md) describes, and fix it before its input goes into a public pull
request. The input is the exploit.
