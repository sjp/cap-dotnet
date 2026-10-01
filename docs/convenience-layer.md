# The convenience layer

`Cap.Fs.Ext` is the ergonomic half of the filesystem API: publishing a file atomically,
walking a tree, removing one, copying one or a single file, and finding names by pattern. It is a separate
assembly on purpose. Every one of these is built entirely out of the core surface — an open,
an enumeration, a rename, an unlink — so the security-critical code stays small enough to
read, and none of the convenience needs to be trusted to keep the containment promise.

Everything here is an extension method on `IDir`, which `Dir` implements. A
`using Cap.Fs.Ext;` brings the whole set into view on a handle you already hold, and on a
component that takes the interface so that a test can hand it something else. See
[Handles that are not a `Dir`](#handles-that-are-not-a-dir) for what changes then.

```csharp
using Cap.Std;
using Cap.Fs.Ext;

using Dir root = Dir.Open("/srv/app/data", AmbientAuthority.Acquire());

root.WriteAllTextAtomic("state.json", json);

foreach (WalkEntry entry in root.Glob("logs/**/*.txt"))
{
    using ICapFile file = entry.OpenFile();
    // ...
}
```

## Nothing here returns a path

A walk, a pattern search and a copy are the three operations most likely to hand back a
string, because that is what the path-based libraries return and what callers expect. They
return a `WalkEntry` instead: a single name, the handle of the directory it was found in, and
how deep it sits.

That is not tidiness. A path handed back would be resolved again by whatever received it,
with the process's ambient authority and none of the confinement the walk was performed
under — so the one artefact a sandboxed operation must not produce is a string that names its
result. The rule is enforced rather than merely intended: a test scans the assembly's own
source and fails if any literal in it contains a path separator, because a path cannot be
assembled without one.

The handle on a `WalkEntry` belongs to the walk. It is open while the walk is inside that
directory and closed when the walk moves on, so an entry is usable during the iteration step
that produced it and not afterwards. To keep something, open it — `entry.OpenFile()`,
`entry.OpenDir()` — or take a copy of the directory handle with `Clone()`, while the entry is
current.

A walk can start from any `IDir`, so `WalkEntry` gives its handles as the interfaces:
`Directory` is an `IDir`, `Entry` an `IDirEntry`, and `OpenDir()` and `OpenFile()` return an
`IDir` and an `ICapFile`. A walk that starts from a `Dir` reaches every level through `Dir`
handles, so those are a `Dir`, a `DirEntry` and a `CapFile` underneath, and a caller that needs
a member only the concrete type has can cast. Reading `Entry` boxes the entry; `Name`, `Type`
and the open and describe members on `WalkEntry` do not, and are the ones to use in a loop over
a large tree.

## Atomic writes

`WriteAllBytesAtomic` and `WriteAllTextAtomic` write the contents to an unguessable scratch
name in the directory the file will end up in, then move that name onto the target. A reader
opening the name at any moment sees the whole of the old contents or the whole of the new
ones — never a prefix, and never a moment in which the name resolves to nothing.

The scratch file is always made in the destination's own directory. Writing it to the
system's temporary location instead is the usual mistake: the two can be on different
filesystems, the move then fails, and the repair everybody reaches for is a copy — which is
not atomic, and was the point.

When the move fails — a directory holding the name is the usual reason — the scratch file is
removed and the exception names the target, keeping the failure's kind. The scratch name is
never quoted: the caller did not choose it, and it is gone by the time the exception arrives.
The same holds for a copy that replaces a file.

A symbolic link at the target name is replaced, not followed: the move acts on the name, so
the link is swapped for the new file and whatever it pointed at — another file in the tree,
something outside it, or nothing — is neither written nor removed. That includes a link to
a directory on Windows, whether a directory symbolic link or a junction: it is a directory
entry there, but the replacing rename used treats it as a name and replaces it like any
other link.

On a Windows version whose rename lacks that replacing form, the filesystem will not move a
file over such a link. There the publish fails and leaves the link and its target as they
were. Removing the link first would leave a moment when the name holds nothing, which the
operation promises never to do. The failure is a `CapIOException` whose `Kind` is
`SymbolicLink` rather than an access-denied error, so a caller can tell a link in the way
from a permissions problem. `Dir.Rename` with `replaceExisting` reports the same case the
same way.

### Streaming the contents

When the contents are produced a piece at a time — a serialiser, a response body, an archive
being built — `OpenAtomicWrite` publishes them without holding them all in memory first. It
resolves the path and claims the scratch name beside the target, then hands back an
`AtomicFile` to write through. `Commit()` publishes what was written. Disposing the
`AtomicFile` without committing removes the scratch name and leaves the target as it was.

```csharp
using (AtomicFile publish = root.OpenAtomicWrite("state.json"))
{
    await JsonSerializer.SerializeAsync(publish.Stream, state, cancellationToken);
    publish.Commit();   // write out, commit the file, move it onto the name, commit the directory
}
```

`Stream` is created the first time it is asked for and belongs to the `AtomicFile`. The
commit flushes and closes it before the move, so the caller does not have to. If a writer
closes it first, that is fine too. `File` gives the same scratch file as an `ICapFile`, for
writes at an offset. Everything else is what `WriteAllBytesAtomic` does, since that method is
this one with the contents written in a single call. That covers the durability settings,
link replacement, permission carrying and cleanup.

- **One attempt.** After a commit, successful or not, the `AtomicFile` is spent: a second
  `Commit` throws `InvalidOperationException`, and `Stream` and `File` throw
  `ObjectDisposedException`. A failed commit leaves the name as it was.
- **Async.** Pass `asynchronous: true` to open the scratch file for asynchronous writes.
  `CommitAsync(cancellationToken)` flushes the stream asynchronously. The file commit, the
  move and the directory commit still run on the calling thread, as they do for
  `WriteAllBytesAtomicAsync`. Cancellation is checked up to the move and not after it.
- **Permissions** are read from the file holding the name when the `AtomicFile` is opened,
  not when it is committed.
- **Threads.** An `AtomicFile` belongs to one caller at a time, like any stream.

### Permissions, ownership and hard links

The published file is a new object, not the old one rewritten. By default it is given the
permissions of the file it replaces: the mode bits on Linux and macOS, the attribute flags on
Windows. A file created `0600` stays `0600` when it is republished. On a `Dir` on Linux and
macOS, the new contents are written into a scratch file only its owner can read and given
the replaced file's mode afterwards, so a private file's next contents are never readable by
anyone else, even under the scratch name. Nothing is carried when the name holds nothing,
holds something other than a file, or holds a symbolic link (the link is what gets replaced,
so the mode of whatever it points at is ignored).

Some things are not carried:

- **Ownership.** The new file belongs to the account that wrote it. Only a privileged process
  may give a file away.
- **Other hard links.** They still name the old file, which keeps the old contents.
- **ACLs, extended attributes and times.**
- **The Windows read-only flag.** Windows refuses to rename a file over a read-only one, so a
  publish over a read-only file fails and leaves it as it was, whatever this setting says.
  Clear the flag first to replace the file. Unix has no such rule: a `0444` file is replaced,
  and the new file is `0444` too.

To give the new file the mode a newly created file gets instead, pass
`new AtomicWriteOptions { PreservePermissions = false }`. `AtomicWriteOptions` also carries
the `Durability`, and the overloads that take a `Durability` are the same as passing it with
permissions preserved.

### Durability

`Durability` decides how far the write is pushed before the call returns. None of the three
changes what a reader can observe on a running machine; the difference appears only when the
machine stops.

| Setting | Contents committed | Name committed | Costs |
|---|---|---|---|
| `None` | no | no | nothing |
| `File` | yes | no | one commit |
| `FileAndDirectory` *(default)* | yes | yes | two commits |

The default is the correct-but-slower one. A caller writing a cache, a rendered artefact or
an extracted archive should say `None` and get the speed; a caller who has not thought about
it gets the answer that does not lose data.

On macOS both commits are full flushes (`F_FULLFSYNC`), carried through the drive's own
cache, so the name is as durable as the contents. A volume that refuses the full flush, such
as some network filesystems, gets plain `fsync` for both.

**Windows does not commit directories.** Doing so needs a volume-level privilege an ordinary
process does not hold, and would stall every other writer on the disk. `FileAndDirectory`
there does what `File` does, and is not refused — refusing would make the safe default
unusable on that platform and push everybody towards the weaker setting everywhere. The
atomicity of the move still holds; it is durability across a power loss, and nothing else,
that is missing.

A caller composing its own durable publish (write, `CapFile.Flush(toDisk: true)`, rename)
finishes with `Dir.Flush(toDisk: true)` on the directory the name is in. It returns false on
Windows for the same reason.

## Walking

`Walk()` yields every entry beneath a handle, parents before their children unless
`ContentsFirst` says otherwise, by descending through handles: each directory is enumerated through the handle opened from the one above
it. `WalkAsync()` is the same walk with the reading done on a thread-pool thread.

```csharp
foreach (WalkEntry entry in root.Walk(new WalkOptions { SkipHidden = true }))
{
    Console.WriteLine($"{entry.Depth} {entry.Name} {entry.Type}");
}
```

| Option | Default | Effect |
|---|---|---|
| `MaxDepth` | 256 | How far below the start the walk descends. Entries at depth `MaxDepth` are reported; an entry at depth `MaxDepth + 1` fails the walk rather than the tree being quietly cut short. An empty directory at the limit is therefore fine, and `MaxDepth = 1` lists what is directly inside the start, failing on the first non-empty directory among it. |
| `FollowSymlinks` | off | Whether a link naming a directory is entered. Off, every descent is an open that refuses a link, so a link is not entered even when the directory read called it a directory or could not say what it was; the directories entered keep the starting handle's policy. On, it cannot widen that policy: a handle that refuses links keeps refusing them. |
| `SkipHidden` | off | Leaves out names beginning with a dot, and on Windows anything carrying the hidden attribute. A skipped directory is not entered. |
| `OnError` | null | Called with the entry and the exception when a directory is there and cannot be opened. Return true to leave it out and carry on, false to fail the walk. Null fails the walk. |
| `MinDepth` | 0 | Entries shallower than this are not yielded, and are still descended into: `MinDepth = 2` leaves out what is directly inside the start and yields everything beneath it. It may not exceed `MaxDepth`. walkdir's `min_depth`. |
| `ContentsFirst` | off | Post-order: a directory the walk enters is yielded once everything inside it has been, the order removing a tree or setting directory times after their contents needs. Its entry's handle (the directory it was found in) is still open when it is yielded. A directory the walk does not enter is yielded where it was read. walkdir's `contents_first`. |
| `Sort` | null | A `Comparison<string>` putting each directory's entries in order by name, `string.CompareOrdinal` for a walk that comes out the same everywhere. Null yields them in the order the filesystem lists them. Sorting reads each directory in full when it is entered, so the walk holds every directory on the way down to the current entry in memory. |

The walk keeps its own stack rather than calling itself, so a tree built to be deep ends as a
refusal rather than as a stack overflow — which cannot be caught and takes the process with
it. Turning on `FollowSymlinks` also turns on a cycle check: a link pointing at a directory
above it makes an infinite tree out of a finite filesystem, so the identity of every directory
on the way down is remembered and one already on that path is reported but not entered again.

Nothing is left out silently. A name that is not a directory to enter is yielded and not
descended into: one that has gone since it was listed, is not a directory, is a link not being
followed, is a chain of links that never arrives, leads out of the subtree, or changed while it
was being opened. A directory that is there and cannot be opened, for want of permission, of
handles or of a working device, fails the walk with `UnauthorizedAccessException` or
`CapIOException`, unless `OnError` says to go on without it. `OnError` covers only that open: a
directory that fails part-way through being read, or a tree deeper than `MaxDepth`, fails the
walk whatever it says. `Glob` behaves the same for the directories it tries to enter, and never
opens one the pattern could not match through.

Removing a tree entry by entry, with each directory after what was inside it:

```csharp
foreach (WalkEntry entry in root.Walk(new WalkOptions { ContentsFirst = true }))
{
    if (entry.Type == CapFileType.Directory) entry.Directory.DeleteDir(entry.Name);
    else entry.Directory.DeleteFile(entry.Name);
}
```

`DeleteTreeContents()` does this for you, and carries on past an entry that will not go.

```csharp
var skipped = new List<string>();
var options = new WalkOptions
{
    OnError = (entry, exception) =>
    {
        skipped.Add(entry.Name);
        return exception is UnauthorizedAccessException; // leave out what we may not read
    },
};
```

## Removing a tree

`DeleteTree(path)` removes a directory and everything inside it; `DeleteTreeContents()`
empties the directory a handle refers to and leaves the directory. Both descend by handle and
unlink by name at each level. `TryDeleteTree(path)` and `TryDeleteTreeContents()` answer false
where the others would throw, for clearing up.

This is the operation sandbox libraries are most often found to have got wrong, and the wrong
version is the obvious one: list the directory, join each name onto its path, delete the
resulting strings. That works until something replaces a directory in the middle with a
symbolic link between the listing and the deletion, at which point the joined path names
somewhere else and the deletion goes there — with the caller's own privileges, on files the
caller never meant to touch.

So the work is done through a handle narrowed to refuse symbolic links, whatever policy the
caller's handle carries. A name at the top that is a link is refused rather than followed; a
directory further down that becomes a link between being listed and being entered is refused
at the open, and unlinking its name removes the link rather than what it points at.

It is not atomic and nothing can make it so. It is many operations, and an entry created while
it runs may or may not be removed. What it guarantees is that every one of those operations
lands inside the subtree the handle covers.

A failure part of the way through does not stop the rest: the first failure is the one
reported, after everything else that could go has gone. One met inside the tree names the entry
it concerns by its place beneath the path given (`'cache/a/b'`), or beneath the handle for
`DeleteTreeContents()`, so it can be found. A directory the process may not open is still
removed when it is empty, as `rm -rf` removes one — that asks nothing of the directory, only of
its parent. One that is not empty stays, and the failure reported is the refusal to open it
(`UnauthorizedAccessException` for a permission) rather than the "not empty" that followed.

Every form takes a `CancellationToken`, looked at before each entry is removed, and
`DeleteTreeAsync`, `TryDeleteTreeAsync`, `DeleteTreeContentsAsync` and
`TryDeleteTreeContentsAsync` do the same work on a
thread-pool thread — no platform here removes a name asynchronously, so what they offer is a
calling thread that is not held and a removal that can be stopped. A cancelled removal throws
`OperationCanceledException`, the `Try` forms included: being told to stop is not a failure to
remove, and answering false would say it was. What had been removed is gone and what had not
is left, as after a failure part of the way through.

```csharp
await root.DeleteTreeAsync("cache", cancellationToken);
```

### Removing one file or link

`RemoveFileOrSymlink(path)` removes a file or a symbolic link, whichever kind of link it is, as
cap-fs-ext's `remove_file_or_symlink` does. The name is removed against the directory holding
it, and a link as the last component is removed as the link: what it points at — a file, a
directory, nothing, somewhere outside — is never reached. A directory is refused
(`CapIOException` with `IsADirectory`) and left, empty or not. Links ahead of the last component
are resolved under the handle's policy as for any path.

Win32 removes a link made to name a directory with `RemoveDirectory` and one made to name a file
with `DeleteFile`. A `Dir` removes either kind as a file on every platform, and for an `IDir`
that keeps the Win32 split a link its file removal refuses is removed as a directory.
`TryRemoveFileOrSymlink` answers false for a name holding nothing, a directory, or a missing or
refused directory above it, and still throws for a path that leads out of the handle.
`RemoveFileOrSymlinkAsync` and `TryRemoveFileOrSymlinkAsync` do the same on a thread-pool
thread.

## Copying a tree

`source.CopyTo(destination, options)` copies the contents of one handle's directory into
another's, handle to handle. Both ends are capabilities, which is the correct reading of what
a copy is: it joins two places together, so it needs authority over both.

The two handles may be on different backends, for example a tree on disk and one held in
memory for a test. Everything is read through the source handle and written through the
destination handle, so neither backend is ever handed the other's handle. See
[backends.md](backends.md#handles-on-different-backends).

`CopyToAsync` is the same copy without holding the calling thread: each directory is read as
`WalkAsync()` reads it, and file contents are moved with `ReadAsync`/`WriteAsync`. Opens,
creations, renames and metadata calls stay synchronous, on whichever thread the copy resumes on.
Both forms take an `IProgress<CopyReport>` and a `CancellationToken`.

```csharp
var progress = new Progress<CopyReport>(r => status.Text = $"{r.Files} files, {r.Bytes} bytes");
CopyReport report = await source.CopyToAsync(destination, options, progress, cancellationToken);
```

Progress is reported after each directory, file and link is made and each entry is skipped, so
the counts never go down and the last report equals the one returned. It is called on the
thread doing the copy; `Progress<T>` posts each report on to the context it was made on.

The token is looked at before each entry and between the pieces a file is copied in: 8 MiB
for a copy made inside the kernel, 64 KiB for one read and written (see below). A clone is one
step and is not interrupted. Cancelled mid-copy, the copy throws `OperationCanceledException` and leaves what a failure
leaves: files already copied stay, no scratch (`cap-*`) file remains, and the file being
written is not published — it is removed from its real name, or under `Overwrite` its scratch
copy is removed and whatever held the name keeps it. The same is true of any other failure
part of the way through a file, so no name in the destination ever holds a partly written
file. Directories already made stay, and under `PreservePermissions` keep the owner-only
permissions they are given while they are filled.

### How the contents are moved

Each file's contents go by the quickest means both ends share, tried in this order:

1. **Shared storage.** The destination is made to share the source's storage, which takes the
   same time whatever the file's length. Linux asks for a reflink (`FICLONE`), which btrfs,
   XFS and the other filesystems that share extents grant. Windows asks for block cloning
   (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`), which ReFS and Dev Drive volumes grant. macOS clones
   with `fclonefileat` on APFS. That call makes a new name rather than filling a file already
   created, so it is used in place of creating the destination, under the same exclusivity,
   and only with `PreservePermissions` on: a clone carries the source's mode, which a copy that
   leaves permissions to the destination must not do. It also carries the source's extended
   attributes, which no other way of copying does. A clone is given the time it was made,
   unless `PreserveTimes` asks for the source's.
2. **A copy inside the kernel.** Linux moves the contents with `copy_file_range`, 8 MiB at a
   time, without bringing them into the process. A filesystem may carry it out by sharing
   extents, or on the server for NFS and SMB.
3. **Reads and writes**, 64 KiB at a time, position by position. Before the first write the
   destination's room is reserved, up to the source's length, so a large file is not claimed
   a write at a time and fragmented (`fallocate` on Linux, `F_PREALLOCATE` on macOS, the
   allocation size on Windows).

A shortcut that fails is never reported. The copy goes on to the next way, and a real fault
such as a full disk or a failing device is met, and reported, by the reads and writes. One the
platform says it does not have at all is not tried again for the rest of the copy. A kernel copy
that reports nothing copied before the end, as one from `/proc` or `/sys` does, is not taken for
the end: the rest is read and written. Both ends have to be on the host's filesystem for the
first two; between a tree on disk and one in memory the contents are always read and written.

`PreserveSparseness` keeps the holes in a sparse file, the ranges the source stores nothing for
and that read as zeroes. Without it, holes are read as zeroes and written as zeroes, so a sparse
source becomes a copy that takes more room. With it, the copy asks the source where its data is
(`SEEK_DATA`/`SEEK_HOLE` on Linux and macOS, `FSCTL_QUERY_ALLOCATED_RANGES` on Windows) and
writes only that, by either of the last two ways. It marks the destination sparse first on
Windows, and gives the destination the source's length, so the holes stay holes, the one at the
end included. A source whose filesystem cannot say where its data is is copied whole. Nothing is
reserved while holes are kept, since a reservation would fill them. A copy that shares storage
keeps the source's holes either way.

`CopyReport.Bytes` counts the bytes each copy reads as: its length, holes included, however the
contents went.

### What happens to each kind

| In the source | Default | `Skip` | `Recreate` |
|---|---|---|---|
| Directory | created in the destination and descended into | — | — |
| Ordinary file | contents copied | — | — |
| Symbolic link | **copy fails** | left out, counted | new link with the same stored target text |
| Named pipe, socket, device node | **copy fails** | left out, counted | refused as a request |
| Anything the filesystem will not classify | **copy fails** | left out, counted | refused as a request |
| Two names for one file (hard link) | two independent files | — | — |

`CopyOptions.Symlinks` and `CopyOptions.OtherKinds` choose the column. The defaults refuse,
because the one thing a copy must not do is put an object of a different kind under the same
name and say nothing: a copy that followed a link would reach outside the tree it was given,
and a copy that read a named pipe would block until something wrote to it.

The kind is taken from a description of the name that does not follow links, and the open
that follows it refuses a link at the name. So a directory or file swapped for a link between
the two is not followed: it is described again and handled in the link row, exactly as if it
had been a link all along. A file whose open finds a named pipe, a socket or a device in its
place is not read, and is handled in that row instead.

A recreated link carries its target text unchanged — not resolved and not rewritten. What the
text means is decided from wherever the new link sits, so a relative target that reached one
place from the source may reach another from the destination. Containment is enforced when
something follows the link, as it is for any other link. A rooted target (`/etc`, and on Windows
also `C:\dir`, `\dir` or a network path) is the exception: no link beneath a handle may store one,
so recreating it stops the copy with `SandboxEscapeException`, before anything already at that
name in the destination is touched.

Hard links become independent files, holding the same bytes and sharing nothing. Preserving
the sharing would produce a destination in which writing one file changes another — a property
the source had and the copy's caller did not ask for.

### The other options

- `Overwrite` (off): a name already taken in the destination stops the copy. With it on, a
  file is replaced and a directory is copied into; a name holding one kind where the source
  has the other still stops the copy. A file is replaced as a name: it is written under a
  scratch name beside the destination and moved over it, so a symbolic link at that name is
  replaced by the file and its target is left alone, rather than being overwritten through
  the link. The new file does not keep the old one's permissions or its other hard links. A
  link where the source has a directory stops the copy, so the copy never descends into a
  directory that a link in the destination chose.
- `PreservePermissions` (off): each copied file and directory is given the source's
  permissions as its platform records them — mode bits on Unix, attribute flags on Windows.
  A value recorded by one platform is never translated into the other's. A directory is
  given its permissions once its contents are copied, so a directory its owner cannot write
  to can still be copied; until then one whose source records mode bits is open to its owner
  alone, as `cp -p` leaves it. A link's own permissions are never carried across on any
  platform, because setting them would mean following the link.
- `PreserveTimes` (off): each copied file, directory and recreated link is given the source's
  last-access and last-write times; a source with no access time leaves the copy's own. A
  link's own times are set, not its target's. A
  directory's are set after everything inside it has been copied, since adding entries moves
  its last-write time on. Creation times are not carried, and the directory the copy writes
  into keeps its own.
- `PreserveSparseness` (off): a hole in a sparse source file stays a hole in the copy, as
  [How the contents are moved](#how-the-contents-are-moved) describes.
- `MaxDepth` (256): as for a walk. A directory at the limit is looked into before its copy
  is made, so an empty one is copied and one with anything in it stops the copy with nothing
  created for it.

A destination inside the source is refused, noticed by identity rather than by comparing
names. Left to run it would copy what it had just written, and then copy that. A destination
that is the source directory itself is refused before anything is read or written. One
strictly inside it is refused when the copy reaches it, so by then the destination already
holds whatever came before: the source's directories on the way down to it, recreated inside
it, and every entry listed ahead of them. That is left in place, as after any other failure
part of the way through, for the caller to remove.

**Windows: an alternate data stream is not copied.** A file with one arrives at the
destination holding only its main contents, silently, because there is no portable way to
carry it and no way to report it that is not noise for the trees that do not have any.

**macOS: extended attributes travel with a clone and with nothing else.** A file cloned on
APFS (with `PreservePermissions` on) arrives with the source's extended attributes, quarantine
flag included. One read and written does not, and neither does a copy on any other platform.

## Copying one file

`source.CopyFile(from, toDir, to, overwrite)` copies one file, as `File.Copy` does, with each
end named by a handle and a path beneath it. It returns the number of bytes copied.

- The source is opened as an existing file is opened, so a symbolic link at `from` is followed
  while it stays beneath `source`, under that handle's policy; what is copied is what it led
  to.
- The destination is created exclusively, so a name already taken is refused. With
  `overwrite: true` the copy is written under a scratch name beside `to` and moved over it,
  as `CopyTo` replaces a file: a link at `to` is replaced, never written through, and a reader
  sees the old file or the whole new one. A directory at `to` is refused either way.
- The copy is given the source's permissions, as `File.Copy` gives it, when both handles are
  on filesystems that record the same kind. Times are not carried; `CopyTo` with
  `PreserveTimes` does that.
- Without `overwrite`, a failure part of the way through the contents leaves the partial file
  at `to`.
- The contents are moved as `CopyTo` moves them, by the quickest means both ends share. Since
  the permissions always travel, a macOS clone is made whenever APFS allows. Holes are written
  as zeroes; `CopyTo` with `PreserveSparseness` keeps them.

The two handles may be on different backends, as for `CopyTo`.

`source.CopyFile(from, toDir, to, options, cancellationToken)` takes a `CopyOptions` instead and
copies the one file as `CopyTo` would copy it, returning a `CopyReport`. It differs from the
`overwrite` form in the direction of `CopyTo`:

- A link at `from` is not followed. It is refused, skipped or made again at `to` with the same
  target text, as `Symlinks` says; the default refuses it. A named pipe, socket or device there
  is refused or skipped as `OtherKinds` says. A directory is refused.
- Permissions, times and holes are carried only when `PreservePermissions`, `PreserveTimes` and
  `PreserveSparseness` ask, and a destination that will not take the permissions fails the
  copy. With the defaults the copy gets the permissions a new file gets.
- A copy that fails or is cancelled part way never leaves a partly written file at `to`.

`Overwrite` replaces a file or link at `to` as the `overwrite` flag does. `MaxDepth` has nothing
to limit.

```csharp
CopyReport report = source.CopyFile(
    "build/app.dll", destination, "app.dll",
    new CopyOptions { Overwrite = true, PreserveTimes = true });
```

## Patterns

`Glob(pattern)` walks the tree with the pattern steering it, and yields the entries whose
names it describes. A directory that no remaining piece of the pattern could match through is
not read at all, so an anchored pattern costs the directories on the way and nothing for the
rest of the tree.

| Piece | Matches |
|---|---|
| `?` | any one character |
| `*` | any run of characters, including none, within a single name |
| `**` | as a whole piece: any number of levels, including none |
| `[abc]` | any one of the characters listed |
| `[a-z]` | any one character in the range |
| `[!abc]`, `[^abc]` | any one character not listed |
| `[]abc]`, `[!]abc]` | a `]` first in the list, after any `!` or `^`, is a member rather than the end |

There is no escape character — on Windows every plausible choice for one, the backslash above
all, already divides one level from the next. A special character is matched literally by
making it the only member of a class instead: `[*]`, `[?]`, `[[]` and `[]]`. A `[` with no `]`
to close it is the literal characters it is made of. Use the walk and a condition in code for a
name no pattern describes comfortably.

Two differences from a shell are worth knowing. Case is compared exactly unless the search says
otherwise: `dir.Glob(pattern, ignoreCase: true)` (and the matching `GlobAsync` overload), or
`GlobPattern.Parse(pattern, ignoreCase: true)` for a pattern parsed once and reused. Whether a
filesystem folds case is a property of how it was made and mounted rather than of the platform.
`GlobPattern.Parse` divides the pattern into levels by the running machine's rules, having no
handle to ask; the `Glob` and `GlobAsync` overloads taking text divide it as the handle reads a
path, which differs for a filesystem held in memory under another platform's rules. And `*`
matches a name beginning with a dot like any other: the names come from a directory read
rather than from a command line, and `WalkOptions.SkipHidden` is where hidden entries are left
out, including the directories a search would otherwise descend into.

A pattern is relative, like every other name this library takes. One that begins at a root, or
that contains `..`, is refused when it is parsed: a pattern describes names beneath a
directory, and a piece that climbed would describe names beside it. Under Windows rules every
rooted form counts — `C:\logs\*`, the drive-relative `C:logs`, `\\server\share\*` and
`\\?\C:\x` as well as a leading separator.

`GlobAsync(pattern)` is the same search with the reading done as `WalkAsync()` does it, and a
`CancellationToken` that stops it between batches of entries. The pattern is parsed when it is
called, so an unusable one is refused there rather than at the first `MoveNextAsync`.

## Handles that are not a `Dir`

Each helper accepts any `IDir`. Given a `Dir`, it works as the rest of this page describes,
including the parts that reach below the public surface: tree removal through the raw handle,
and the directory commit at the end of an atomic write. Given anything else, such as a
wrapper that logs or meters what a component does or a stub in a unit test, it does the same
work through the interface's own members. A walk, a copy and a tree removal still descend one
handle at a time and use only single names against each handle, so a wrapper sees the same
shape of calls a `Dir` would.

A few things cannot be done or known through the interface, and change as follows:

- **Tree removal cannot clear the Windows read-only mark.** A `Dir` clears it and retries when
  the mark refuses a removal. Through the interface, the entry is left in place and the
  implementation's own failure is reported, after the rest of the tree has been removed.
  Failures are the exceptions the implementation threw, the first of them rethrown.
- **The directory commit is `IDir.Flush(toDisk: true)`.** A false answer from it is accepted,
  as it is on Windows.
- **Permissions are written through the interface.** `PreservePermissions` calls
  `IDir.SetPermissions` and `ICapFile.SetPermissions` on whatever the destination hands back,
  and a failure is whatever that implementation throws rather than an exception naming the
  entry.
- **A copy into its own subtree may not be noticed early.** The check compares identities, and
  identities from two handles are comparable only when both are `Dir` handles on one
  filesystem, or both report a backend on the host's own filesystem. Otherwise the copy stops
  when it reaches `MaxDepth`.
- **Paths, patterns and the hidden attribute follow the machine.** A `Dir` says which path
  syntax its filesystem uses, and a filesystem held in memory may use Windows rules anywhere.
  The interface does not say, so for anything else the running machine's syntax is assumed.

None of this adds a guarantee. The helpers are confined because the handle they work through
is. Given a `Dir`, or something that forwards to one, they stay inside it. Given an
implementation that resolves names some other way, they do whatever it does. See the
[threat model](threat-model.md#57-the-handle-interfaces-carry-no-guarantee).

## Asking what a name holds

`IsDir`, `IsFile` and `IsSymlink` answer without throwing when the name holds nothing.

Each asks about the name and never about what the name points at, so a link aimed at a
directory answers `false` to `IsDir` and `true` to `IsSymlink`. That is the only rule under
which the three are consistent with each other.

`IsDir(path, followLink: true)` and `IsFile(path, followLink: true)` ask about the target
instead, when the caller says so by name. The link is followed under the handle's policy and
only while it stays beneath the handle, so a link leading out, one the policy refuses, a chain
that never arrives and a link to nothing all answer `false`.

An answer describes an instant that has already passed. Code that asks one of these in order
to decide which operation to attempt has written the check-then-act race this library exists
to remove — the way to find out whether an open will succeed is to attempt the open. These are
for reporting and for display.
