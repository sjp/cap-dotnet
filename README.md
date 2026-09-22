# cap-dotnet

A capability-based filesystem API for .NET — a native port of
[cap-std](https://github.com/bytecodealliance/cap-std).

A `Dir` handle *is* the authority to reach what is under it. Code holding one can open
`config/app.json`; code holding one cannot open `/etc/passwd`, `../../secrets`, or the
target of a symlink pointing out of the tree, whatever string it is handed.

> **Status: early.** Path parsing and confined resolution are implemented, and a `Dir`
> handle can be opened and directories derived from it; nothing opens a file yet. The
> security posture is in [`docs/threat-model.md`](docs/threat-model.md), and this README is
> a placeholder until a fuller one leads with the problem rather than the API.

## Why

.NET has no capability-based filesystem API, and — the harder part — no primitive to build
one on. There is no `openat`, no `O_PATH`, no `O_NOFOLLOW`; `File.OpenHandle` takes a path
and resolves it with the process's ambient authority
([dotnet/runtime#24655](https://github.com/dotnet/runtime/issues/24655),
[#52908](https://github.com/dotnet/runtime/issues/52908) are still open).

The usual substitute is a containment check on strings:

```csharp
var full = Path.GetFullPath(Path.Combine(root, userPath));
if (!full.StartsWith(root)) throw new UnauthorizedAccessException();
```

This is not a security boundary. `GetFullPath` collapses `..` lexically, so if `a` is a
symlink to `/etc` then `a/../b` checks as inside and resolves as `/b`. The existing .NET
virtual-filesystem libraries say as much themselves — Microsoft's own `PhysicalFileProvider`
documentation declines to make a security claim.

## Repository layout

```
src/Cap.Primitives/    path parsing, interop, resolution algorithms (internal)
src/Cap.Std/           the public API: Dir, CapFile, CapMetadata
src/Cap.Fs.Ext/        atomic writes, walks, handle-to-handle copy
src/Cap.Net/           capability sockets: a Pool of permitted endpoints
src/Cap.Time/          the system clock as a TimeProvider, behind a token
src/Cap.Rand/          OS entropy behind a token, and a seeded stream for tests
src/Cap.Directories/   well-known project directories as Dir handles
src/Cap.Analyzers/     Roslyn analyzers banning ambient System.IO in consuming code
tests/                 unit, adversarial escape corpus, TOCTOU stress
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

## Security

Escapes are vulnerabilities; please report them privately. See
[SECURITY.md](SECURITY.md) and [docs/threat-model.md](docs/threat-model.md), whose §5
("Explicit non-goals") is the part most worth reading first.

[docs/paths.md](docs/paths.md) is the parsing contract — what a path is allowed to be, and
what is deliberately never rewritten on the way through.

[docs/ambient-authority.md](docs/ambient-authority.md) covers the one way authority enters a
process from outside, and how to make a running process list every place it did.

[docs/convenience-layer.md](docs/convenience-layer.md) covers the ergonomic layer built on
top of the core — atomic writes, walks, tree removal, handle-to-handle copy and pattern
matching — including the table of what a copy does with each kind of object it meets.

[docs/network.md](docs/network.md) covers the socket capability, and leads with what it does
not promise: unlike a directory handle, a pool of permitted endpoints is not enforced by the
operating system and is not a containment boundary. Read that section before the API.

[docs/time.md](docs/time.md) covers the clock capability: the system `TimeProvider`, handed
out only against a token, and why that type is used rather than a clock type of this
library's own.

[docs/randomness.md](docs/randomness.md) covers the randomness capability: the operating
system's generator, handed out only against a token, the seeded stream for tests that cannot
be passed where the secure one is required, and the exact algorithm that stream is fixed to.
