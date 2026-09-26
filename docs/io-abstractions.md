# `IFileSystem` confined to a `Dir`

[System.IO.Abstractions](https://github.com/TestableIO/System.IO.Abstractions) is how most .NET
code makes file access testable. A component takes an `IFileSystem`, production passes
`FileSystem`, a thin wrapper over the static `System.IO` types, and tests pass
`MockFileSystem`. A lot of existing code is written that way.

The `Cap.IO.Abstractions` package adds `DirFileSystem`, an `IFileSystem` that resolves every
path beneath a `Dir`. Code already written against `IFileSystem` is confined by changing the
composition root, and the component itself stays as it is. `UploadStore` in
[`samples/TestableComponent`](../samples/TestableComponent) stores uploads under the names
their senders chose, and checks none of them:

```csharp
public sealed class UploadStore(IFileSystem fileSystem, string directory)
{
    public void Save(string fileName, byte[] contents) =>
        fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(directory, fileName), contents);

    public byte[] Load(string fileName) =>
        fileSystem.File.ReadAllBytes(fileSystem.Path.Combine(directory, fileName));
}
```

It used to be built with `new UploadStore(new FileSystem(), "/srv/app/data/uploads")`. Its
composition root now opens the uploads directory once and hands it a `DirFileSystem` whose
root is that directory:

```csharp
using Dir data = Dir.Open(dataPath, AmbientAuthority.Acquire());
using Dir reportsDir = data.OpenOrCreateDir("reports");
using Dir uploadsDir = data.OpenDir("uploads");

var reports = new ReportStore(reportsDir);
var uploads = new UploadStore(new DirFileSystem(uploadsDir), "/");
```

The component's tests can keep using `MockFileSystem`. They can also switch to a
`DirFileSystem` over an in-memory `Dir` from [`Cap.Std.Testing`](testing.md), to get the real
containment behaviour in tests too:

```csharp
var fs = new InMemoryFileSystem();
fs.AddFile("private/secret.txt", "not for uploaders");
fs.AddSymbolicLink("uploads/shared", "../private", targetIsDirectory: true);
using Dir uploads = fs.OpenRoot("uploads");
var store = new UploadStore(new DirFileSystem(uploads), "/");

Assert.Throws<SandboxEscapeException>(() => store.Load("shared/secret.txt"));
Assert.Throws<SandboxEscapeException>(() => store.Save("../private/planted.txt", [1]));
Assert.Equal(["secret.txt"], fs.GetEntries("private"));
```

System.IO.Abstractions on its own does not confine, and does not claim to. See the
[comparison](../README.md#how-it-compares). This package gives code written against it both
testability and confinement.

Once the composition root builds a `DirFileSystem`, nothing else needs to construct the
ambient `FileSystem`. With `CAP0001` turned on, the [analyzer](analyzers.md#ifilesystem)
reports `new FileSystem()`, its wrappers and Testably's `RealFileSystem`, and leaves code that
takes an `IFileSystem` alone.

## The virtual namespace

`IFileSystem` callers pass absolute paths, work relative to a current directory, and expect
full names back. `DirFileSystem` presents the `Dir` as the whole of a namespace:

- **The root is `/`**, on every platform. On Windows, code that looks for a drive letter can
  be given one with `new DirFileSystemOptions { VirtualDrive = 'C' }`, which spells the root
  `C:\`. The root is still the handle, and no drive is reached.
- **An absolute path loses the root and nothing else.** `/data/a.txt` becomes `data/a.txt`
  beneath the `Dir`. Exactly one root is removed, so `//etc/passwd` reaches the `Dir` as
  `/etc/passwd` and is refused as absolute. A path rooted somewhere else, such as another
  drive or a UNC share, is passed on as written and refused.
- **A relative path is taken against the current directory.** The current directory belongs
  to the `DirFileSystem` instance and is never the process's. It starts at the root, and
  `Directory.SetCurrentDirectory` moves it after checking that the target is a directory.
  The join only spells out the caller's request in full.
- **Everything else is resolved by the `Dir`.** `..`, symbolic links and every other
  component reach it as the caller wrote them. A `..` above the root, or a link whose target
  leaves the root, is refused with `SandboxEscapeException`. The adapter never folds `..`
  away itself, because `a/../b` and `b` are different places whenever `a` is a symbolic link
  ([Path parsing](paths.md#-is-walked-not-collapsed) has the detail). No check on the text
  stands between the caller and the disk.

On Linux and macOS a host path such as `/etc/passwd` is therefore a path in the virtual
namespace. It names `etc/passwd` beneath the root, not the host's file. On Windows,
`C:\Windows` is rooted on a drive and is refused.

### Full names are virtual

`FullName`, `DirectoryName`, `Path.GetFullPath`, `Directory.GetCurrentDirectory` and the
strings enumeration returns are paths in the virtual namespace. Given back to the same
`DirFileSystem` they are harmless. Given to `System.IO` they name a different file, on the
host, outside the directory, and that is a bug.

They are also folded as `System.IO` folds them: `/a/../b` is shown as `/b`. A folded name
describes a request, not a location. When `a` is a symbolic link, `/a/../b` and `/b` lead to
different places. The adapter keeps the caller's own spelling for everything it resolves,
including the paths inside a `FileInfo` or `DirectoryInfo`, and uses the folded one only for
display. [Why there is no `Dir.FullName`](no-full-name.md) explains why `Dir` itself offers no
such string.

## Errors

`IFileSystem` callers catch `FileNotFoundException`, `DirectoryNotFoundException`,
`UnauthorizedAccessException` and `IOException`. `DirFileSystem` throws the one `System.IO`
throws for the same condition. For example, opening a directory as a file is an access failure,
and a file beneath a missing directory is a missing directory. Messages name the virtual path.

Where `System.IO` throws a plain `IOException`, this throws a `CapIOException`, which derives
from it, so a `catch (IOException)` still matches and `CapIOException.KindOf` says why it
failed. A refused escape is a `SandboxEscapeException`, which is also an `IOException`. A
generic handler catches it, and a handler that wants to log escape attempts can catch it by
type. Existence checks (`File.Exists`, `Directory.Exists`, `Path.Exists`) answer false for
anything they cannot reach, an escape included, as `System.IO` answers for anything it cannot
see.

## Differences from `System.IO`

Each of these is deliberate and is covered by the package's tests.

| Area | `System.IO` | `DirFileSystem` |
|---|---|---|
| Reach | the whole filesystem | beneath the `Dir`. A `..` above the root or a link that leaves it is a `SandboxEscapeException` |
| A symbolic link at the name an open creates, truncates or appends to | followed | refused with `CapIOException`, and the link and its target are left alone. A write usually targets a name somebody else chose, and following a link planted there would empty or create a different file. Remove the link first to write at its name |
| Symbolic link targets | stored as given | a rooted target is refused with `SandboxEscapeException`, since it names a place on the host. Use a relative target |
| `ResolveLinkTarget` on a link with a rooted target | returns the target | `SandboxEscapeException` |
| `//x` | the same as `/x` on Unix | refused as absolute: only one root is removed |
| Full names | host paths | virtual paths, folded lexically. See above |
| Current directory | process-wide | per `DirFileSystem` instance |
| `Directory.GetLogicalDrives` | the host's drives | the virtual root |
| `Path.GetTempPath` | the host's temporary directory | `/.tmp/`, created beneath the root when first asked for. What goes there is part of the tree, visible to whatever else can see it, and is not cleared away |
| `Path.GetTempFileName` | a `tmp*.tmp` file in the host's temporary directory | an empty file in `/.tmp/`, with a name drawn by `CapTempFile` |
| `Directory.CreateTempSubdirectory(prefix)` | in the host's temporary directory | in `/.tmp/`, named `prefix` followed by a name drawn by `CapTempDir` |
| `Path.GetRandomFileName` | a random name | `NotSupportedException`. The adapter is given no entropy; use one of the members above |
| Search patterns | may include a directory part | name entries in one directory only. A pattern with a separator is an `ArgumentException`. `EnumerationOptions.ReturnSpecialDirectories` is ignored |
| Recursive enumeration | does not descend into links | the same. A link that leads to a directory inside the tree is reported as a directory, and one that leads out as a file |
| `File.Copy` | copies contents and, on Unix, permissions | copies contents only |
| `DirectoryInfo.CreateSubdirectory("../x")` | refused unless the result is beneath the directory | allowed while it stays beneath the root, which still confines it |
| Removing or moving the root | not applicable | `IOException`. The root is the handle and has no name to remove |
| Changing permissions or attributes: `SetAttributes`, `SetUnixFileMode`, the `Attributes`, `IsReadOnly` and `UnixFileMode` setters | supported | `NotSupportedException`: `Cap.Std` cannot change them beneath a handle. The `Attributes` and `IsReadOnly` setters accept the value already there |
| Creating with a Unix mode: `Directory.CreateDirectory(path, mode)`, `FileStreamOptions.UnixCreateMode` | supported | `NotSupportedException`. Ignoring the mode would create something more permissive than asked for |
| Setting a creation time | supported | `NotSupportedException`. Access and write times can be set |
| Members that take a `SafeFileHandle`, and `Wrap` on the factories | supported | `NotSupportedException`: the handle, or the host path inside a `FileInfo`, `DirectoryInfo` or `FileStream`, was not opened beneath the `Dir` |
| `DriveInfo`, `FileSystemWatcher`, `FileVersionInfo`, access control lists, `Encrypt`, `Decrypt` | supported | `NotSupportedException`, saying why |

The members that throw `NotSupportedException` do so for every argument. A missing member
would fail at the first call anyway, and the exception says what to use instead.

## Differences from `MockFileSystem`

The package's behavioural tests run against `DirFileSystem` on disk, `DirFileSystem` in
memory, and `MockFileSystem`. Where `MockFileSystem` does not do what `System.IO` does, the
test is skipped for it. The skips are:

- reading a directory as a file does not throw. `System.IO`, and `DirFileSystem`, throw
  `UnauthorizedAccessException`;
- a file beneath a missing directory is reported as `FileNotFoundException`, not
  `DirectoryNotFoundException`;
- copying a file onto itself with `overwrite: true` throws `NullReferenceException`, not
  `IOException`;
- `Directory.SetCurrentDirectory` accepts a directory that does not exist;
- a relative symbolic link target is taken against the current directory rather than the
  directory holding the link.

A test that passes on `MockFileSystem` because of one of these can fail against
`DirFileSystem`. Beyond these, `MockFileSystem` does not confine anything, and the
[differences from `System.IO`](#differences-from-systemio) above apply to it as they do to
`System.IO`.

## Moving a component onto `Dir`

`DirFileSystem` confines a component without changing it. Moving the component onto `Dir`
itself goes further: it drops the directory string, the virtual full names and the
[differences](#differences-from-systemio) above, and it gets the whole of `Dir`, including the
atomic writes, walks and copies in [`Cap.Fs.Ext`](convenience-layer.md). The two can be done
one component at a time, since a `DirFileSystem` and a component on `Dir` can share one
handle.

Here is `UploadStore` from the top of this page, moved:

```csharp
public sealed class DirUploadStore(Dir uploads)
{
    public void Save(string fileName, byte[] contents) => uploads.WriteAllBytes(fileName, contents);

    public byte[] Load(string fileName) => uploads.ReadAllBytes(fileName);
}
```

The steps are the same for any component:

1. **Replace the `IFileSystem` parameter, and the directory string that went with it, with a
   `Dir`.** Take `IDir` instead only when every path the component touches is one it made
   itself, so that a test can hand it a stub. A component that resolves names someone else
   chose takes `Dir`, because the containment guarantee belongs to that type and not to the
   interface ([threat model](threat-model.md#57-the-handle-interfaces-carry-no-guarantee)).
2. **Remove the path building.** `fileSystem.Path.Combine(directory, name)` becomes `name`,
   relative to the handle. A subdirectory the component works in becomes a `Dir` of its own,
   from `OpenDir` or `OpenOrCreateDir`.
3. **Replace each member.** `fileSystem.File`, `fileSystem.Directory` and `fileSystem.Path`
   have the members of `System.IO`'s `File`, `Directory` and `Path`, and
   [Migrating from `System.IO`](migration.md) gives the replacement for each. The members that
   exist only on `IFileSystem` are below.
4. **Move the tests** from `MockFileSystem` to `InMemoryFileSystem`, using the second table
   below.

| `IFileSystem` | Replacement | Notes |
|---|---|---|
| `IFileSystem` constructor parameter | `Dir`, or `IDir` | See step 1. |
| `new FileSystem()` in the composition root | `Dir.Open(path, AmbientAuthority.Acquire())` | Once, for the directory the component needs. |
| `fileSystem.Path.Combine(directory, p)` | `p` | Relative to the handle. |
| `fileSystem.File.X(p, …)`, `fileSystem.Directory.X(p, …)` | as for `File.X` and `Directory.X` in [Migrating from `System.IO`](migration.md) | |
| `fileSystem.FileInfo.New(p)`, `IFileInfo` | `dir.GetMetadata(p)` to describe it, `dir.OpenFile(p)` to use it | A `CapMetadata` describes the object as it was when asked; there is no object that re-reads a path. |
| `fileSystem.DirectoryInfo.New(p)`, `IDirectoryInfo` | `dir.OpenDir(p)` | A handle on the directory rather than a description of a path. |
| `IDirectoryInfo.EnumerateFiles()`, `GetDirectories()` and the rest | `dir.EnumerateEntries()`, filtered by `Type` | **Ext** `Glob` for a pattern, **Ext** `Walk` for a whole tree. |
| `fileSystem.FileStream.New(p, mode, access)` | `dir.OpenFile(p, mode, access).AsStream()` | Dispose the `CapFile` as well as the stream. |
| `IFileSystemInfo.FullName`, `fileSystem.Path.GetFullPath(p)` | none | A `Dir` has no full name. See [Why there is no `Dir.FullName`](no-full-name.md). |
| `fileSystem.Directory.GetCurrentDirectory()`, `SetCurrentDirectory` | none | Pass the `Dir` for the directory instead. |
| `fileSystem.Path.GetTempPath()`, `GetTempFileName()` | `CapTempDir.NewIn(dir)`, `CapTempFile.New(dir)` | Scratch space beneath a directory the component was given. |
| `fileSystem.FileSystemWatcher`, `fileSystem.DriveInfo` | none | As for `System.IO`. |

In tests:

| `MockFileSystem` | `InMemoryFileSystem` | Notes |
|---|---|---|
| `new MockFileSystem()` | `new InMemoryFileSystem()`, then `OpenRoot()` for the handle | Paths are relative to the root, with `/` between names on every platform. There is no drive to spell. |
| `new MockFileSystem(files)`, `AddFile(p, new MockFileData(text))` | `AddFile(p, text)` | Missing directories are created on the way. |
| `AddEmptyFile(p)` | `AddFile(p)` | |
| `AddDirectory(p)` | `AddDirectory(p)` | |
| `GetFile(p).TextContents`, `.Contents` | `ReadAllText(p)`, `ReadAllBytes(p)` | |
| `FileExists(p)` | `Exists(p)` | True for anything at the name. |
| `AllFiles`, `AllPaths` | `GetEntries(p)` | One directory at a time. |
| No equivalent | `AddSymbolicLink(p, target)` | Including a link that leads outside, to test that it is refused. |
| No equivalent | `FailNextWrites`, `Capacity`, `SetUnreadable`, `SetUndeletable` | See [Faults](testing.md#faults). |

The tests for `DirUploadStore` in the sample build the same tree as those for `UploadStore`,
and check the same refusals:

```csharp
var fs = new InMemoryFileSystem();
fs.AddFile("private/secret.txt", "not for uploaders");
fs.AddSymbolicLink("uploads/shared", "../private", targetIsDirectory: true);
using Dir uploads = fs.OpenRoot("uploads");
var store = new DirUploadStore(uploads);

store.Save("avatar.png", [1, 2, 3]);

Assert.Equal([1, 2, 3], fs.ReadAllBytes("uploads/avatar.png"));
Assert.Throws<SandboxEscapeException>(() => store.Load("shared/secret.txt"));
```

## Ownership and threads

`DirFileSystem` does not dispose the `Dir` it is given, and is not `IDisposable`. Whoever
opened the `Dir`, usually the composition root, closes it after the last use of the file
system. It accepts a `Dir` rather than an `IDir`. `IFileSystem` is already the seam for
substituting a test double, and taking the interface would make its containment depend on
whatever implementation it was handed.

Every member is safe to call from any thread. The current directory is shared by every caller
of one instance, as the process's is shared by every caller of `System.IO`.

## Versions

The package depends on `TestableIO.System.IO.Abstractions` and on
`Testably.Abstractions.FileSystem.Interface`, where the interfaces are declared. Both are MIT
licensed, and they are the only third-party runtime dependencies of any package in this
repository. Their maintainers add members to the interfaces in minor releases, and a newer
interface assembly beside this one would load and then fail the first time a new member was
called. So the dependency is pinned to the minor versions the adapter is built and tested
against, 22.2.x and 10.3.x, and each new minor version needs a release of this package that
implements its members.

The `Wrappers` package, which holds the ambient `FileSystem`, is not a dependency.
