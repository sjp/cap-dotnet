# Questions people ask

Short answers to the questions that come up most, each pointing at the page that has the
whole of it.

## Why is there no `Dir.FullName`?

A `Dir` is an open handle on a directory, not a name for one. A path recorded when it was
opened can stop being true the moment after, and code that reads it back usually rebuilds a
path from it, which brings back the bug the handle was there to prevent.
[Why there is no `Dir.FullName`](no-full-name.md) has the full answer, along with what to use
for logging, display and handing a location to another process.

## Why can't I create, remove or rename `.`?

A path made only of `.` and separators (`.`, `./`, `./.`) names the handle's own directory.
`Exists` is true for it, and it can be opened, which gives a copy of the handle, and
described. What it lacks is a name in a parent the handle can reach, so every operation that
acts on a name refuses it with a `CapIOException` of kind `InvalidArgument`. A path that ends
in `..` and climbs back to the handle's directory is treated the same way. The empty string is
a different case and is an `ArgumentException`. See
[How cap-dotnet reads a path](paths.md#-is-walked-not-collapsed).

## Does `..` work?

Yes, as long as it stays inside the handle. A `..` is never collapsed as text, because whether
`a/..` means the directory you started in depends on whether `a` is a symbolic link, and only
the filesystem knows that. The resolver takes the step against a real directory handle and
checks where it landed. `nested/../file` opens `file`. A `..` taken at the handle's own
directory is refused with `SandboxEscapeException`, even when the rest of the path would lead
back inside. So `../dir/file` is refused although it might name somewhere the handle could
reach. See [How cap-dotnet reads a path](paths.md#-is-walked-not-collapsed).

## Which backend am I on, and why does macOS never get the kernel-confined one?

Linux 5.6 and later resolve a whole path in one `openat2(RESOLVE_BENEATH)` call, which leaves
no window between components. Older kernels, kernels where seccomp blocks that call, and all
of macOS take the component-by-component walk. Windows takes its own handle-relative open.
macOS has no open that confines resolution beneath a directory, so the walk is the only way
there. The walk keeps resolution inside the tree, but someone who can write inside the tree can
still race it ([threat model §6.1](threat-model.md#61-the-fallback-resolver-narrows-toctou-it-does-not-close-it)).
[Resolution backends](backends.md) explains how to find out at run time which one you have,
and [Platform differences](platforms.md#macos) covers the rest of what differs on macOS.

## Why do the tests refuse to run as root?

Many of the containment tests check that something was refused. A process that can bypass
file permissions passes those for the wrong reason, because the refusal it expected never had
to happen. So every test assembly checks at startup that it lacks that power, and fails if it
has it. On Unix that means not running as root. On Windows an administrator token is fine, and
is needed to create the symbolic links the suite attacks. What must not be enabled is the
backup, restore or take-ownership privilege. See [README.md](../README.md#building).

## Where is the `Cap.Primitives` package?

There isn't one. `Cap.Primitives.dll` holds the few types every package shares, such as
`AmbientAuthority`, `CapPath` and `SymlinkPolicy`, along with the resolution code. It ships
inside `Cap.Std`, and every other package depends on `Cap.Std`, so it arrives whichever
package you install, with the analyzer alongside it. See [Releasing](releasing.md).

## I referenced `Cap.Std`, so why does `File.ReadAllText` still compile?

The rules that forbid whole families of framework APIs (`File`, sockets, the clock, entropy)
are off until a project turns them on. Adding a package reference shouldn't break a build that
uses `File` for its own reasons, and projects usually adopt capabilities one component at a
time. To make ambient `System.IO` a build error, mark the assembly strict:

```csharp
[assembly: Cap.Primitives.CapabilityStrict]
```

You can also turn individual rules on in `.editorconfig`. See
[The analyzer](analyzers.md#why-the-ambient-rules-start-off).
