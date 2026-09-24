# cap-dotnet

A capability-based filesystem API for .NET, ported from
[cap-std](https://github.com/bytecodealliance/cap-std).

A `Dir` is an open directory and the authority to reach what is beneath it, and nothing
else. A path handed to it is resolved against the directory handle, never as a string. A
name that would lead outside, such as a symbolic link, a `..`, an absolute path or a Windows
device name, is refused by the resolution itself.

```csharp
using Cap.Primitives;
using Cap.Std;

// The composition root: the one place a directory is opened by an ordinary path.
using Dir uploads = Dir.Open("/srv/app/uploads", AmbientAuthority.Acquire());

// `userPath` is untrusted. It cannot reach outside `uploads` however it is spelled.
byte[] contents = uploads.ReadAllBytes(userPath);
```

## Packages

| Package | What it holds |
|---|---|
| `Cap.Std` | `Dir`, `CapFile`, `CapMetadata`, and the shared types such as `AmbientAuthority`, `CapPath` and `SymlinkPolicy`. It also carries the analyzer that reports ambient filesystem, network, clock and entropy access. |
| `Cap.Fs.Ext` | Atomic writes, recursive walks, tree removal, handle-to-handle copy, pattern matching. |
| `Cap.Net` | Sockets drawn from a pool of permitted endpoints. |
| `Cap.Time` | The system clock as a `TimeProvider`, handed out against an `AmbientAuthority` token. |
| `Cap.Rand` | The operating system's random generator behind a token, and a seeded stream for tests. |
| `Cap.Directories` | Configuration, data, cache and state directories as `Dir` handles. |

Every package depends on `Cap.Std`, so referencing any one of them also brings the analyzer.

## What it is not

It is not a process sandbox. Code holding a `Dir` can still call `File.Open`, P/Invoke, or
start a process. The guarantee covers untrusted *data*, such as a path, an archive entry or an
uploaded file name. Untrusted *code* needs an operating-system sandbox as well. The
[threat model](https://github.com/sjp/cap-dotnet/blob/main/docs/threat-model.md) sets out
exactly what is and is not promised.

Until 1.0, a fix to a containment bug can change behaviour and still ship in a patch
release. See the
[versioning policy](https://github.com/sjp/cap-dotnet/blob/main/docs/releasing.md#versioning).

## Links

- [Getting started](https://github.com/sjp/cap-dotnet/blob/main/docs/getting-started.md)
- [Migrating from System.IO](https://github.com/sjp/cap-dotnet/blob/main/docs/migration.md)
- [The analyzer](https://github.com/sjp/cap-dotnet/blob/main/docs/analyzers.md)
- [Verifying a package's provenance](https://github.com/sjp/cap-dotnet/blob/main/docs/releasing.md#verifying-a-package)
- [Reporting a vulnerability](https://github.com/sjp/cap-dotnet/blob/main/SECURITY.md)
