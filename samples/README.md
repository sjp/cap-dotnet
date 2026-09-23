# Samples

## `AmbientAudit`

Where a process reaches outside itself, printed at the end of a run.

```bash
dotnet run --project samples/AmbientAudit
```

A report writer arranged the way an application using this library is meant to be:
authority enters once, at the top, and the handle it produces is passed down. It also
contains one piece of code that reaches for the system temporary directory on its own, as a
dependency would. The dump names both, and the counts tell the once-at-start-up entry from
the once-per-call one:

```
Ambient authority was taken at 2 sites:
  .../samples/AmbientAudit/Program.cs:40 in Main: taken once at +0.000s
  .../samples/AmbientAudit/Program.cs:92 in Render: taken 3 times, first at +0.003s
```

The recording is off unless asked for; this sample asks for it in its project file. See
[docs/ambient-authority.md](../docs/ambient-authority.md).

## `ArchiveExtractor`

A zip extractor that can't be made to write outside its destination. There's no check that
could be got wrong: each entry's name goes as-is to a `Dir` on the destination, and a name
that resolves to somewhere else is refused by the resolution itself.

```bash
dotnet run --project samples/ArchiveExtractor                            # the demonstration
dotnet run --project samples/ArchiveExtractor -- archive.zip destination  # a real archive
```

Run with no arguments, it builds a hostile archive of its own, extracts it into a scratch
directory, and exits non-zero if anything was written outside it:

```
Extracting a hostile archive into /tmp/cap-archive-extractor-AJHBEz/out:
  wrote    readme.txt
  wrote    docs/guide/intro.txt
  wrote    docs/guide/usage.txt
  refused  ../escaped.txt  (outside the destination)
  refused  docs/../../escaped.txt  (outside the destination)
  refused  docs/guide/../../../escaped.txt  (outside the destination)
  refused  /escaped-absolute.txt  (outside the destination)
```

It is also the program whose NativeAOT size is recorded in [docs/aot.md](../docs/aot.md).
CI publishes it as a native executable on every platform and runs the demonstration.

## Planned

- **Sandboxed file server** — serve a directory tree where a crafted request path is
  structurally incapable of escaping it.
- **Plugin host** — hand each plugin a `Dir` on its own data directory and nothing else.
