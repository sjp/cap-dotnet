# NativeAOT and trimming

Every shipping assembly can be compiled ahead of time with NativeAOT, trimmed, and published
as a single file, with no warnings and no suppressions. The escape corpus runs against the
published binary on Linux, Windows and macOS in CI, in both shapes, so this is tested rather
than just claimed.

## What an application can rely on

- **No trim or AOT warnings from this library.** Every assembly under `src/` builds with
  `IsAotCompatible` and `IsTrimmable` set, which turns on the trim, AOT and single-file
  analyzers, and every warning is a build error. Nothing in `src/` suppresses one, and in
  the assemblies that resolve names (`Cap.Primitives`, `Cap.Std`) the attribute that would
  suppress one is banned (`build/CapBannedSymbols.Primitives.txt`). A publish of an
  application that uses the library reports no warnings that come from it.
- **No reflection, no `Activator.CreateInstance`, no code generated at run time.** Nothing
  in the library looks a member up by name, creates an instance of a type it was given, or
  emits code. Any of those that the trimmer could not follow would be one of the warnings
  above, so the build would fail on it.
- **No run-time marshalling.** Every call into the operating system is a `[LibraryImport]`
  stub written by the source generator at compile time (`[DllImport]` is a build error,
  SYSLIB1054). `Cap.Primitives`, the only assembly that calls the operating system, declares
  `[assembly: DisableRuntimeMarshalling]`, so a declaration that would need the runtime to
  marshal a non-blittable type does not compile, and there is no marshalling code that
  would have to be generated when the program runs.

## How it is checked

A publish with no warnings only tells you the compiler could analyse everything. It
doesn't tell you the code it kept still behaves the same as native code, or after the
trimmer has removed what it judged unused. The only way to know that is to run the
containment tests against the binary that comes out of the publish.

`tests/Cap.Escape.Aot.Tests` is the escape corpus built from the same sources as
`tests/Cap.Escape.Tests`, but against xunit's ahead-of-time packages (`xunit.v3.aot.mtp-off`),
which find tests with a source generator instead of reflection. The `nativeaot` job in
`.github/workflows/ci.yml` publishes it two ways on each of Linux, Windows and macOS and runs
each result:

| `CapPublishAs` | What is published |
|---|---|
| `native` (default) | A NativeAOT executable: no JIT, and no IL when it runs |
| `single-file` | Self-contained, trimmed, bundled into one file and run on the JIT |

Both carry every backend the host has, and the corpus runs each of them under both link
policies, as it does under `dotnet test`. A trim or AOT warning fails the publish
(`IlcTreatWarningsAsErrors`, `ILLinkTreatWarningsAsErrors`), and a test failure fails the
job.

To do the same locally:

```bash
dotnet publish tests/Cap.Escape.Aot.Tests -c Release --use-current-runtime -o out/native
out/native/Cap.Escape.Aot.Tests

dotnet publish tests/Cap.Escape.Aot.Tests -c Release --use-current-runtime \
  -p:CapPublishAs=single-file -o out/single-file
out/single-file/Cap.Escape.Aot.Tests
```

The publish shape is selected with `CapPublishAs` instead of passing `-p:PublishAot=true` on
the command line. A command-line property applies to every referenced project, including the
analyzer, and the analyzer targets netstandard2.0, which can be neither compiled ahead of
time nor trimmed. NativeAOT cannot cross-compile to a different processor architecture
without extra toolchain setup, so `--use-current-runtime` builds for the machine you're on.

`dotnet test` does not run this project. On the JIT it is the same suite as
`Cap.Escape.Tests`, which `dotnet test` already runs.

## Size

`samples/ArchiveExtractor`, a zip extractor that cannot be made to write outside its
destination, published as NativeAOT with the SDK's default settings:

| Platform | ArchiveExtractor | Same program on `System.IO` alone | Difference |
|---|---|---|---|
| linux-arm64 | 2,441,208 bytes (2.33 MiB) | 1,979,240 bytes (1.89 MiB) | 461,968 bytes (451 KiB) |

Measured with SDK 10.0.400 (runtime 10.0.11). The size is the executable alone: on Linux the
default publish strips debug symbols into a separate `.dbg` file, which you don't ship.

The comparison program is the naive version of the same tool. It joins each entry's name
onto the destination path and writes to wherever that points, which is the zip-slip bug
itself. It does the same work with `ZipFile`, `File` and `Directory`, so the difference is
roughly what the library itself adds: the path parser, the three resolution backends, the
handle types and the part of the convenience layer the sample uses.

The `nativeaot` CI job publishes the sample on every platform it covers, runs it, and writes
its size to the job summary. That summary is where to find the Windows and macOS figures,
and where to check that the Linux one still holds.
