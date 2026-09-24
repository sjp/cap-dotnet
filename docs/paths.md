# How cap-dotnet reads a path

Every operation starts by parsing the string a caller handed over. This document is the
contract for that step: what is accepted, what is refused and why, and — the part that
matters most — what is *not* rewritten.

The short version: parsing classifies and refuses. It never repairs. A path that comes back
from the parser is made of the caller's own characters, split into components, with nothing
substituted, folded or collapsed.

## Why not the framework's path helpers

`System.IO.Path` is not usable for a containment decision, and the build enforces that by
banning the type outright inside the parsing and resolution assembly.

`GetFullPath` resolves against the process working directory, which is ambient state the
caller does not control, and it collapses `..` as string arithmetic. That second part is the
problem. Given `a/../b` it returns `b`. If `a` is a symbolic link to `/etc`, the kernel
resolves `a/../b` to `/b`, because `..` is applied to wherever the link landed, not to
wherever the string appeared to be. A check performed on the collapsed string has approved a
path that is not the one that will be opened.

On Windows there is a second layer. Below the Win32 API a filename has its trailing dots and
spaces removed and is matched against a table of device names. Both happen *after* any
validation a caller wrote, so `foo.` and `foo` are one file while looking like two, and
`CON` is the console however deeply it is nested.

Neither behaviour is a bug in the framework — both are what the platform requires of a
general-purpose path API. They are simply the wrong tools for deciding whether a name stays
inside a sandbox.

## What is accepted

A relative path, and nothing else.

| Shape | Example | Result |
|---|---|---|
| Relative | `config/app.json` | accepted |
| Absolute | `/etc/passwd`, `C:\Windows` | refused |
| Root-relative (Windows) | `\file` | refused |
| Drive-relative (Windows) | `C:file` | refused |
| UNC | `\\server\share` | refused |
| Device namespace | `\\?\…`, `\\.\…` | refused |

Each gets its own reason rather than a single "invalid", so a caller can tell *you passed an
absolute path* from *that name is reserved*. The distinctions describe the shape of the
input and never anything about the filesystem, so reporting them to an untrusted caller
leaks nothing about the host.

The two Windows entries in the middle are worth singling out. `\file` and `C:file` look
relative — no leading root, nothing obviously suspicious — but they resolve against the
current drive and the per-drive working directory respectively. Both are process-wide state.
A path that carries ambient authority while wearing relative syntax is more dangerous than a
plainly absolute one, not less, because it survives a glance.

## Components

A path is split on `/`, and on `\` as well under Windows rules. From the resulting
components:

- `.` is dropped.
- An empty component — a repeated or trailing separator, as in `a//b` — is dropped. Every
  kernel treats `a//b` as `a/b`, so refusing it would only push callers into stitching paths
  together more carefully than the OS requires. Dropping it is safe because it names nothing:
  each component that remains is still checked on its own, so there is no check to skip past.
- `..` is **never** collapsed. See below.
- Everything else is yielded exactly as written.

Two facts are recorded at parse time because splitting the string throws them away:

- **whether the path contains `..`**, and
- **whether the path requires its target to be a directory** — it ended in a separator, or
  its last component was `.` or `..`. `foo/` and `foo` are different requests: the first must
  fail when `foo` is a regular file, and once the components are separated there is nothing
  left to recover that from.

A path of exactly one ordinary filename is flagged as such, so resolving it is a single
lookup rather than a walk. That flag is deliberately narrow — a path containing `..`, or one
insisting on a directory, is not a single lookup even with one component — so a resolver can
act on it with no further checks.

## `..` is walked, not collapsed

A `..` is never turned into string arithmetic, for the reason in the first section: collapsing
is only correct when no component is a symbolic link, and whether one is cannot be known
without asking the filesystem. A parser that collapses has committed to an answer it had no
way to compute.

`CapPath` offers two policies for it. Under `ParentLinkPolicy.Reject`, the default for
`CapPath.TryParse`, a `..` anywhere causes the whole path to be refused, which is the safe
answer for code that has no resolver of its own. Under `ParentLinkPolicy.Preserve` it is
carried through as a component, and whatever resolves the path must handle it by taking an
actual step against a real directory handle and re-checking the result against the sandbox
root. Removing the component and the one before it from the list is the same lexical
collapse, merely performed after the split rather than before, and it is wrong in exactly the
same cases.

A `Dir` parses every path it is given under `Preserve`, because its resolvers do take that
step:

- `dir/nested/../file` enters `nested`, steps back out and opens `dir/file`.
- `link/../file` steps back from wherever `link` led, which only the walk can know.
- A `..` taken at the handle's own directory is refused with `SandboxEscapeException`, even
  when the rest of the path would lead back inside: `dir/../../dir/file` is refused.
- A path ending in `..` names the directory the walk climbed back to. It can be opened,
  described and asked about. It holds no name in its parent, so creating, removing, renaming
  or linking at it is refused with a `CapIOException` whose kind is `InvalidArgument`, after
  the path has been resolved, so that one climbing above the handle is still reported as an
  escape. A file open refuses it as it refuses any path spelled as a directory.

Refusing every `..` outright would not make a handle safer. A symbolic link inside the tree can
already hold `..` in its target, and following it is the same walk under the same root test,
so a written-out `..` reaches nothing a link could not.

## A path spelled as a directory

A path ending in a separator, or in `..`, has to name a directory. An operation that acts on
something else refuses it according to what the name holds, as POSIX does. A file open that
only opens is refused as missing, as passing through something that is not a directory, or as
naming a directory. A file open that may create is refused as naming a directory whatever is
there, since no file can be created under such a name.

## Windows names

A component is additionally refused under Windows rules when it:

- names a character device — `CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`, `LPT0`–`LPT9`, the
  Latin-1 superscript spellings `COM¹` `COM²` `COM³` and their `LPT` equivalents, `CONIN$`
  and `CONOUT$` — in any disguise. The device name is matched before the extension and after
  trailing spaces are removed, so `CON.txt`, `CON .txt`, `con`, `CoN` and `CON.tar.gz` all
  reach the console;
- ends in a dot or a space, which Windows strips below the API;
- contains `:`, which introduces an alternate data stream: `file:stream` names a second,
  hidden body of the same file, and `CON::$DATA` reaches the device through one;
- contains `* ? < > " |`, which the native open call interprets as patterns, so a single
  name could match something other than itself;
- contains a character below `U+0020`.

A blocklist of OS behaviour ages badly — the reserved set has grown before and can grow
again — so this is the first of two defences rather than the only one. A handle opened on
Windows is also interrogated after the fact to confirm it names a filesystem object, so a
name nobody anticipated still cannot be used.

None of these apply under POSIX rules, where a file called `CON` or `foo.` is an ordinary
file and refusing it would make a real file permanently unreachable.

The rules are selected by an explicit syntax parameter rather than by the running OS. That is
what lets the Windows rules — the intricate and security-critical ones — be exercised in full
on a Linux build agent, instead of only on the one leg of the matrix that runs Windows.

## Characters and encoding

**`U+0000` is refused everywhere.** It terminates the string where the path is handed to the
kernel, so a name containing one would be silently truncated to a different name after being
checked.

**Nothing else is refused under POSIX rules.** A Unix filename may contain any byte but `/`,
which has already been consumed as a separator. Newlines, backslashes, leading dashes and
every name Windows reserves are ordinary filenames, and refusing them would make real files
unreachable for no gain.

**Unicode is never normalised.** The composed and decomposed spellings of an accented letter
are different names to a Linux kernel, and folding them here would let a check be passed in
one form and the file opened in the other. macOS is the complication: its filesystem layer
normalises on our behalf at the syscall boundary, so a name written in one form may come back
in the other. The consequence is that comparing a path to the sandbox root cannot assume byte
equality on that platform, and containment must not be decided by string comparison in any
case.

**Unix filenames are bytes, not text.** Nothing obliges them to be UTF-8, and on a filesystem
that has been around long enough some are not. Converting them with a decoder that
substitutes a replacement character produces a string that no longer encodes back to the name
it came from — the file cannot be reopened, and one bad name makes its whole directory
impossible to enumerate usefully.

So the conversion escapes instead of substituting. A byte that cannot begin a valid UTF-8
sequence becomes a lone low surrogate in `U+DC80`–`U+DCFF`, carrying its value in the low
eight bits, and encoding reverses that exactly. Every possible byte sequence survives a round
trip unchanged. The cost is strings that are not well-formed UTF-16 and must not be written
anywhere that assumes they are; the escape range cannot collide with real text, because
surrogate code points are not characters and cannot appear alone in well-formed UTF-16.

Decoding is strict about what it accepts as valid UTF-8. Overlong encodings, encoded
surrogates and values above `U+10FFFF` are refused and escaped byte by byte rather than
decoded generously, because two byte sequences that decoded to one string would be two names
for one file — and an alias is exactly what a containment check gets walked past on.

## Length

A path is refused above 32767 characters, and a component above 255.

These are bounds on how much work a hostile caller can ask for, not claims about what the
filesystem accepts. The kernel's limits are stricter and counted in bytes; a path under these
limits may still be refused when it is opened. A component of more than 255 characters is
always more than 255 bytes, so refusing one never refuses a name the kernel would have taken.

## What a parsed path does not mean

It is not a promise that the file exists, that it is reachable, or that it is inside
anything. It is a promise about the *string*: relative, well-formed, and containing no
component the platform will reinterpret.

Containment is decided afterwards, by resolving the path against a directory handle. Parsing
removes the ways a name can lie about itself; it does not and cannot decide where the name
leads.
