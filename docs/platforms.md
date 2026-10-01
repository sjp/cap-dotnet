# Platform differences

The containment guarantee is the same everywhere: a `Dir` reaches what is beneath it and
nothing else. What differs is the filesystem underneath — which names it accepts, which names
it considers equal, and how the kernel lets a path be resolved beneath a handle. This page is
what a program moving between Linux, macOS and Windows will notice.

## At a glance

| | Linux | macOS | Windows |
|---|---|---|---|
| Resolution backend | `openat2` confined open where available, otherwise a walk ([below](#linux-which-backend)) | walk | walk through the native API, each step relative to the last handle |
| TOCTOU within one operation | none on `openat2`; on the walk, steering to another object *inside* the tree | steering inside the tree | steering inside the tree |
| Path separators | `/` | `/` | `/` and `\` |
| Case | sensitive (unless the volume folds) | insensitive on the default APFS volume | insensitive |
| Unicode normalisation | none: names are bytes | the filesystem may return a name in a different normal form | none |
| Names refused beyond `..`, absolute paths and `NUL` | none | none | device names, trailing dots and spaces, `:`, `* ? < > " \|`, control characters |
| Alternate data streams | — | — | refused |
| 8.3 short names | — | — | refused as aliases |
| Creating symbolic links | anyone | anyone | administrators, or accounts with Developer Mode on |
| Anonymous temporary files | `O_TMPFILE` where the filesystem supports it | falls back to a named file | falls back to a named file |
| Unix-domain sockets through a `Dir` | yes | no | no |
| Permissions reported as | Unix mode | Unix mode | file attributes |
| Creation time | where the filesystem records it | yes | yes |
| Appending (`append: true`, `CapFile.IsAppending`) | a flag on the open file, shared with streams taken from it | same | applied by `CapFile` to its own writes; a stream taken while it is on gets a handle that can only append |
| Committing a directory's entries (`Dir.Flush(toDisk: true)`) | `fsync` on the directory | `F_FULLFSYNC` on the directory, falling back to `fsync` where the volume refuses it | not possible; returns false |
| Earliest time `SetTimes` can store | any a `DateTimeOffset` holds (the filesystem may clamp it) | same | after 1 January 1601; earlier is `ArgumentOutOfRangeException` |
| Processor architectures | x86-64, AArch64, 32-bit ARM; any other is refused ([below](#linux-architectures)) | any .NET runs on | any .NET runs on |

## Windows

**Minimum version: whatever .NET 10 runs on**, which is Windows 10 version 1607 and Windows
Server 2016. Where the backend asks for something newer than that, it falls back to the older
form when a system or filesystem does not offer it:

| Newer form | Falls back to |
|---|---|
| `NtQueryDirectoryFileEx` (an ntdll export from Windows 10 1709) | `NtQueryDirectoryFile`, chosen once per process |
| `FileIdExtdDirectoryInformation` (128-bit identifiers in a directory listing) | `FileIdFullDirectoryInformation` |
| `FileDispositionInformationEx` (a removed name disappears at once, as on Unix) | `FileDispositionInformation`: the name lingers until the last handle closes |
| `FileRenameInformationEx` (replacing a name that is open, as on Unix) | `FileRenameInformation` |
| `FileNormalizedNameInformation` (the stored name, for the check on names containing `~`) | `FileAlternateNameInformation`: the entry's short name (see below) |

One class has no fallback yet: `FileIdInformation`, which every metadata call reads. On a
filesystem that does not offer it, the calls that read it fail. No hosted CI runner is older than 1709, so the older
directory query is exercised by forcing it on the Windows leg rather than on an old system.

**Reserved device names are refused, in every disguise.** `CON`, `PRN`, `AUX`, `NUL`,
`COM0`–`COM9`, `LPT0`–`LPT9`, their superscript-digit forms, `CONIN$` and `CONOUT$` all reach
a device rather than a file, and so do `CON.txt`, `con`, `CON ` and `CON.tar.gz`. A path
containing one throws `SandboxEscapeException`. Because the set has grown before, every
handle the Windows backend opens is also asked what it is, and anything but a file or
directory on a filesystem is closed before it reaches the caller: a device name nobody
anticipated still gets nowhere.

**Names ending in a dot or a space are refused.** Windows strips them below the API, so
`report.txt.` would be checked as one name and opened as another. The volume itself can
still hold such a name, made through the `\\?\` prefix that skips the stripping, and an
enumeration lists it as it is stored; handing that name back to open, remove or rename it is
refused like any other name ending in a dot or a space, so it has to be dealt with outside
the library.

**`:` is refused.** It introduces an alternate data stream — `file.txt:hidden` is a second
body of the same file — and `CON::$DATA` is a device reached through one.

**Short names are refused as aliases.** `PROGRA~1` names the same directory as
`Program Files`. A component containing `~` is opened, then asked its real name; if the two
differ the handle is dropped. A file whose actual name contains a tilde is allowed.

Many network redirectors (older Samba among them) and user-mode filesystems (WinFsp, Dokan)
cannot say what an object's real name is. There the entry is asked for its short name instead.
If it has none, or the filesystem can't answer that either, the name is accepted: a filesystem
that doesn't generate short names has no alias to refuse. If the short name is some other
spelling, the name is accepted. If the short name is the very name that was asked for, the
name is refused. That includes a file whose real name is already an eight-plus-three name
with a tilde, like `PLAIN~1`, since the short name alone cannot tell the two apart. Names such
as `~$report.docx` or `notes.txt~` are never eight-plus-three, so they are always accepted.

**Case is not a boundary, and containment never relies on it.** `Secret` and `SECRET` are
one file, and every open this library issues matches case-insensitively, as the rest of
Windows does. Every containment decision is taken from an open handle rather than by
comparing strings, so changing the case of a refused name gets nowhere. The exception is a
directory made case-sensitive (`fsutil file setCaseSensitiveInfo <dir> enable`, which needs no
privilege from Windows 10 version 1803): beneath it the filesystem matches names exactly
whatever the open asks for, so `a` and `A` are two files there and `a` does not open `A`, as
on Linux. Containment is unaffected, for the same reason as above.

**Symbolic links and junctions.** A link to a directory and a link to a file are different
kinds on Windows, and Windows itself will not traverse a link as the wrong kind. Resolution
here does not rely on that: when an open is refused because the name is a link of the other
kind, the link is reported as the link it is and its target is read and judged like any
other, so one leading out of the subtree is refused as an escape rather than as the wrong
kind of object, and one staying inside is followed as it would be on Linux or macOS. Other
programs on the machine still go by the kind, so use `CreateDirSymlink` for directories.
Creating either needs the privilege Windows grants to administrators and to
Developer Mode. Junctions always store an absolute target, so they are always refused on
the way through, whatever the symbolic-link policy says — but `ReadLink` still reports what one
holds, since reading a link is not following it, and what comes back is the target the
filesystem acts on, which for a rooted one is spelled the way the filesystem stores it
(`\??\C:\...`) rather than the way it is displayed. A directory-kind link and a junction are
directory entries here, so `CreateHardLink` cannot give one a second name, as it can on Unix.

**Other reparse points are split by whether they redirect.** A reparse point whose tag stands
for another object — a container link, a distributed file system link, or any tag with the
name-surrogate bit set — throws `SandboxEscapeException`. One whose tag only names the filter
serving the entry works like any other file or directory: files compressed with
`compact /exe` or by CompactOS, files on a deduplicated volume, OneDrive and other
Files-On-Demand placeholders (folders included), and ProjFS trees such as a VFS for Git checkout
can be opened, read, listed, walked and copied, and `GetMetadata` reports them as `File` or
`Directory`. Opening one costs a second open and an identity check (see
[backends.md](backends.md#what-the-windows-walk-does-differently)), and reading a placeholder
lets its provider download the contents, as it would for any other program. `ReadLink` on one
throws with `CapErrorKind.NotALink`. A tag nothing on the machine serves — an application
execution alias in `WindowsApps`, or a placeholder whose sync client has been uninstalled —
cannot be opened, and fails with `CapErrorKind.NotSupported` rather than as an escape.

**Opening a name without saying which kind takes two opens.** A directory and a file are
opened with different options, so `OpenAny` opens the name once with the right to ask what it
is and nothing more, then opens that object again through the handle it got, with an empty
name, as a directory or with the file's sharing and options. The second open names nothing, so
it reaches the same object and a rename in between can't change the answer. The extra call is
the only difference from other platforms, where one open serves both kinds.

**Setting a time to "now" reads the system clock.** The Windows call that sets a file's times
has no value meaning "the moment this is recorded", as the Unix calls do, so for
`CapFileTime.Now` the backend reads the system time and passes it in. It is the time a write
would have been stamped with.

**Paths.** `/` and `\` both separate components. Drive-relative (`C:file`), root-relative
(`\file`), UNC (`\\server\share`) and device-namespace (`\\?\`, `\\.\`) paths are refused;
the first two look relative but resolve against process-wide state.

## macOS

**Unicode normalisation.** APFS and HFS+ may store or return a name in a different Unicode
normal form from the one it was created with, so `café` written with a composed `é` may come
back decomposed from an enumeration. This library never normalises a name itself: a name is
passed to the kernel exactly as given, and containment is never decided by comparing names,
so a check cannot be passed in one form and the file opened in the other. A program that
compares names from an enumeration with names it holds should normalise both itself.

**Case.** The default volume is case-insensitive, with the same consequence as on Windows.

**`/tmp` and `/var` are links** to `/private/tmp` and `/private/var`. Opening a root through
them is fine: the root is opened with ambient authority, following links, and confinement
applies to what lies beneath wherever it led. `TryGetPath` will report the `/private` form.

**Backend.** Always the walk. macOS has no confined open like Linux's, so the residual
window described in [threat model §6.1](threat-model.md#61-the-fallback-resolver-narrows-toctou-it-does-not-close-it)
applies.

**Unix-domain sockets** cannot be named relative to a directory handle on macOS, so the
socket types that bind or connect through a `Dir` report themselves unsupported there rather
than resolve a path. See [network.md](network.md#unix-domain-sockets-are-a-dir-capability).

## Linux: which backend

Linux 5.6 added `openat2` with `RESOLVE_BENEATH`, which resolves a whole path in one kernel
operation that cannot leave the directory. Where it is available, that is what runs, and the
guarantee holds even against someone rearranging the tree during the call.

It is not always available:

- the kernel is older than 5.6;
- a seccomp filter denies it — some container runtimes' default profiles did for some time
  after it was added;
- it has been turned off with the `Cap.Primitives.DisableOpenat2` switch or
  `CAPDOTNET_DISABLE_OPENAT2=1`.

In any of those cases the walk runs instead. It keeps containment but has the within-tree
window macOS and Windows have. The choice is made once per process. Read it with
`Dir.ResolutionBackend`, or watch the `Cap.Primitives` meter; see
[backends.md](backends.md#which-backend-is-running).

**Names are bytes.** A Linux filename may be any byte sequence without `/` or `NUL`, and need
not be valid UTF-8. A name that is not is carried through .NET strings with each undecodable
byte escaped to a lone surrogate, so it can be listed and reopened unchanged; see
[paths.md](paths.md#characters-and-encoding). Nothing a Windows filesystem would refuse is
refused here: `CON` and `name.` are ordinary files.

**Case** is significant, unless the volume folds it (vfat, or ext4 with casefolding enabled),
in which case the Windows remarks apply.

**Rename without replacing** uses `renameat2` with `RENAME_NOREPLACE`, so the refusal of a
taken destination is part of the move itself. A kernel without the call, or a filesystem that
reports the flag unsupported, fails the move with `CapErrorKind.NotSupported`. Some
filesystems (older NFS, several FUSE filesystems) answer `EINVAL` instead, and that comes
through as `CapErrorKind.InvalidArgument`. That is also the code for a request that is itself
wrong, such as moving a directory beneath itself, and telling the two apart would take a
lookup before the move: that is the race the flag exists to close. On such a filesystem, ask
for `replaceExisting: true` if replacing is acceptable. The library never falls back to
checking first.

### Linux architectures

The Linux backend supports the architectures .NET itself supports on Linux: **x86-64,
AArch64 and 32-bit ARM**, under glibc or musl. On any other — riscv64, loongarch64, ppc64le,
s390x, 32-bit x86, all of which have community builds of the runtime — constructing the
backend throws `PlatformNotSupportedException`, so the first `Dir` opened fails rather than
running.

The refusal is deliberate. Several open flags and syscall numbers differ by architecture,
and the one the containment guarantee rests on, "do not follow a symbolic link", is a
different flag on another architecture (x86-64's value is "large file" on ARM). Running with
another architecture's values would not fail loudly; it would follow links. Each supported
table is written out in `src/Cap.Primitives/Interop/Unix/LinuxConstants.cs` and asserted
literally by tests that run on every CI leg; adding an architecture means adding a table
there.

On 32-bit ARM, file offsets, timestamps and filesystem sizes go to the kernel through its
64-bit calls by number rather than through the C library's 32-bit defaults, so files past
2 GiB and times past 2038 work as they do elsewhere. Setting a file's times there needs
Linux 5.1 or later (`utimensat_time64`). That leg runs nightly under emulation rather than
on every change.

## Everywhere

- Paths must be relative. `..` is walked beneath the handle, never collapsed as text, and
  refused where it would climb above it.
- A path is limited to 32767 characters and a component to 255, as a bound on work, not a
  promise the filesystem will accept that much.
- The first directory handle is opened with the process's own authority and follows links;
  everything after is confined.
