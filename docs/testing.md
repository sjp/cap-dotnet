# Testing code that takes a `Dir`

A component that takes a `Dir` or an `IDir` can be tested in three ways.

| | Use it for | What runs |
|---|---|---|
| [An in-memory `Dir`](#an-in-memory-dir) from `Cap.Std.Testing` | Almost everything. It is the default. | This library's path parsing, resolution and link policy, over a tree held in memory. Containment is tested for real, and failures can be injected. |
| [A stubbed `IDir`](#a-stubbed-idir) from a mocking library | Checking which calls the component makes, and how it handles a failure from one of them. | Nothing but the stub. No name is resolved. |
| [A `CapTempDir` on disk](#a-scratch-directory-on-disk) | What only a real filesystem has: the host's resolution backend, flushing to storage, raw handles, files other programs write. | Everything, on the platform the test runs on. |

The examples below come from [`samples/TestableComponent`](../samples/TestableComponent),
whose tests use all three. `ReportStore` there takes an `IDir` and keeps one JSON file per day
in it: `Save` replaces a day's report with an atomic write, `Load` reads one, `ListDays`
enumerates the directory, and `Prune` removes reports older than a given day.

## An in-memory `Dir`

The `Cap.Std.Testing` package holds a filesystem in memory that hands out real `Dir` handles.
It needs `using Cap.Std.Testing;`.

```csharp
var fs = new InMemoryFileSystem();
fs.AddFile("reports/notes.txt", "not a report");

using Dir reports = fs.OpenRoot("reports");
var store = new ReportStore(reports);

store.Save(new DateOnly(2026, 9, 1), """{ "total": 3 }""");

Assert.Equal("""{ "total": 3 }""", fs.ReadAllText("reports/2026-09-01.json"));
Assert.Equal([new DateOnly(2026, 9, 1)], store.ListDays());
```

Testing against the disk works too, but it is slow, it behaves differently on each platform,
and it cannot produce on demand the awkward states a unit test often wants: a full disk, a
permission failure, a name that refuses to be removed. The in-memory filesystem can.

Reference it from test projects only. It lives in its own package so that production code
cannot pick up an in-memory backend without depending on a test package. A `Dir` backed by
memory in production would fail silently: every read and write works, and everything written
is lost when the process exits. So the package warns (`CAPTESTING001`) at build time in any
project that installs it, directly or through another package, and is not a test project.
A project counts as one when it sets `IsTestProject`, as `Microsoft.NET.Test.Sdk` does, or
`IsTestingPlatformApplication`, as a Microsoft.Testing.Platform test project does. A library
of shared test helpers is neither, and opts out:

```xml
<PropertyGroup>
  <CapAllowStdTestingOutsideTests>true</CapAllowStdTestingOutsideTests>
</PropertyGroup>
```

The warning comes from MSBuild rather than the compiler, so `TreatWarningsAsErrors` leaves it
a warning. List it in `WarningsAsErrors` to make it an error.

### What is real and what is simulated

A handle from `OpenRoot` is the same `Dir` type that `Dir.Open` returns, and every call through
it runs this library's own path parsing, resolution, symbolic-link policy, and translation of
failures into exceptions. Only the bottom layer is simulated: the step that turns "open this
one name beneath this directory" into a system call. That is the difference from a mock of
`System.IO`, which reimplements the whole surface and slowly drifts from the real behaviour.
Here code under test that climbs out of its directory, or follows a link that leaves it, is
refused with the same `SandboxEscapeException`, for the same reason, as it would be on disk.

The filesystem models files, directories and symbolic links; hard links and link counts; a
stable `CapFileId` for each object; access, write, change and creation times; Unix mode bits or
Windows attributes; renames with and without replacing the destination; the refusal to remove
a directory that is not empty; and files that stay readable through an open handle after their
name is removed. Where two answers are possible, it gives the one Linux gives.

It is held to the disk's behaviour by the library's own tests. The suites for `Cap.Std`,
`Cap.Fs.Ext` and the escape corpus, written against the disk, also run in continuous
integration with this filesystem standing in for it, under Linux and Windows path rules, and
resolving both ways. A place where it answers differently from the disk fails one of those
tests.

Permissions are recorded and reported but not enforced. The exception is the Windows
read-only attribute, which, when the filesystem follows Windows rules, refuses removal of the
name and refuses opening the file for writing, as it does on Windows.

Turning appending on for an open file sends every later write through it, and through its
copies and any stream taken from it, to the end, as Linux's append flag does. Under Windows
rules appending applies only to the file's own writes, and `Dir.Flush(toDisk: true)` returns
false, as they do on Windows.

Reading never changes a file's access time, as on a filesystem mounted with `noatime`.
Creating something stamps all four of its times. A write stamps the write and change times, and
any other change stamps the change time. Adding, removing or renaming an entry stamps the
directory that holds it.

### Building and inspecting a tree

`AddFile`, `AddDirectory`, `AddSymbolicLink` and `AddHardLink` build the tree before the test
runs. `SetTimes`, `SetUnixMode` and `SetAttributes` adjust it. `Exists`, `ReadAllBytes`,
`ReadAllText`, `GetEntries` and `GetSymbolicLinkTarget` inspect it afterwards without going
through a handle, so an assertion does not depend on the code it checks.

These paths are scaffolding, not input to the code under test. `/` separates components under
either path syntax. Missing directories are created on the way. A symbolic link on the way is
not followed. `.` and `..` are refused. Each name is still checked against the filesystem's
path rules, so a tree cannot hold a name its own handles would refuse. `AddSymbolicLink` does
accept a rooted target, so that a test can plant the link an attacker would.

`OpenRoot()` opens the top of the tree. `OpenRoot("tenants/a")` opens a directory inside it,
and the handle reaches nothing above that directory. Any number of roots may be open at once.
Opening one needs no `AmbientAuthority` token, because the filesystem is not the host's and a
handle on it grants nothing outside it. Those roots therefore never appear in the
[ambient authority log](ambient-authority.md).

### Options

| Option | Default | Meaning |
|---|---|---|
| `PathSyntax` | The running platform's | `CapPathSyntax.Windows` makes the handles read paths as Windows does, on any machine: `\` separates components, and reserved and rooted names are refused. It also switches the recorded permissions from Unix mode bits to Windows attributes. |
| `CaseSensitive` | Sensitive under Unix syntax, not under Windows syntax | When false, a name is found under any spelling, keeps the spelling it was created with, cannot be created a second time in another case, and can be renamed to change only its case. |
| `TimeProvider` | A clock stopped at 2000-01-01T00:00:00Z | Where timestamps come from. Pass a `FakeTimeProvider` to let time pass under the test's control. |
| `Resolution` | `ResolutionBackend.PortableWalk` | `ConfinedOpen` resolves a whole path in one call, as the Linux kernel does. The two reach the same answers, so the same test can run down both. |

Every handle reports `ResolutionBackend.InMemory` as its `Backend`. Its handles are not
operating-system objects, so `UnsafeGetHandle` throws `NotSupportedException`, and `AsStream`
returns a stream of the filesystem's own rather than a `FileStream`.

### Faults

| Member | Effect |
|---|---|
| `SetUnreadable(path)` | Every operation on the object, and every lookup inside it if it is a directory, fails with `UnauthorizedAccessException`. Its name can still be described, renamed and removed. |
| `SetUndeletable(path)` | Removing or replacing the name fails with `UnauthorizedAccessException`, and clearing the read-only attribute does not help. |
| `FailNextWrites(count, kind)` | The next `count` writes, appends or length changes through any handle fail with the exception the framework throws for `kind`, so `CapIOException.KindOf` reports `kind`. `CapErrorKind.Other` is what a full disk reports. |
| `Capacity` | Once the files with a name hold this many bytes between them, a write that would grow them fails as a full disk does. `UsedBytes` reports the current total. |

A test that injects a fault checks what the component leaves behind afterwards.
`ReportStore.Save` writes a new report to a scratch name and renames it over the old one, so a
write that fails leaves the old report untouched:

```csharp
var fs = new InMemoryFileSystem();
fs.AddFile("2026-09-01.json", """{ "total": 3 }""");
using Dir root = fs.OpenRoot();
var store = new ReportStore(root);

fs.FailNextWrites(1, CapErrorKind.Other);   // what a full disk reports

IOException e = Assert.ThrowsAny<IOException>(() => store.Save(new DateOnly(2026, 9, 1), """{ "total": 4 }"""));
Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(e));
Assert.Equal("""{ "total": 3 }""", fs.ReadAllText("2026-09-01.json"));
Assert.Equal(["2026-09-01.json"], fs.GetEntries());   // no half-written scratch file left behind
```

## A stubbed `IDir`

Some tests want a stub rather than a filesystem: one that fails the third write, or checks that
a file was read exactly once. `Dir`, `CapFile`, `DirEntry` and `CapOpened` implement `IDir`,
`ICapFile`, `IDirEntry` and `ICapOpened`, which have the same members. A component that takes
the interface can be handed a stub from any mocking library. This one uses Moq:

```csharp
Mock<IDir> reports = new();
reports.Setup(dir => dir.EnumerateEntries()).Returns(
[
    TestEntries.Create("2026-09-01.json", CapFileType.File, TestFileIds.Next(), reports.Object),
    TestEntries.Create("2026-09-02.json", CapFileType.Directory, TestFileIds.Next(), reports.Object),
    TestEntries.Create("notes.json", CapFileType.File, TestFileIds.Next(), reports.Object),
]);

IReadOnlyList<DateOnly> days = new ReportStore(reports.Object).ListDays();

Assert.Equal([new DateOnly(2026, 9, 1)], days);
reports.Verify(dir => dir.ReadAllText(It.IsAny<string>()), Times.Never);
```

The helpers in `Cap.Fs.Ext` (`Walk`, `Glob`, `CopyTo`, the atomic writes, `DeleteTree` and
the rest) are extension methods on `IDir`, so a component that takes the interface keeps them,
and a stub or a wrapper sees the individual calls each helper makes through it.

A stub runs none of this library's resolution, so it cannot show that the component stays
inside its directory. The in-memory filesystem can, so prefer it unless the test is about the
calls themselves. The interfaces do not carry the containment guarantee, which belongs to
`Dir`. A component that takes `IDir` in production is confined only if it is handed a `Dir`. The
[threat model](threat-model.md#57-the-handle-interfaces-carry-no-guarantee) has the detail.

### Values for stubs to return

`CapMetadata`, `CapFileId` and `DirEntry` have no public constructors, so production code can
only get them from a handle. An identity is worth comparing only because the filesystem issued
it, and `IsSameFileAs` relies on that. For stubs, `Cap.Std.Testing` makes these values, two of
which the stub above uses:

- `CapMetadataBuilder` has a setter for each field. It defaults to an empty regular file with
  one name, a fresh identity, and the permissions a new file gets on the running platform.
  `WithUnixMode` and `WithWindowsAttributes` each clear the other, because a real description
  never holds both.
- `TestFileIds.Create(volume, node)` makes a given identity, and `TestFileIds.Next()` makes an
  unused one. To describe one object reached under two names, give two descriptions the same
  identity.
- `TestEntries.Create(name, type, fileId, owner)` makes an `IDirEntry` that opens and describes
  itself by calling `owner` with its name, the same way `DirEntry` does. Pass the stub as
  `owner`, and the entry's opens become calls on the stub that the test can check.

## A scratch directory on disk

Some behaviour exists only on a real filesystem: which [resolution backend](backends.md) the
host has, whether a flush reaches storage, what `UnsafeGetHandle` returns, and how the
component copes with files another program wrote. For those, `CapTempDir.New` makes a
directory of the test's own in the system's temporary location, and removes it and everything
in it on disposal:

```csharp
using CapTempDir scratch = CapTempDir.New(AmbientAuthority.Acquire());
var store = new ReportStore(scratch.Directory);

store.Save(new DateOnly(2026, 9, 1), """{ "total": 3 }""");   // flushed to storage by default

Assert.Equal("""{ "total": 3 }""", store.Load(new DateOnly(2026, 9, 1)));
Assert.Equal(Dir.ResolutionBackend, scratch.Directory.Backend);
```

`CapTempDir.New` takes an `AmbientAuthority` token, because it opens a place on the host. Set
`CAPDOTNET_PERSIST_TEMPORARY=1` to keep the directories after a run, to inspect what a failing
test left.

Keep these tests few. Each answers as the platform it runs on answers, which is the point of
having it, and also why it can pass on Linux and fail on Windows.

## Code written against `IFileSystem`

Code written against System.IO.Abstractions' `IFileSystem` rather than a `Dir` can be tested
the same way: wrap the in-memory root in a `DirFileSystem` from `Cap.IO.Abstractions`. See
[`IFileSystem` over a `Dir`](io-abstractions.md), which also covers moving such a component
onto `Dir`.

## Running tests in parallel

Each handle carries the backend that opened it. So one test process can hold handles on
several in-memory filesystems and on the disk at the same time, and tests in all three styles
run in parallel without any of them switching a global setting.

Every member of `InMemoryFileSystem`, and every operation through one of its handles, may be
called from any number of threads. One lock guards the whole tree, so operations do not run in
parallel within one filesystem. Separate filesystems share nothing, so give each test its own:
build it in the test, as above, rather than sharing one through a fixture. A test that does
share one sees what every other test wrote to it.

Each `CapTempDir.New` draws a directory of its own, so tests on disk do not collide either.
