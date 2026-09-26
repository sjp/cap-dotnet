# cap-dotnet

A capability-based filesystem API for .NET — a native port of
[cap-std](https://github.com/bytecodealliance/cap-std).

## The check you have already written

Every .NET codebase that serves files, extracts archives or stores per-tenant data has a
line like this somewhere:

```csharp
string full = Path.GetFullPath(Path.Combine(root, userPath));
if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
{
    throw new UnauthorizedAccessException();
}
string contents = File.ReadAllText(full);   // passes the check, reads outside
```

It is the careful version — ordinal comparison, a trailing separator so that `/srv/data`
does not also admit `/srv/data-evil` — and it is not a security boundary. Suppose `userPath`
is `reports/secret.txt`, and `reports` is a symbolic link to a directory outside `root`.
Anyone who can write into `root` can leave one there: an upload, an extracted archive, an
earlier bug. `GetFullPath` works on the string alone, so it sees nothing but
`root/reports/secret.txt`; the check passes; and the operating system follows the link when
the file is opened. `..` has the same problem from the other side: `GetFullPath` collapses
`a/..` as text, which is only the right answer if `a` is not a link — and nothing asked.

This is not a mistake in one codebase. It is `PhysicalFileProvider`'s own containment check,
almost to the character.

The same read through a `Dir`:

```csharp
using Dir dir = Dir.Open(root, AmbientAuthority.Acquire());
string contents = dir.ReadAllText(userPath);   // throws SandboxEscapeException
```

There is no check to get right. The path is resolved against the directory handle, one name
at a time or in a single confined kernel call, and a name that would lead outside — a link,
a `..` that climbs above the directory, an absolute path, a Windows device name — is refused
by the resolution itself. A `..` that stays inside is walked, a step at a time, never
collapsed as text.

Both snippets are taken verbatim from [`samples/ContainmentCheck`](samples/ContainmentCheck),
which builds the link, runs them, and fails if either half does not behave as described
here. CI runs it on Linux, Windows and macOS.

```bash
dotnet run --project samples/ContainmentCheck
```

## What is guaranteed

> Given a `Dir` opened on directory *D*, no operation reachable from that `Dir` — and no
> `Dir`, `CapFile` or handle transitively derived from it — can observe or modify a
> filesystem object that is not, at the moment of the operation, reachable by descending
> from *D* without traversing a parent link or an out-of-tree symlink target.

In plain terms: holding a `Dir` lets you reach what is under it and nothing else, whatever
string you are handed, and everything you get from it is bound the same way.

**And what is not:**

- **It is not a process sandbox.** Code holding a `Dir` can still call `File.Open`,
  P/Invoke, or start a process. The guarantee is a property of this API, for untrusted
  *data* — a path, an archive entry, an uploaded file name. Untrusted *code* needs an
  operating-system sandbox as well.
- **It does not stand up to a privileged attacker**: one who can replace the root's own
  parent directories, mount filesystems, or holds a privilege that bypasses file
  permissions.
- **There are no resource limits.** A caller with a `Dir` can fill the disk.
- **It does not hide what the handle already reveals** about the root itself: its
  timestamps, its identity, the fact that it exists.
- **It is not atomic across operations.** Removing a tree is many calls; an enumeration is
  not a snapshot.
- **On some hosts, containment is not atomic within one operation either.** Where the kernel
  cannot resolve a path in one confined call, someone who can write *inside* the tree can,
  with the right timing, steer an operation to a different object that is also inside it.
  Never outside. [Which backend am I on?](docs/backends.md#which-backend-is-running)

[docs/threat-model.md](docs/threat-model.md) states each of these precisely, lists the
attacks that are defended, and names the test that covers each one.

## How it compares

| | Decides containment by | A link inside the root pointing out | Security boundary? |
|---|---|---|---|
| **`Cap.Std.Dir`** | resolving each name beneath an open directory handle | refused | **yes**, for untrusted paths; see above |
| `Microsoft.Extensions.FileProviders.PhysicalFileProvider` | `Path.GetFullPath` and a prefix comparison on the resulting string | followed | **no** — its documentation makes no security claim, and its check is the string check above |
| `Zio.FileSystems.SubFileSystem` | joining the path onto a sub-path and testing the result as text | followed | **no** — it is a view for composing file systems, and nothing in it examines links |
| `System.IO.Abstractions` | nothing: it is an injectable wrapper over `System.IO` | followed | **no** — it exists to make file access testable, and does not claim to confine it. `Cap.IO.Abstractions` implements its `IFileSystem` over a `Dir`, which gives code written against it both ([docs](docs/io-abstractions.md)) |

[`samples/StaticFileServer`](samples/StaticFileServer) serves one content root both ways —
through a `Dir`, and through `UseStaticFiles` over `PhysicalFileProvider` — and shows the
second handing out a file from outside it through a link.

The other three are good at what they are for. None of them is for this, and treating one as
if it were is how the check at the top of this page ends up in production.

## Getting started

Authority enters a program once, at the top, and is passed down as a handle:

```csharp
using Cap.Primitives;
using Cap.Std;

// The composition root: the one place a directory is opened by an ordinary path.
using Dir uploads = Dir.Open("/srv/app/uploads", AmbientAuthority.Acquire());

var store = new AvatarStore(uploads.OpenOrCreateDir("avatars"));
```

```csharp
sealed class AvatarStore(Dir avatars)
{
    // `userId` is untrusted. It cannot reach outside `avatars` however it is spelled.
    public byte[] Load(string userId) => avatars.ReadAllBytes($"{userId}.png");
}
```

`AvatarStore` cannot reach anything outside `avatars`, and nothing in it needs to be
reviewed to know that — it was never given anything else.
[docs/getting-started.md](docs/getting-started.md) walks through it properly.

## Documentation

**Using it**

| | |
|---|---|
| [Getting started](docs/getting-started.md) | Acquiring authority once, passing `Dir` down, and the handful of types you will use. |
| [Migrating from `System.IO`](docs/migration.md) | Every commonly used `File.*`, `Directory.*` and `Path.*` member, and what replaces it. |
| [Why there is no `Dir.FullName`](docs/no-full-name.md) | The question everyone asks first. |
| [Platform differences](docs/platforms.md) | What differs on Windows, macOS and Linux, and why. |
| [Resolution backends](docs/backends.md) | Which one you are on, how to find out at run time, and what each guarantees. |
| [The convenience layer](docs/convenience-layer.md) | Atomic writes, walks, tree removal, handle-to-handle copy, patterns. |
| [Ambient authority](docs/ambient-authority.md) | The one way authority enters, and how to list every place it did. |
| [The analyzer](docs/analyzers.md) | The build-time rules shipped in `Cap.Std`, and the one line that makes ambient `System.IO` an error. |
| [NativeAOT and trimming](docs/aot.md) | What an application can rely on, and the size of a native binary. |
| [Testing](docs/testing.md) | Three ways to test code that takes a `Dir`: an in-memory filesystem that hands out real handles, a stubbed `IDir`, and a scratch directory on disk. |
| [`IFileSystem` over a `Dir`](docs/io-abstractions.md) | Confining code written against System.IO.Abstractions without changing it, every difference from `System.IO` and `MockFileSystem`, and moving such code onto `Dir` member by member. |

**Other capabilities**

| | |
|---|---|
| [Sockets](docs/network.md) | A pool of permitted endpoints — and, first, why it is not a boundary. |
| [The clock](docs/time.md) | The system `TimeProvider`, handed out against a token. |
| [Randomness](docs/randomness.md) | The OS generator behind a token, and a seeded stream for tests. |
| [Project directories](docs/directories.md) | Configuration, data, cache and state directories as `Dir` handles. |

**Security and internals**

| | |
|---|---|
| [Threat model](docs/threat-model.md) | The guarantee, the attacks in scope, the non-goals, the residual risk. |
| [Path parsing](docs/paths.md) | What a path is allowed to be, and what is never rewritten on the way through. |
| [Fuzzing](docs/fuzzing.md) | The fuzz targets and property tests, and what to do with a finding. |
| [Benchmarks](docs/benchmarks.md) | Each operation against its `System.IO` equivalent, per backend. |
| [Releasing](docs/releasing.md) | The packages, the versioning policy, and how to verify a package's provenance. |

**Samples** — see [samples/README.md](samples/README.md): the demonstration above, a
static file server, a plugin host that gives each plugin its own directory, a zip extractor
that cannot be made to write outside its destination, an audit of where a process took
authority, a WebAssembly host whose guests' preopened directories are `Dir` handles, and a
component tested three ways beside an `IFileSystem` component confined by its composition
root.

## Repository layout

```
src/Cap.Primitives/    path parsing, interop, resolution algorithms (ships inside Cap.Std)
src/Cap.Std/           the public API: Dir, CapFile, CapMetadata
src/Cap.Fs.Ext/        atomic writes, walks, handle-to-handle copy
src/Cap.Net/           capability sockets: a Pool of permitted endpoints
src/Cap.Time/          the system clock as a TimeProvider, behind a token
src/Cap.Rand/          OS entropy behind a token, and a seeded stream for tests
src/Cap.Directories/   well-known project directories as Dir handles
src/Cap.Std.Testing/   an in-memory filesystem that hands out real Dir handles, for tests
src/Cap.IO.Abstractions/  System.IO.Abstractions' IFileSystem, confined to a Dir
src/Cap.Analyzers/     Roslyn analyzer shipped in Cap.Std: ambient IO, clock and entropy
samples/               runnable programs, each built and run in CI
tests/                 unit, adversarial escape corpus, TOCTOU stress, property tests
fuzz/                  libFuzzer harness and the inputs it has saved
bench/                 BenchmarkDotNet, each operation against its System.IO baseline
```

## Building

```bash
dotnet build CapDotnet.slnx
dotnet test  CapDotnet.slnx
```

Requires the .NET 10 SDK (see `global.json`). Tests run on Microsoft.Testing.Platform and
**must not be run with the power to bypass file permissions** — every test assembly asserts
this at startup, because negative containment tests can pass for the wrong reason when the
process outranks the permission system. On Unix that means not running as root. On Windows
an administrator token is fine, and in fact necessary to create the symlinks the suite
attacks; what must not be enabled is a backup, restore or take-ownership privilege.

```bash
dotnet run -c Release --project bench/Cap.Benchmarks -- --filter '*'
```

runs the benchmarks: each operation against its `System.IO` equivalent, once per resolution
backend the machine has.

## Security

Escapes are vulnerabilities; please report them privately. See [SECURITY.md](SECURITY.md).
So is a host quietly running a weaker resolution backend than it reports.

## Licence

MIT; see [LICENSE](LICENSE). [NOTICE](NOTICE) covers third-party material, of which there is
currently none.
