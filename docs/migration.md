# Migrating from `System.IO`

The shape of every change is the same: a static call that takes a full path becomes an
instance call on a `Dir` that takes a path relative to it.

```csharp
// Before
string text = File.ReadAllText(Path.Combine(dataRoot, "settings", "app.json"));

// After: `data` was opened once, at start-up, and handed to this code.
string text = data.ReadAllText("settings/app.json");
```

Paths use `/` on every platform, and `\` too on Windows. They must be relative: an absolute
path or a Windows device name is refused with `SandboxEscapeException` rather than resolved.
`..` is resolved beneath the handle, as a step back out of a directory the path entered, and
refused with `SandboxEscapeException` when it would climb above the handle. Where a `System.IO` method would have created or found a path by joining strings,
the replacement usually opens a `Dir` on the directory and works inside that, which is also
what the [analyzer's `CAP0005`](analyzers.md#cap0005-paths-built-by-joining-strings) asks for.

One behaviour differs on purpose. An open that creates, empties or appends to a file refuses a
symbolic link at the name it is given. That covers `CreateFile`, `WriteAllBytes`,
`WriteAllBytesAsync`, `WriteAllText`, `WriteAllLines`, `AppendAllText` and `OpenFile` with any
`FileMode` but `Open`. It holds under every symbolic-link policy, even
for a link to another file in the same tree, and even for a dangling one. `System.IO` follows
the link and writes to what it leads to. To write at a name that holds a link, remove the link
first with `DeleteFile`, or publish the new file over it with **Ext** `WriteAllTextAtomic`,
which replaces the link. Opening an existing file with `FileMode.Open` still follows a link.

`Dir` members are in `Cap.Std`. Members marked **Ext** are extension methods in `Cap.Fs.Ext`.

## `File`

| `System.IO` | Replacement | Notes |
|---|---|---|
| `File.Exists(p)` | **Ext** `dir.IsFile(p, followLink: true)` | Follows a link at `p`, as `File.Exists` does, but only while it stays beneath `dir`; a link leading out answers false. `dir.IsFile(p)` asks about the name itself, so a link answers false. `dir.Exists(p)` is true for anything at the name, directory included. |
| `File.ReadAllText(p)`, `File.ReadAllText(p, encoding)` | `dir.ReadAllText(p)`, `dir.ReadAllText(p, encoding)` | UTF-8, or the encoding given, unless a byte-order mark says otherwise, as `File.ReadAllText` does. |
| `File.ReadAllTextAsync(p)` | `dir.ReadAllTextAsync(p)` | |
| `File.ReadAllBytes(p)` | `dir.ReadAllBytes(p)` | |
| `File.ReadAllBytesAsync(p)` | `dir.ReadAllBytesAsync(p)` | |
| `File.ReadAllLines(p)` | `dir.ReadAllLines(p)` | And with an encoding. |
| `File.ReadLines(p)`, `File.ReadLinesAsync(p)` | `dir.ReadLines(p)`, `dir.ReadLinesAsync(p)` | The file is opened by the call, so a missing one is reported there. The sequence can be enumerated once: a second enumeration would have to open the name again, which may by then hold something else. |
| `File.WriteAllBytes(p, b)` | `dir.WriteAllBytes(p, b)` | Truncates and writes in place. Refuses a symbolic link at `p`, which `File.WriteAllBytes` writes through. |
| `File.WriteAllBytesAsync(p, b)` | `dir.WriteAllBytesAsync(p, b)` | |
| `File.WriteAllText(p, s)`, `File.WriteAllText(p, s, encoding)` | `dir.WriteAllText(p, s)`, `dir.WriteAllText(p, s, encoding)` | Truncates and writes in place, UTF-8 with no byte-order mark unless an encoding is given. Refuses a symbolic link at `p`. **Ext** `dir.WriteAllTextAtomic(p, s)` publishes instead, so readers see the old file or the new one, never half of either. The published file is a new file: it gets the replaced file's permissions, but not its owner, and other hard links to the old file keep the old contents. |
| `File.WriteAllLines(p, lines)` | `dir.WriteAllLines(p, lines)` | And with an encoding. Lines are taken from the sequence as they are written. |
| `File.AppendAllText(p, s)` | `dir.AppendAllText(p, s)` | And with an encoding, whose byte-order mark is written only into an empty file. Every write goes to the end of the file on every platform. Refuses a symbolic link at `p`, which `File.AppendAllText` follows. |
| No equivalent: an open that appends and also reads, empties the file or must create it | `dir.OpenFile(p, mode, access, append: true)` | Appending is a flag of its own here, as in POSIX, so it combines with any mode and with reading, and `CapFile.IsAppending` changes it on an open file. `FileMode.Append` keeps its framework meaning. |
| `File.Open(p, mode, access, share)` | `dir.OpenFile(p, mode, access, share)` | Returns a `CapFile`; `.AsStream()` gives a `Stream`. Every mode but `FileMode.Open` refuses a symbolic link at `p`, and `noFollow: true` makes `FileMode.Open` refuse one too. |
| `File.OpenRead(p)` | `dir.OpenFile(p)` | Read is the default. |
| `File.OpenWrite(p)` | `dir.OpenFile(p, FileMode.OpenOrCreate, FileAccess.Write)` | |
| `File.Create(p)` | `dir.CreateFile(p)` | Refuses a symbolic link at `p`. |
| `new FileStream(p, …)` | `dir.OpenFile(p, …).AsStream()` | |
| `File.OpenHandle(p, …)` | `dir.OpenFile(p, …)` | `CapFile` has positional `Read`/`Write` and their `Async` forms, like `RandomAccess`. |
| `File.Delete(p)` | `dir.DeleteFile(p)` | A link is removed, not its target. Throws if nothing is there; `File.Delete` does not. `dir.TryDeleteFile(p)` returns false instead. |
| `File.Move(a, b)` | `dir.Rename(a, dir, b)` | The destination is named against a `Dir` too, and may be a different one: moving between two trees needs authority over both. |
| `File.Move(a, b, overwrite: true)` | `dir.Rename(a, dir, b, replaceExisting: true)` | |
| `File.Replace(a, b, backup)` | two `Rename` calls | There is no single-call equivalent. |
| `File.Copy(a, b)`, `File.Copy(a, b, overwrite)` | **Ext** `dir.CopyFile(a, toDir, b, overwrite)` | The destination is named against a `Dir` too. Carries the source's permissions, as `File.Copy` does, and not its times. With `overwrite` the copy is published over `b`, so a link there is replaced rather than written through. **Ext** `dir.CopyFile(a, toDir, b, options)` takes a `CopyOptions` and copies the one file as `CopyTo` would, carrying times, permissions or holes only when asked and not following a link at `a`. **Ext** `source.CopyTo(destination)` copies a whole tree between two `Dir`s. |
| `File.CreateSymbolicLink(p, target)` | `dir.CreateSymlink(p, target)` | `dir.CreateDirSymlink` for a link to a directory, which Windows records differently. A relative target is stored as written (on Windows with each `/` stored as `\`); whether it can be followed is decided when it is used. A rooted target (`/etc`, `C:\dir`) is refused with `SandboxEscapeException`, where `File.CreateSymbolicLink` accepts one. |
| `File.ResolveLinkTarget(p, false)`, `FileInfo.LinkTarget` | `dir.ReadLink(p)` | |
| `File.ResolveLinkTarget(p, true)` | open through the link instead | Resolution follows a link only while it stays inside the tree; there is no call that hands back where it leads as a path. |
| Creating a hard link | `dir.CreateHardLink(p, toDir, to)` | Both ends need a `Dir`. A symbolic link at `p` gets the second name itself; `followLink: true` gives it to what the link leads to. |
| `File.GetAttributes(p)` | `dir.GetMetadata(p).Permissions.TryGetWindowsAttributes(out var a)` | |
| `File.GetUnixFileMode(p)` | `dir.GetMetadata(p).Permissions.TryGetUnixMode(out var m)` | |
| `File.GetLastWriteTimeUtc(p)` | `dir.GetMetadata(p).LastWriteTime` | A `DateTimeOffset`. A symbolic link at `p` is described as itself; `dir.GetMetadata(p, followLink: true)` describes what it leads to. |
| `File.GetLastAccessTimeUtc(p)` | `dir.GetMetadata(p).LastAccessTime` | Null where the filesystem keeps no access time, which Linux reports for some filesystems. |
| `File.GetCreationTimeUtc(p)` | `dir.GetMetadata(p).CreationTime` | Null where the filesystem records none, rather than a made-up date. |
| `new FileInfo(p).Length` | `dir.GetMetadata(p).Length` | |
| No equivalent: the link count, `st_nlink` | `dir.GetMetadata(p).LinkCount` | |
| No equivalent: the status-change time, `st_ctime` or Windows `ChangeTime` | `dir.GetMetadata(p).ChangeTime` | Null where the filesystem records none, which on Windows is the FAT family. |
| `File.SetLastWriteTimeUtc(p, t)`, `File.SetLastAccessTimeUtc(p, t)` | `dir.SetTimes(p, lastWrite: CapFileTime.At(t))`, and `lastAccess:` | Does not follow a symbolic link at `p` unless given `followLink: true`: the link's own times are set, where `File.SetLastWriteTime` sets its target's. `CapFileTime.Now` asks the system to stamp the time of the change. On an open file, `file.SetTimes(...)`, which needs a handle opened for writing. |
| `File.SetCreationTime` | none | Not every platform can set one. |
| `File.SetAttributes(p, a)`, `File.SetUnixFileMode(p, m)` | `file.SetPermissions(CapPermissions.FromWindowsAttributes(a))`, `file.SetPermissions(CapPermissions.FromUnixMode(m))` on an open `CapFile`, or `dir.SetPermissions(...)` on an open `Dir` | Set through a handle, never by name: open what is to be changed first. That fixes which object changes, and it is the only way to leave a symbolic link alone on Linux, which has no call that changes a link's own mode. A file handle must be able to write. The mode is written as given, with no umask. A value of the other platform's kind is refused rather than translated, and on Windows the security descriptor is not touched. |
| No equivalent: a second handle on an open file, as `dup(2)` gives | `file.Clone()` | The same open file, with the same access, whatever the name now holds. Refused for a file opened with `FileOptions.Asynchronous` on Windows, where a copy of the handle could not do asynchronous work. |
| No equivalent: `fdatasync(2)`, cap-std's `File::sync_data` | `file.Flush(toDisk: true)` | Not provided separately: the flush commits the metadata as well as the data, which costs more on some filesystems and is never less durable. |
| `File.Encrypt`, `File.Decrypt` | none | |

## `Directory`

| `System.IO` | Replacement | Notes |
|---|---|---|
| `Directory.Exists(p)` | **Ext** `dir.IsDir(p, followLink: true)` | Follows a link at `p`, as `Directory.Exists` does, but only while it stays beneath `dir`. `dir.IsDir(p)` answers false for a link. |
| `Directory.CreateDirectory(p)` | `dir.OpenOrCreateDirAll(p)` | Returns a `Dir` on the last directory. A path that would leave the `Dir`, meet a file where a directory is needed, or pass a link the policy refuses is refused before anything is made. A failure while making the directories leaves the ones already made, as `Directory.CreateDirectory` does. `dir.OpenOrCreateDir(p)` makes the last name only, and refuses a link there. |
| `Directory.CreateDirectory(p)`, new name expected | `dir.CreateDir(p)` | Fails if the name is taken. |
| `new DirectoryInfo(p)` | `dir.OpenDir(p)` | A `Dir` rather than a description of a path. |
| No equivalent: open `p` whichever it holds, as `open(2)` without `O_DIRECTORY` does | `dir.OpenAny(p)` | Reads only. Returns a `CapOpened`: check `IsDirectory`, then `TakeDir()` or `TakeFile()`. The path is resolved once, so the kind reported is that of the object opened, which trying `OpenFile` and then `OpenDir` can't promise. |
| `Directory.Delete(p)` | `dir.DeleteDir(p)` | Empty directories only. A link to a directory is removed with `dir.DeleteFile(p)`, or **Ext** `dir.RemoveFileOrSymlink(p)`, which removes a file or either kind of link and refuses a directory. |
| `Directory.Delete(p, recursive: true)` | **Ext** `dir.DeleteTree(p)` | Never follows a link out of the tree; it removes the link. |
| No equivalent: removing the directory a handle is open on, cap-std's `Dir::remove_open_dir` and `remove_open_dir_all` | **Ext** `dir.DeleteTreeContents()`, then `parent.DeleteDir(name)` | Not provided. POSIX cannot remove a directory through its own handle, only by its name in its parent, so a safe form needs the parent's `Dir` and the name, and that is `DeleteDir` or **Ext** `DeleteTree` on the parent. |
| `Directory.Move(a, b)` | `dir.Rename(a, dir, b)` | |
| `Directory.EnumerateFileSystemEntries(p)`, `GetFileSystemEntries` | `dir.OpenDir(p).EnumerateEntries()` | Yields `DirEntry` values, which carry the name, kind and `FileId` and open what they name directly. |
| `Directory.EnumerateFiles(p)`, `GetFiles` | `EnumerateEntries().Where(e => e.Type == CapFileType.File)` | |
| `Directory.EnumerateDirectories(p)`, `GetDirectories` | `EnumerateEntries().Where(e => e.Type == CapFileType.Directory)` | |
| `Directory.EnumerateFiles(p, "*.json")` | **Ext** `dir.Glob("*.json")` | |
| `Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)` | **Ext** `dir.Walk()` | `WalkEntry.Depth` and `.Directory` stand in for the relative path. |
| `Directory.GetLastWriteTimeUtc(p)` etc. | `dir.GetMetadata(p)` | As for files. |
| `Directory.SetLastWriteTimeUtc(p, t)` etc. | `dir.SetTimes(p, lastWrite: CapFileTime.At(t))` | As for files. On an open directory, `dir.SetTimes(...)`. |
| `Directory.CreateSymbolicLink(p, target)` | `dir.CreateDirSymlink(p, target)` | |
| `Directory.GetCurrentDirectory()`, `SetCurrentDirectory` | none, by design | The working directory is process-wide state that any code can change. Pass a `Dir`. |
| `Directory.GetParent(p)`, `DirectoryInfo.Parent` | none, by design | A handle confers nothing above itself. Keep the parent's `Dir` if you need it. |
| `Directory.GetDirectoryRoot`, `GetLogicalDrives` | none | |

## Handles from elsewhere

| From | Replacement | Notes |
|---|---|---|
| A directory descriptor or handle received from outside: socket activation, a parent process, `SCM_RIGHTS`, a WASI preopen | `Dir.FromHandle(handle, AmbientAuthority.Acquire())` | cap-std's `Dir::from_std_file`. Takes the ambient token, since where the handle came from is outside the capability graph. The directory is opened again through the handle, so the result owns an open object of its own, and the caller keeps and may close the handle it passed. A handle that is not a directory is refused. |

## Overloads not provided

- **`ReadOnlySpan<char>` paths.** Every member takes a `string`. A caller slicing names out of
  something larger, such as archive entries, makes a string of each; the resolution that
  follows costs far more than the copy.
- **`FileStreamOptions`.** `dir.OpenFile` takes the same settings as separate arguments:
  `Mode`, `Access`, `Share`, `Options` and `PreallocationSize` map one to one, and a stream's
  `BufferSize` is given to `CapFile.AsStream(bufferSize: ...)`.

## `Path`

| `System.IO` | Replacement | Notes |
|---|---|---|
| `Path.Combine(root, p)`, `Path.Join` | `dir.OpenDir(a)` and then a name within it | Or a relative path with `/` in it, when every part is yours. |
| `Path.GetFullPath(p)` | none | It is what the README shows being walked past. See [Why there is no `Dir.FullName`](no-full-name.md). |
| `Path.GetTempPath()` | `CapTempDir.New(AmbientAuthority.Acquire())` | A private scratch directory beneath it, removed on disposal. |
| `Path.GetTempFileName()` | `CapTempFile.New(dir)`, or `CapTempFile.NewAnonymous(dir)` for a file with no name at all | Created owner-only (`0600`) on Unix, as `GetTempFileName` does, whatever `dir` is; on Windows it takes the permissions `dir` hands down. |
| `Path.GetFileName`, `GetExtension`, `GetFileNameWithoutExtension`, `ChangeExtension` | unchanged | String manipulation of a name is harmless. Deciding *containment* from strings is what is not. |
| `Environment.GetFolderPath(SpecialFolder.ApplicationData)` and friends | `Cap.Directories.ProjectDirs` | Configuration, data, cache, state and runtime directories as `Dir` handles; see [directories.md](directories.md). |

## `FileSystemWatcher`, `DriveInfo`, `FileSystemInfo`

No equivalent. A watcher reports changes by path, and a path cannot be turned back into
authority without the ambient lookup this library exists to remove.

## Streams

`CapFile.AsStream()` returns a `Stream` over the same open file, so everything that takes a
`Stream` — `StreamReader`, `JsonSerializer`, `ZipArchive`, `HttpContent` — works unchanged.
For a file on disk it is a `FileStream`, and code that needs a member only `FileStream` has,
such as `Name` or `Lock`, can cast to it. The declared type is `Stream` because a file on a
filesystem held in memory has no operating-system handle to build a `FileStream` over, and
code written against `Stream` works against both.

```csharp
using CapFile file = data.OpenFile("events.log");
using StreamReader reader = new(file.AsStream());

while (reader.ReadLine() is { } line)
{
    // ...
}
```

By default the stream leaves the handle open when disposed (`leaveOpen: true`), so dispose
the `CapFile` as well, as above.

## Exceptions

`FileNotFoundException`, `DirectoryNotFoundException`, `UnauthorizedAccessException` and
`IOException` mean what they mean in `System.IO`, so existing handlers keep working.
`SandboxEscapeException`, an `IOException`, is new: the path would have led outside the
`Dir`. Code that told failures apart by `HResult` or by the message should use
`CapIOException.KindOf(exception)` instead: it gives the same `CapErrorKind` on every
platform. See [getting-started.md](getting-started.md#what-refusals-look-like).
