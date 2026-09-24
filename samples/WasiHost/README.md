# WASI host

A `wasi_snapshot_preview1` implementation in C#, running guests on
[Wasmtime](https://github.com/bytecodealliance/wasmtime-dotnet), whose filesystem is a set of
`Dir` handles. A WASI host needs exactly what this library provides: a guest is given some
directories and must reach nothing outside them, whatever paths it writes and whatever links
it finds or makes. Wasmtime's own WASI implementation gets that containment from cap-std, in
Rust. Here it comes from `Cap.Std`, in .NET.

```bash
dotnet run --project samples/WasiHost/Host                          # the demonstration
dotnet run --project samples/WasiHost/Host -- run \
    --dir ./data::/ --env GREETING=hello program.wasm arg1 arg2     # a module of your own
```

The demonstration runs a small guest, written in WebAssembly text, that tries to read each of
its arguments. It is handed a directory as `/` and a list of paths, some inside and some
reaching out of it by every route that gets past a string check:

```
The guest was given /tmp/cap-wasi-host-32OsyR/guest as '/', and tried to read:
  notes.txt: read "the guest's own notes"
  docs/readme.txt: read "a document the guest was given"
  ../secret.txt: refused, ENOTCAPABLE
  docs/../../secret.txt: refused, ENOTCAPABLE
  /tmp/cap-wasi-host-32OsyR/secret.txt: refused, ENOTCAPABLE
  shortcut: refused, ENOTCAPABLE
  absolute: refused, ENOTCAPABLE
```

`shortcut` and `absolute` are symbolic links inside the guest's directory, pointing at the
file beside it. The program exits non-zero if the guest read anything outside.

## Layout

- `Preview1` is the adapter: a library whose only authority is what the host hands it — the
  preopened `Dir`s, a `TimeProvider` and an `IRandomSource`. It is built with
  `[assembly: CapabilityStrict]`, so reaching the filesystem, the clock or entropy any other
  way is a build error.
- `Host` is the program. It opens the preopened directories and takes the clock and entropy
  source, the one place authority enters.

## How WASI maps onto `Dir`

A preopened directory is a `Dir`, with nothing added. Every `path_*` call takes a directory
descriptor and a relative path, and so does every method on `Dir`, so each is one call with the
guest's path passed through as it arrived:

| WASI | `Dir` |
|---|---|
| a preopen, or a directory descriptor | `Dir` |
| a file descriptor | `CapFile`, plus the position `fd_read` and `fd_write` move |
| `path_open` | `OpenFile`, or `OpenDir` for `O_DIRECTORY`; `FDFLAGS_APPEND` is `append: true` |
| `fd_fdstat_set_flags` with `FDFLAGS_APPEND` | `CapFile.IsAppending` |
| `path_create_directory`, `path_remove_directory`, `path_unlink_file` | `CreateDir`, `DeleteDir`, `DeleteFile` |
| `path_rename`, `path_link` | `Rename(..., replaceExisting: true)`, `CreateHardLink` |
| `path_symlink`, `path_readlink` | `CreateSymlink` or `CreateDirSymlink`, `ReadLink` |
| `path_filestat_get` | `GetMetadata(path)` |
| `path_filestat_set_times`, `fd_filestat_set_times` | `SetTimes(path, ...)`, and `SetTimes` on the `CapFile` or `Dir` |
| `fd_readdir` | `EnumerateEntries` |
| `ENOTCAPABLE` | `SandboxEscapeException` |
| every other error code | `CapIOException.KindOf(exception)` |

The adapter never builds a host path and never resolves a guest path itself. It looks inside a
path in two cases only. A path made of nothing but `.` components names the directory it is
resolved against, which the library refuses as naming nothing beneath the handle, so that
path is answered from the descriptor's own `Dir`. And `path_symlink` joins the link's
directory to its target to see what kind of link to make; see below.

`path_symlink` with a rooted target, such as `/`, answers `ENOTCAPABLE`: `Dir` refuses to
store one, as WASI hosts do.

`fd_filestat_set_times` on a file needs a descriptor opened for writing. `CapFile.SetTimes`
refuses a handle that can only read, so a file opened only to read is not given that right,
and the call answers `ENOTCAPABLE`.

## Tests

`tests/WasiHost.Tests` checks the adapter in two ways.

**The escape corpus, made by a guest.** Every case in the corpus that the library is tested
against directly is also made through WASI calls, with the path as bytes in guest memory, on
every backend and under both symbolic-link policies. Each must come to the same outcome, and
nothing outside the sandbox may change or be seen. The call is made by a small guest that
re-exports each import, so it crosses the engine exactly as a real guest's would. Two
differences in outcome are expected and asserted: a path of only `.` opens the directory
itself, and WASI's rename replaces a name that is taken where the library call the corpus
makes refuses. Three more are differences only in the code: `link` reports a directory given
a second name as `EPERM`, `readlink` reports a name that holds no link as `EINVAL`, the code a
malformed path also gets, and creating, removing, renaming or linking at a path ending in `..`
is answered `EINVAL`, since such a path leaves no name to act on. POSIX answers those last
requests with a different code per call.

**The WebAssembly WASI test suite.** Its preview 1 programs that are given a directory — 49
of them, written in Rust and C — are run against the adapter. Fetch the suite
first; its compiled programs come to about a hundred megabytes, so they are not kept here:

```bash
export CAPDOTNET_WASI_TESTSUITE=$(build/ci/fetch-wasi-testsuite.sh /tmp)
dotnet test --project tests/WasiHost.Tests
```

All 49 pass. A program that fails because the library does not offer something WASI needs
is listed as a known gap in the test, which asserts that it still fails, so a gap that
closes is noticed. None is listed now.

## What the library does not yet offer

Running a real WASI host over `Dir` is a review of the API by a demanding caller. The gaps it
found are listed here. The adapter reports each as an error rather than working around it,
except where two library calls together can do what is asked without reaching anything
either could not reach alone. Those cases are listed too, with what they cost.

**Composed from two calls:**

- **Whether to follow a final link is decided per handle, not per call.** WASI asks per
  lookup. The adapter serves a no-follow lookup through the descriptor's
  `Restrict(SymlinkPolicy.Deny)` view, which also refuses links before the last component.
  WASI would follow those. A directory opened that way would keep the stricter policy
  forever, and so would everything opened beneath it. So a no-follow directory open is made
  twice: once through the strict view, and once through the ordinary handle, which is kept if
  it is the same directory. Describing what a final link leads to is done by opening the
  target, which needs permission to open it. Setting the times of what a final link leads to
  is done the same way: the target is opened for writing, or failing that as a directory, and
  set through the handle. That needs permission to write the file. A hard link to what a final link leads to is not
  possible at all. An open that creates or truncates never follows a final link, whatever the
  lookup asks: `Dir` refuses a link at the name for every mode but `FileMode.Open`, so the
  adapter reports that refusal where WASI would create or empty the file the link leads to.
- **There is no open for "whatever the name holds".** A WASI open may name a file or a
  directory without saying which. The adapter tries a file open, then a directory open: a
  second resolution, and a window in which a rename can change which object is described.
- **Metadata has no link count or status-change time**, and a directory entry has no
  identity. `filestat` reports both as zero, and `fd_readdir` describes every entry separately
  to learn its inode.
- **A link's kind has to be guessed.** Windows records whether a link names a file or a
  directory, and won't traverse one made as the wrong kind. `Dir` asks the caller to choose
  between `CreateSymlink` and `CreateDirSymlink`. WASI doesn't say, so the adapter opens the
  target as a directory from where the link will sit, beneath the same descriptor, and makes a
  directory link if that works and a file link otherwise. A target made or replaced later
  may be of the other kind. On other platforms the two kinds are the same link.
- **A directory's own entries cannot be committed.** `fd_sync` on a directory answers
  `ENOTSUP`.

## What this is not

The adapter bounds what a guest can reach on the filesystem. The WebAssembly engine is what
stops a guest from reaching the host's memory. The adapter checks every guest pointer against
the guest's memory before using it, but the isolation of guest code is Wasmtime's.

The testsuite expectations were recorded on Linux, and CI runs the suite there. The escape
corpus through WASI runs on every platform.
