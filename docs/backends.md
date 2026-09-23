# Resolution backends and how to select them

cap-dotnet resolves a path against a `Dir` handle through one of three backends. Which one
runs changes the security properties — specifically, whether the residual TOCTOU window of
[threat model §6.1](threat-model.md#61-the-fallback-resolver-narrows-toctou-it-does-not-close-it)
exists at all — so it is observable, selectable, and tested in CI rather than being an
implementation detail.

| Backend | Platform | TOCTOU |
|---|---|---|
| `openat2(RESOLVE_BENEATH)` | Linux ≥ 5.6, when not blocked by seccomp | none — resolution is kernel-atomic |
| Component-by-component walk | Linux fallback, macOS | narrowed, not eliminated |
| Native relative open with a `RootDirectory` handle | Windows | narrowed, not eliminated |

On the kernel-atomic backend a path costs exactly one syscall however many names it has,
which is what leaves no window between components. The other two spend one open per name.

## Forcing the fallback

`openat2` is not always available: it needs Linux 5.6, and a seccomp filter can make it
return `EPERM` on a kernel that has it (Docker's default profile did exactly this for some
time after the syscall was added). The fallback is therefore not a legacy path — it is the
path a significant share of real deployments take, and it must be exercised as a first-class
configuration.

Two knobs select it, checked in this order:

1. **AppContext switch** `Cap.Primitives.DisableOpenat2`

   ```xml
   <!-- in the consuming application's .csproj -->
   <ItemGroup>
     <RuntimeHostConfigurationOption Include="Cap.Primitives.DisableOpenat2" Value="true" />
   </ItemGroup>
   ```

   or, before the first `Dir` is opened:

   ```csharp
   AppContext.SetSwitch("Cap.Primitives.DisableOpenat2", true);
   ```

2. **Environment variable** `CAPDOTNET_DISABLE_OPENAT2=1`

   This exists mainly so CI can set it per job, and so an operator can turn `openat2` off
   without a rebuild if a kernel regresses.

A switch, not a `#if`. The binary that is tested with the fallback forced is bit-identical
to the one users ship, which is the only way the fallback leg in CI means anything.

The escape corpus does not depend on either knob to reach the walk. It sets the switch only
while it builds one instance of the Linux backend, installs that instance for the length of
each case, and runs every case on it as well as on the instance the kernel would have chosen.
So a single run on a Linux machine covers both backends, and the walk it covers is the shipped
code in its shipped fallback configuration rather than a test double.

## Distrusting the kind a directory read reports

Reading a directory normally answers what each entry is as part of the same call, so listing
a directory costs no lookups at all. A filesystem is entitled not to answer — several do not,
and those entries are looked up individually, which is why enumerating such a filesystem is
markedly slower rather than merely different.

A filesystem that answers *wrongly* is also possible, and has happened in filesystems
implemented outside the kernel. Two knobs make the reader disregard the answer and look every
entry up instead, on Linux and macOS:

1. **AppContext switch** `Cap.Primitives.AlwaysLookUpEntryKind`
2. **Environment variable** `CAPDOTNET_ALWAYS_LOOK_UP_ENTRY_KIND=1`

Read once when an enumeration begins, so a directory being read does not change its mind
part of the way through, and free when unset. Neither does anything on Windows, where the
kind arrives with the entry and there is no separate lookup to force.

The lookup never follows a link, so an entry holding one is still reported as a link and not
as whatever it leads to. An entry removed between the read and the lookup is reported as
being of no known kind, which is the only honest answer left: the alternative is to say what
it used to be.

## What the component-by-component walk does

The walk is one loop. Every operation that names something beneath a directory handle goes
through it, and they differ only in what happens to the last component — a deliberate
constraint, because two resolution loops eventually disagree about what `..` means and the
disagreement is only ever found by whoever exploits it.

**Every step refuses to follow a link.** Not as a default but as a rule with no exception:
an intermediate open that followed one would have handed the containment decision to the
kernel, which does not know where the sandbox root is. Links are read explicitly instead,
and their targets are walked by the same loop under the same root test, so a component a
link introduced is checked exactly like one the caller wrote.

**Moving up steps back through a handle.** The walk keeps every directory it has descended
through open, and `..` closes the top one. It never asks the kernel to resolve a parent,
because the parent of an open directory is whatever a concurrent rename last made it. A
`..` at the root is refused rather than clamped to the root: a caller that asked to go above
it has been handed a path that tries to escape, and quietly resolving it to something else
would hide that while leaving the path working for whoever supplied it.

**A name that changes under the walk is looked at again.** Finding that a name is a link
and reading what it says are two calls, and whatever can write in the directory can put
something else under the name between them. The read then has nothing to read, which says
something about the tree at that instant and nothing about the caller's path, so the walk
goes back and opens the name afresh rather than reporting it. The kernel-atomic backend does
the same with a lost race of its own: it abandons the attempt and asks to be called again.
Each second look is charged to the link budget below, so a name swapped in a loop ends the
resolution with the refusal a chain of links too long to follow gets, rather than holding
the caller forever. The second look decides nothing about containment — it goes through the
same open that refuses links as the first.

**Nothing is collapsed as text.** `link/..` resolves to the parent of the link's *target*,
which is where the kernel would land and is not where string arithmetic would.

The policy it applies to links is the same one the kernel-atomic backend is asked for, so
that the two are observably identical and a forced-fallback run is testing the same
semantics:

| Case | Behaviour |
|---|---|
| Relative link whose target stays inside the root | Followed |
| Relative link whose target climbs out through `..` | Refused as an escape |
| Absolute link target, in any of its spellings | Refused as an escape |
| Chain within budget, all inside | Followed |
| Chain exceeding the budget, or a cycle | Refused as a link loop |
| Reparse point whose tag is not a filesystem link | Refused, never read as a link |
| A second filesystem mounted inside the root | Crossed, unless the caller asked not to cross one. A Windows junction is not this case: it is a reparse point, and is refused |
| Any link at all, where the handle's policy refuses them | Refused as a link, without being read — so which way it pointed is never learned |

The last row is the one caller-visible choice. It is fixed when a sandbox root is opened and
carried by every handle derived from it; it can be tightened when a handle is handed on and
never loosened. It does not reach containment — the refusals above hold under both settings —
and it does not reach the last component of a path, because whether an operation acts on a
link or on what the link points at is a property of the operation. Removing a name removes
the name, and reading a link reads it, however links met on the way are treated.

An absolute target is refused rather than re-read as though the sandbox root were the
filesystem root. The re-reading is defensible — it is what `chroot` does — but it silently
changes which file a link means, and nothing in the result tells a caller which reading they
got. It is also unavailable in isolation on Linux: asking the kernel to re-anchor absolute
targets also makes it clamp an upward step at the root rather than refuse it, which would
turn a link trying to climb out from a reported refusal into a successful open of a different
file, and would leave this backend and the walk disagreeing about the same tree.

Two limits bound the work a single path can cost, and they are separate because they bound
different things:

- **Forty symbolic links per resolution.** The number Linux applies to its own resolution,
  chosen so that a tree resolving on one backend and not on another is not a difference
  anyone discovers in production.
- **Two hundred and fifty-six directory levels.** The walk holds a handle per level, so
  depth spends descriptors. Far deeper than any real tree, and well under the allowance a
  process is normally given, so one crafted path cannot exhaust it and break opens
  elsewhere in the program.

Every exit from the walk closes the handles it opened, including the failing exits — which
are the ones a hostile path is trying to take. That is asserted rather than assumed, from
the kernel's own descriptor list as well as from the library's bookkeeping.

## Observability

Silently taking a weaker backend is, per [SECURITY.md](../SECURITY.md), a vulnerability in
its own right. So:

- The capability probe runs once at startup and caches its result, including **both**
  `ENOSYS` (kernel too old) and `EPERM` (seccomp). Retrying per call would be a syscall
  storm in exactly the deployments that can least afford it.
- The selected backend is reportable at runtime, and the CI fallback leg asserts the
  `openat2` call count is zero. A forced-fallback job that quietly keeps using `openat2`
  tests nothing at all.
- CI also runs the suite on a host where the kernel itself refuses the syscall — once
  answering `EPERM`, once `ENOSYS` — because the two switches above are answered before the
  kernel is ever asked. They exercise the decision to stand down; only a refusal exercises
  the probe that reads one.

> **TODO:** name the public accessor for the active backend and the call counter here
> once they exist.

## What the Windows walk does differently

Windows has no confined open, so it follows the same component-at-a-time model with the same
guarantee and the same residual race. Six things about it are specific to the platform, and
each is a decision rather than an implementation detail.

**Names go to the filesystem as counted strings, never as paths.** The familiar Win32 layer
rewrites what it is handed on the way down: it strips trailing dots and spaces, recognises a
reserved device name wherever it occurs including after an extension, and re-parses prefixes.
A name that has been validated is therefore not the name Win32 would open, and that gap is
how every published escape from a Windows directory sandbox has worked. The native API takes
a counted string plus a directory handle to resolve it against, and does none of the
rewriting. Every open also asks for a reparse point itself rather than its target, so that
whether to follow a link is decided here rather than by the object manager — which does not
know where the sandbox root is.

**Exactly one call hands Win32 a path**, the one that opens the very first directory from an
ordinary path string. That step is ambient by definition, and it needs precisely the
drive-letter and working-directory handling the rest of the backend avoids. It is also the
only place a name can reach something that is not a file at all — the path syntax reaches
serial ports, volumes and pipes as readily as directories, and several of the names that do so
look like ordinary filenames. So the handle it produces is interrogated before it is handed
back, and refused unless it is a directory on a filesystem. What was opened is a fact; what a
path was going to open is a guess.

Other Win32 calls are used where they take a handle rather than a path — reading a reparse
point, asking what kind of object a handle refers to. Those are safe for the same reason the
path-taking one is not: there is no string for the layer to rewrite.

**Every handle is asked whether it is on a filesystem**, and dropped if it is not. Refusing
the reserved device names while they are still strings is the first defence against them, and
it is a blocklist — a shape that ages badly, because the reserved set belongs to Windows and
has grown before. A blocklist that has fallen behind fails open, and what it fails open on is
a handle to the console or a serial port, which is exactly the escape this backend exists to
prevent.

So the object that was opened is asked what it is, using the system's own classification, and
anything that is not a file or directory on a filesystem is refused. The question is asked the
way round that fails closed: one kind of object is accepted and everything else — including a
kind this code has never heard of — is dropped. Deriving the answer from the volume underneath
the handle instead would mean keeping a second list, of which device types count as a
filesystem, and that list would age the same way the first one does.

Reaching this check should be impossible, since the names it catches are refused earlier. That
is the point of it. It costs one question per open and it is the only part of the device
defence that does not depend on having anticipated the name.

**Names are matched without regard to case.** That is what the rest of the system does, and
therefore the only choice under which a name reaches the same file here as it does in every
other program. Asking for case-sensitive matching would not reliably get it — the kernel has
a setting that overrides the request — and would leave this library disagreeing with
everything else about which file a name refers to. The consequence is stated rather than
worked around: **containment on Windows never rests on comparing names as strings**, because
two names differing only in case are one file. Every decision about whether a step is allowed
is taken from an open handle instead.

**A name that is an alias for a different name is refused.** A volume that generates short
names records two names for one entry — the one it was created with, and an eight-plus-three
alias derived from it — and an open by either reaches the same object. Nothing about that
leaves the directory, so it is not an escape; what it defeats is any rule a caller states
about names, because such a rule is stated about one spelling and there are two. A caller
refusing to serve `secret documents` is not refusing `SECRET~1`, and both are the same file.

Every generated alias contains a tilde, which makes the presence of one a cheap and complete
trigger: a component without one cannot be a generated alias and costs nothing, and a
component with one is opened and then asked what it is actually called, the handle being
dropped if the two names disagree. A file genuinely named `plain~1` answers with its own name
and is opened — the check is a comparison, not a refusal of the character. The alternative,
requiring short-name generation to be turned off on the volume and documenting the rest as
residual risk, was rejected: it makes the guarantee depend on how the host was configured,
which is not something a library can verify or a caller can usually change.

**Reparse tags are an allowlist of two**, the symbolic link and the junction. Reparse points
are a general extension mechanism and most tags have nothing to do with paths: an application
execution alias holds a series of counted strings, a container link is meaningful only to a
filter driver, and new tags arrive with new Windows features. A blocklist would treat every
tag invented after it was written as a link and read a structure of unknown shape as a
destination, so anything unrecognised is refused instead.

Within a symbolic link, whether the target is relative is taken from the structure's own flag
and never inferred from how the stored name is spelled, because the flag is what the
filesystem acts on and the two can disagree. Both readings fail closed: a link declaring
itself rooted is refused whatever it spells, and for one declaring itself relative the path
parser refuses every rooted spelling. A junction has no such flag and is never relative — its
target is recorded as a path from a volume root, which is why one can never be followed while
staying beneath a directory handle.

Every offset and length inside a reparse point is checked against the bytes actually returned
rather than against the length the structure claims for itself. Inside a sandbox that data is
attacker-controlled: anything that can create a file there can create a reparse point with
whatever header it likes.

## Directories opened only to be traversed

Reaching a name inside a directory and listing what a directory contains are separate rights
on every system here, and cap-dotnet asks for them separately. A handle kept only so that
further names can be resolved against it is opened without the authority to read the
directory at all.

This shows up in one place a caller can see. A directory that grants traversal without
granting listing — mode `0711`, the usual way a shared parent is kept from disclosing what
is beneath it — can be walked through on Linux, which has a form of open that takes no read
access, and on Windows, which grants traverse and list separately. macOS has neither, so
every directory handle there can also list, and a tree using that permission pattern is
reachable on the other two platforms and not on that one.

It is a platform limitation rather than a policy: macOS offers no way to ask for less. It is
recorded here so that the failure is recognisable when it appears, because nothing about the
error says the cause is the permission on a directory being passed through rather than on
the file being opened.

## Describing what a name holds

Asking what a file is costs one call per platform, and the three platforms answer with
different calls:

| Platform | Call | Notes |
|---|---|---|
| Linux | `statx` | One fixed layout on every architecture, unlike `struct stat` |
| macOS | `fstatat` / `fstat`, 64-bit-inode form | The entry point is chosen by architecture; on Intel the undecorated name still means the old layout |
| Windows | `NtQueryInformationFile` | Two queries normally, three when the entry redirects |

The same question is asked twice in this library, in two different ways, and the difference
is deliberate. Resolution asks it of every component of every path, wants only the type and
the identity, and passes the flag that lets a network filesystem answer from its cache — a
type and an inode number do not go stale in a way a walk can act on, and revalidating per
component would turn a deep path into a series of round trips. A caller asking what a file
is asks once, on purpose, and wants the length and the timestamps to be current, so that
call synchronises. A caller who notices that listing a directory is cheap and describing
every entry in it is not has found this, and the difference is the network round trip.

Windows needs more than one query because no single reply combines the times, the length,
the attributes and the identity. The times, the length and the attributes come together, so
those describe one instant; the identity is a second query; and the reparse tag is asked for
only when the attributes say the entry redirects, which is what separates a symbolic link
from a structure of unknown shape that merely looks like one.

**The identity is carried at 128 bits.** Windows issues identifiers that wide because the
64-bit ones it used to issue are not unique on every filesystem it supports, so a reader that
kept the low half would report two distinct files as one file under two names — silently, and
only on the filesystems nobody has mounted on a build agent. Resolution's own identity check
compares two things it looked at moments apart on one volume and does use the low half; the
comparison offered to callers does not.

**A creation time is absent rather than invented.** Linux reports per call whether the
filesystem supplied one, and several do not. macOS and Windows have nowhere to say so and
leave the field at zero instead, which is read as absence: a file created at the start of
1970, or of 1601, is not a thing that happens, and reporting one would be a worse answer than
reporting none.

**Timestamps outside the range the framework can hold are clamped, not refused.** Anything
that can write a file can set its timestamps, and a filesystem image can be crafted with any
value at all, so an absurd one is ordinary hostile input. Failing the call instead would
report nothing about a file whose other fields were perfectly readable, and would give anyone
who can write inside a sandbox a way to stop a caller's walk.

## A note on struct layout

`openat2` takes a pointer to `struct open_how` plus its size. If our declared size does not
match the kernel's, the syscall returns `EINVAL` — which the probe would read as "not
available", demoting every caller to the fallback permanently and silently. The fast path
would then never run again, on any machine, and every test would still pass.

This is why the interop layer requires per-architecture struct
layout tests, and why the arm64 leg exists in CI.
