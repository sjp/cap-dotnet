# The convenience layer

`Cap.Fs.Ext` is the ergonomic half of the filesystem API: publishing a file atomically,
walking a tree, removing one, copying one, and finding names by pattern. It is a separate
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

A symbolic link at the target name is replaced, not followed: the move acts on the name, so
the link is swapped for the new file and whatever it pointed at — another file in the tree,
something outside it, or nothing — is neither written nor removed. On Windows a link to a
directory, whether a directory symbolic link or a junction, is a directory entry, and the
filesystem will not move a file over one. There the publish fails and leaves the link and
its target as they were. Removing the link first would leave a moment when the name holds
nothing, which the operation promises never to do. The failure is a `CapIOException` whose
`Kind` is `SymbolicLink` rather than an access-denied error, so a caller can tell a link in
the way from a permissions problem. `Dir.Rename` with `replaceExisting` reports the same case
the same way.

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

`Walk()` yields every entry beneath a handle, parents before their children, by descending
through handles: each directory is enumerated through the handle opened from the one above
it. `WalkAsync()` is the same walk with the reading done on a thread-pool thread.

```csharp
foreach (WalkEntry entry in root.Walk(new WalkOptions { SkipHidden = true }))
{
    Console.WriteLine($"{entry.Depth} {entry.Name} {entry.Type}");
}
```

| Option | Default | Effect |
|---|---|---|
| `MaxDepth` | 256 | How far below the start the walk descends. A deeper tree stops the walk with a failure rather than being quietly cut short. |
| `FollowSymlinks` | off | Whether a link naming a directory is entered. Off, every descent is an open that refuses a link, so a link is not entered even when the directory read called it a directory or could not say what it was; the directories entered keep the starting handle's policy. On, it cannot widen that policy: a handle that refuses links keeps refusing them. |
| `SkipHidden` | off | Leaves out names beginning with a dot, and on Windows anything carrying the hidden attribute. A skipped directory is not entered. |

The walk keeps its own stack rather than calling itself, so a tree built to be deep ends as a
refusal rather than as a stack overflow — which cannot be caught and takes the process with
it. Turning on `FollowSymlinks` also turns on a cycle check: a link pointing at a directory
above it makes an infinite tree out of a finite filesystem, so the identity of every directory
on the way down is remembered and one already on that path is reported but not entered again.

## Removing a tree

`DeleteTree(path)` removes a directory and everything inside it; `DeleteTreeContents()`
empties the directory a handle refers to and leaves the directory. Both descend by handle and
unlink by name at each level.

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

## Copying a tree

`source.CopyTo(destination, options)` copies the contents of one handle's directory into
another's, handle to handle. Both ends are capabilities, which is the correct reading of what
a copy is: it joins two places together, so it needs authority over both.

The two handles may be on different backends, for example a tree on disk and one held in
memory for a test. Everything is read through the source handle and written through the
destination handle, so neither backend is ever handed the other's handle. See
[backends.md](backends.md#handles-on-different-backends).

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
  A value recorded by one platform is never translated into the other's. A link's own
  permissions are never carried across on any platform, because setting them would mean
  following the link.
- `PreserveTimes` (off): each copied file, directory and recreated link is given the source's
  last-access and last-write times. A link's own times are set, not its target's. A
  directory's are set after everything inside it has been copied, since adding entries moves
  its last-write time on. Creation times are not carried, and the directory the copy writes
  into keeps its own.
- `MaxDepth` (256): as for a walk.

A destination inside the source is refused, noticed by identity rather than by comparing
names. Left to run it would copy what it had just written, and then copy that.

**Windows: an alternate data stream is not copied.** A file with one arrives at the
destination holding only its main contents, silently, because there is no portable way to
carry it and no way to report it that is not noise for the trees that do not have any.

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

There is no escape character, so a name containing one of these cannot be matched literally —
on Windows every plausible choice for one, the backslash above all, already divides one level
from the next. Use the walk and a condition in code for an awkward name.

Two differences from a shell are worth knowing. Case is compared exactly unless
`GlobPattern.Parse(pattern, ignoreCase: true)` says otherwise, because whether a filesystem
folds case is a property of how it was made and mounted rather than of the platform. And `*`
matches a name beginning with a dot like any other: the names come from a directory read
rather than from a command line, and `WalkOptions.SkipHidden` is where hidden entries are left
out, including the directories a search would otherwise descend into.

A pattern is relative, like every other name this library takes. One that begins at a root, or
that contains `..`, is refused when it is parsed: a pattern describes names beneath a
directory, and a piece that climbed would describe names beside it.

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
- **A copy can only preserve permissions onto a `Dir` or a `CapFile`.** No interface member
  writes permissions, so `PreservePermissions` fails the copy with a `CapIOException` of kind
  `NotSupported` when the destination hands back anything else.
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
which the three are consistent with each other; to ask about a target, open it and ask the
handle.

An answer describes an instant that has already passed. Code that asks one of these in order
to decide which operation to attempt has written the check-then-act race this library exists
to remove — the way to find out whether an open will succeed is to attempt the open. These are
for reporting and for display.
