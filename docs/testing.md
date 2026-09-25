# Testing code that takes a `Dir`

Code written against a `Dir` can be tested against the disk: open a `CapTempDir`, build a
tree in it, and hand the code a handle. That works, but it is slow, it behaves differently on
each platform, and it cannot produce on demand the awkward states a unit test often wants: a
full disk, a permission failure, a name that refuses to be removed.

The `Cap.Std.Testing` package holds a filesystem in memory that hands out real `Dir` handles.

```csharp
using Cap.Std;
using Cap.Std.Testing;

var fs = new InMemoryFileSystem();
fs.AddFile("config/app.json", """{ "x": 1 }""");
fs.AddDirectory("data");
fs.AddSymbolicLink("data/latest", "../config");

using Dir root = fs.OpenRoot();
var loader = new ConfigLoader(root.OpenDir("config"));

loader.Save();
Assert.Equal("""{ "x": 2 }""", fs.ReadAllText("config/app.json"));
```

Reference it from test projects only. It lives in its own package so that production code
cannot pick up an in-memory backend without depending on a test package.

## Stubbing the interfaces

Some tests want a stub rather than a filesystem: one that fails the third write, or checks that
a file was read exactly once. `Dir`, `CapFile`, `DirEntry` and `CapOpened` implement `IDir`,
`ICapFile`, `IDirEntry` and `ICapOpened`, which have the same members. A component that takes
the interface can be handed a stub from any mocking library:

```csharp
IDirEntry report = Mock.Of<IDirEntry>(entry => entry.Name == "a.json" && entry.Type == CapFileType.File);

Mock<IDir> reports = new();
reports.Setup(dir => dir.EnumerateEntries()).Returns([report]);
reports.Setup(dir => dir.ReadAllText("a.json")).Returns("""{ "total": 3 }""");

new ReportIndex(reports.Object).Load();
reports.Verify(dir => dir.ReadAllText("a.json"), Times.Once);
```

A stub runs none of this library's resolution, so it cannot show that the component stays
inside its directory. The in-memory filesystem can, so prefer it unless the test is about the
calls themselves. The interfaces do not carry the containment guarantee, which belongs to
`Dir`. A component that takes `IDir` in production is confined only if it is handed a `Dir`. The
[threat model](threat-model.md#57-the-handle-interfaces-carry-no-guarantee) has the detail.

### Values for stubs to return

`CapMetadata`, `CapFileId` and `DirEntry` have no public constructors, so production code can
only get them from a handle. An identity is worth comparing only because the filesystem issued
it, and `IsSameFileAs` relies on that. For stubs, `Cap.Std.Testing` makes these values:

```csharp
Mock<IDir> reports = new();
reports.Setup(dir => dir.EnumerateEntries()).Returns(
[
    TestEntries.Create("a.json", CapFileType.File, TestFileIds.Next(), reports.Object),
]);
reports.Setup(dir => dir.GetMetadata("a.json", false)).Returns(
    new CapMetadataBuilder().WithLength(900).WithLastWriteTime(yesterday).Build());
```

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

## What is real and what is simulated

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

## Building and inspecting a tree

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

## Options

| Option | Default | Meaning |
|---|---|---|
| `PathSyntax` | The running platform's | `CapPathSyntax.Windows` makes the handles read paths as Windows does, on any machine: `\` separates components, and reserved and rooted names are refused. It also switches the recorded permissions from Unix mode bits to Windows attributes. |
| `CaseSensitive` | Sensitive under Unix syntax, not under Windows syntax | When false, a name is found under any spelling, keeps the spelling it was created with, cannot be created a second time in another case, and can be renamed to change only its case. |
| `TimeProvider` | A clock stopped at 2000-01-01T00:00:00Z | Where timestamps come from. Pass a `FakeTimeProvider` to let time pass under the test's control. |
| `Resolution` | `ResolutionBackend.PortableWalk` | `ConfinedOpen` resolves a whole path in one call, as the Linux kernel does. The two reach the same answers, so the same test can run down both. |

Every handle reports `ResolutionBackend.InMemory` as its `Backend`. Its handles are not
operating-system objects, so `UnsafeGetHandle` throws `NotSupportedException`, and `AsStream`
returns a stream of the filesystem's own rather than a `FileStream`.

## Faults

| Member | Effect |
|---|---|
| `SetUnreadable(path)` | Every operation on the object, and every lookup inside it if it is a directory, fails with `UnauthorizedAccessException`. Its name can still be described, renamed and removed. |
| `SetUndeletable(path)` | Removing or replacing the name fails with `UnauthorizedAccessException`, and clearing the read-only attribute does not help. |
| `FailNextWrites(count, kind)` | The next `count` writes, appends or length changes through any handle fail with the exception the framework throws for `kind`, so `CapIOException.KindOf` reports `kind`. `CapErrorKind.Other` is what a full disk reports. |
| `Capacity` | Once the files with a name hold this many bytes between them, a write that would grow them fails as a full disk does. `UsedBytes` reports the current total. |

## Threads

Every member, and every operation through a handle, may be called from any number of threads.
One lock guards the whole tree, so operations do not run in parallel within one filesystem.
Separate filesystems share nothing, so tests that each build their own run in parallel with
each other and with tests on disk.
