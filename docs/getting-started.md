# Getting started

cap-dotnet asks one thing of a program's structure: that it take authority over the
filesystem in one place, at the top, and pass it down as values. Everything else follows from
that, and most of it is ordinary .NET.

## Referencing it

Reference `Cap.Std`. The package contains the `Cap.Primitives` assembly, which holds the few
types every package shares (`AmbientAuthority`, `SymlinkPolicy`, `CapFileType`), and it
carries the [analyzer](analyzers.md). Add `Cap.Fs.Ext` for atomic writes, walks, tree removal, copying and
pattern matching ([convenience-layer.md](convenience-layer.md)).

```csharp
using Cap.Primitives;
using Cap.Std;
```

## Take authority once, at the top

A `Dir` is an open directory and the authority to reach what is beneath it. The first one in
a program has to come from somewhere, and that is the only call that resolves an ordinary
path with the process's own privileges:

```csharp
// Program.cs
using Dir data = Dir.Open(configuration["DataRoot"], AmbientAuthority.Acquire());
```

`AmbientAuthority.Acquire()` is not a permission check. It is a marker: searching for it
lists every place a program reached for authority nobody handed it, and the analyzer reports
one made outside the program's entry point or a type marked `[CompositionRoot]`. See
[ambient-authority.md](ambient-authority.md).

The path given here is trusted. It comes from configuration or the command line, never from a
request: everything the library promises begins after this call returns, so an attacker who
chooses this path has chosen the sandbox.

## Pass handles down

Derive a handle for each component, covering only what it needs, and give it that:

```csharp
using Dir uploads = data.OpenOrCreateDir("uploads");
using Dir reports = data.OpenOrCreateDir("reports");

var uploadService = new UploadService(uploads);
var reportService = new ReportService(reports.Restrict(SymlinkPolicy.Deny));
```

```csharp
public sealed class UploadService(Dir uploads)
{
    public async Task SaveAsync(string fileName, byte[] body, CancellationToken cancellationToken)
    {
        // fileName is whatever the client sent. However it is spelled, it names something
        // beneath `uploads` or it is refused.
        await uploads.WriteAllBytesAsync(fileName, body, cancellationToken);
    }
}
```

Reading `UploadService` tells you everything it can touch: its constructor takes one `Dir`,
and there is no other way for it to reach the filesystem through this library. That is the
point. The question "could this component be tricked into writing somewhere else?" is
answered by its signature rather than by auditing every string it builds.

`Restrict` hands on a stricter [symbolic-link policy](threat-model.md#421-the-callers-knob-and-what-it-does-not-reach):
by default a link is followed if it stays inside the tree and refused if it leaves;
`SymlinkPolicy.Deny` refuses every link. A handle can be restricted but never loosened, and
every handle derived from it inherits the policy.

## What refusals look like

Operations throw the same exceptions `System.IO` does where the meaning is the same —
`FileNotFoundException`, `DirectoryNotFoundException`, `UnauthorizedAccessException`,
`IOException` — so existing handlers keep working. Two are new:

| Exception | Means |
|---|---|
| `SandboxEscapeException` | The path would have led outside the handle: a `..`, an absolute, drive- or root-relative path, a Windows device name, or a link pointing out. Worth logging: something tried. |
| `CapIOException` | The base of the library's own `IOException`s, including the one above. |

A path that is malformed rather than hostile — empty, or containing a character the platform
cannot store in a name — is an `ArgumentException`.

Most operations also have a `Try` form that returns false instead of throwing, for code that
expects failure as a normal outcome.

## Lifetime and threads

A `Dir` is `IDisposable` and holds one operating-system handle. Disposing it closes that
handle only: handles derived from it stay open, because each owns its own. Every member of
`Dir` is safe to call from any number of threads at once, and a disposal racing an operation
ends as that operation throwing `ObjectDisposedException`.

A `CapFile` is an open file. Its positional `Read(buffer, offset)` and `Write(buffer, offset)`
can be called concurrently; `AsStream()` gives a `FileStream` for APIs that need one, with a
stream's usual single-caller rules.

## The rest of the surface

| You want to | Use |
|---|---|
| Read or write a whole file | `dir.ReadAllText`, `ReadAllBytes`, `WriteAllBytes`, and their `Async` forms |
| Write a file so that readers see all of it or none | `dir.WriteAllTextAtomic` (`Cap.Fs.Ext`) |
| Open a file | `dir.OpenFile(path, mode, access)` → `CapFile` |
| List a directory | `dir.EnumerateEntries()` → `DirEntry`, which opens what it names without a path |
| Walk a tree, or match a pattern | `dir.Walk()`, `dir.Glob("**/*.json")` (`Cap.Fs.Ext`) |
| Scratch space | `CapTempDir.NewIn(dir)`, `CapTempFile.NewAnonymous(dir)` |
| Know where a handle is, for a log line | `dir.TryGetPath(AmbientAuthority.Acquire(), out string? path)` — see [no-full-name.md](no-full-name.md) |

[migration.md](migration.md) maps each common `File`, `Directory` and `Path` member to its
replacement.

## Turning the ambient APIs off

Once a component takes everything through handles, make it stay that way:

```csharp
[assembly: Cap.Primitives.CapabilityStrict]
```

In an assembly marked strict, `File`, `Directory`, sockets, the system clock and the
operating system's entropy are build errors. It is a compiler rule for code that cooperates,
not a sandbox for code that does not — see [analyzers.md](analyzers.md) and the
[threat model](threat-model.md#51-cap-dotnet-is-not-a-process-sandbox).

## Checking the backend

On Linux the strongest form of the guarantee depends on the kernel. If yours depends on it,
check at start-up:

```csharp
if (Dir.ResolutionBackend != ResolutionBackend.ConfinedOpen)
{
    throw new InvalidOperationException($"Kernel-atomic resolution is unavailable: {Dir.ResolutionBackend}.");
}
```

[backends.md](backends.md#which-backend-is-running) says what each backend guarantees and how
to watch it from outside the process.
