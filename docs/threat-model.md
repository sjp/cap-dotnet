# cap-dotnet threat model

**Status:** draft.
**Audience:** contributors to this repository, and reviewers deciding whether cap-dotnet is
an acceptable security control for their system.

This document is written *before* the resolution code, on purpose. Every design decision in
path parsing, in the three resolution backends, and in the symlink policy has to be
justifiable against something, and this is that something. If a proposed change cannot be
checked against a statement in this file, either the change is wrong or this file is
incomplete.

---

## 1. Terminology

| Term | Meaning here |
|---|---|
| **Ambient authority** | Authority that a piece of code holds simply by running in this process — `File.Open("/etc/passwd")` works because the *process* is allowed to, not because the caller was handed anything. |
| **Capability** | An unforgeable reference that *is* the authority. A `Dir` is a capability: code that does not hold one cannot reach what it names. |
| **Sandbox root** | The directory a `Dir` was opened on. |
| **Descending** | Moving from a directory to one of its entries by name, without following a link whose target lies outside the sandbox and without traversing a parent link. |
| **Escape** | Any observation of, or effect on, a filesystem object that is not reachable by descending from the sandbox root. |

## 2. The guarantee

> Given a `Dir` opened on directory *D*, no operation reachable from that `Dir` — and no
> `Dir`, `CapFile` or handle transitively derived from it — can observe or modify a
> filesystem object that is not, at the moment of the operation, reachable by descending
> from *D* without traversing a parent link or an out-of-tree symlink target.

Three parts of that sentence are load-bearing and are easy to weaken by accident:

- **"transitively derived"** — the guarantee is closed under derivation. A `Dir` obtained
  from a `Dir`, a `CapFile` obtained from either, and a directory entry obtained from an
  enumeration are all bound by it. There is no operation that hands back a wider authority
  than the one it was called on.
- **"at the moment of the operation"** — the guarantee is about the state of the filesystem
  when the syscall executes, not when the caller typed the path. This is what makes the
  TOCTOU discussion in §6 mandatory rather than optional.
- **"observe or modify"** — *observe* includes existence. A failure mode that says "no such
  file" for a path outside the sandbox but "permission denied" for one that exists outside
  it has leaked information and is a bug. Dangling symlinks are the sharp case; see S6 in
  §4.2.

### 2.1 What is deliberately *not* promised

The guarantee constrains what is reachable. It does not promise that reachable operations
succeed, that they are atomic, or that the sandbox contents are consistent between two
calls. It is a containment property, not a transactional one.

## 3. Trust boundaries and actors

cap-dotnet sits between **calling code** and **the filesystem**. Three actors matter:

1. **The caller.** Assumed to be *buggy or attacker-influenced, but in-process*. The whole
   point is that code holding a `Dir` on `/srv/uploads/tenant-7` cannot be tricked — by a
   malicious filename, a crafted archive entry, a user-supplied path — into touching
   `/etc/shadow`. The caller is **not** assumed to be actively hostile machine code; see
   the non-goal in §5.1.
2. **A concurrent filesystem actor.** Another process, or another thread, that can create,
   rename, and delete entries *inside* the sandbox while an operation is in flight. This is
   the TOCTOU adversary. Assumed to be unprivileged and to have write access only inside
   the sandbox subtree.
3. **The path itself.** Filenames and symlink targets already on disk are attacker-controlled
   input. A zip archive, a `git clone`, an untrusted upload, or a previous run of the same
   service can all have planted them. Parsing them is parsing hostile data.

Everything *above* the sandbox root — the root's own ancestors, the mount table, the kernel
— is trusted. cap-dotnet cannot defend a sandbox whose parent directory an attacker can
replace.

## 4. In scope: attacks that must be defended

Each row must, by the time the adversarial escape corpus lands, name the test that covers
it. A blank **Test** column is a visible reminder that the claim is unverified; prose in its
place would hide the same gap. The rows still blank are the ones that need an operation this
library does not yet expose — creating a link, renaming, hardlinking — or a host feature the
suite cannot yet arrange.

The **Where** column names the component responsible for the defence. The three resolution
backends are described in [backends.md](backends.md).

### 4.1 Lexical

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| L1 | `..` component in caller input | Rejected at parse time; never collapsed lexically | path parsing | `CapPathParseTests.Rejects_parent_links_by_default` |
| L2 | Absolute path (`/etc/passwd`, `C:\Windows`) | Rejected | path parsing | `CapPathParseTests.Rejects_absolute` |
| L3 | Drive-relative (`C:file`) and root-relative (`\file`) on Windows | Rejected | path parsing | `CapPathParseTests.Rejects_paths_relative_to_ambient_state` |
| L4 | UNC (`\\server\share`) and device namespace (`\\?\`, `\\.\`) | Rejected | path parsing | `CapPathParseTests.Rejects_unc`, `.Rejects_device_namespace`, `WindowsReservedNameTests.A_device_namespace_prefix_on_a_device_name_is_refused` |
| L5 | Empty component, `.`, repeated separators | Normalised or rejected, never silently skipped past a check | path parsing | `CapPathParseTests.Rejects_empty`, `CapPathComponentTests.Enumerates_components` |
| L6 | Very long paths / deep nesting | Bounded; fails cleanly rather than stack-overflowing | path parsing; component walk | `CapPathParseTests.Rejects_paths_and_components_that_are_too_long`, `PortableWalkTests.A_path_deeper_than_the_walk_will_descend_is_refused` |

`..` deserves a note. The obvious implementation — collapse `a/../b` to `b` before touching
the disk — is **wrong**, because if `a` is a symlink to `/etc` then the kernel resolves
`a/../b` to `/b`, not `./b`. `System.IO.Path.GetFullPath` does exactly this collapsing,
which is why it is banned inside `Cap.Primitives` by the build.

The full parsing contract — what is accepted, what is refused, and what is deliberately left
alone — is in [paths.md](paths.md).

### 4.2 Symlinks

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| S1 | Symlink to an absolute path outside the sandbox | Rejected, under every policy | symlink policy; all backends | `PortableWalkTests.An_absolute_link_is_refused`, `PortableWalkOnDiskTests.A_link_out_of_the_tree_is_refused_by_the_walk`, corpus case `escape-via-absolute-link` |
| S2 | Relative symlink escaping via `..` | Rejected | component walk | `PortableWalkTests.A_link_that_climbs_out_is_refused`, `PortableWalkOnDiskTests.A_link_out_of_the_tree_is_refused_by_the_walk`, corpus case `escape-via-parent-link` |
| S3 | Symlink chain that stays inside | Followed, unless the handle's policy refuses every link | symlink policy; all backends | `PortableWalkTests.A_link_inside_the_sandbox_is_followed`, `.A_links_target_is_resolved_from_where_the_link_lives`, corpus cases `chain-within-budget`, `denied-chain` |
| S4 | Symlink chain exceeding the budget | Fails as `ELOOP`, does not hang | component walk | `PortableWalkTests.A_chain_of_links_is_followed_exactly_as_far_as_the_platform_would`, `.A_link_cycle_is_stopped`, corpus cases `chain-beyond-budget`, `self-cycle`, `mutual-cycle` |
| S5 | Symlink in a *non-final* component | Same rules as any other component | component walk | `PortableWalkTests.A_link_inside_the_sandbox_is_followed`, `.A_step_up_is_taken_from_where_the_walk_actually_is`, corpus cases `link-as-middle-component`, `denied-link-as-middle-component` |
| S6 | Dangling symlink pointing outside | Decided from the target **as stored**, before anything is looked up. A link that leaves is a containment refusal whether or not the place it names exists, because that is never asked; a link that dangles *inside* is an ordinary not-found | symlink policy; all backends | `SymlinkPolicyTests.An_escaping_link_never_reveals_whether_its_target_exists_to_the_walk`, `.…_to_the_confined_open`, corpus cases `escape-via-parent-link-target-exists` / `-absent`, `dangling-link-inside` |
| S7 | `/proc/self/fd/N`, `/proc/self/root` and other magic links (Linux) | Rejected — by `RESOLVE_NO_MAGICLINKS` on the `openat2` backend; in the walk by the two rules that already apply, since a no-follow open refuses one and its target reads back as an absolute path or as no path at all | Linux backends | corpus cases `magic-link-to-a-process-root`, `magic-link-to-an-open-descriptor`, run on disk on Linux |
| S8 | Windows junction / mount point (always absolute) | Rejected | Windows backend | `WindowsResolutionOnDiskTests.A_junction_is_refused_rather_than_followed`, `WindowsReparseDataTests.A_junction_is_never_relative` |
| S9 | Windows `IO_REPARSE_TAG_APPEXECLINK`, `IO_REPARSE_TAG_WCI_LINK`, unknown tags | Rejected — these are not filesystem links and must not be interpreted as such | Windows backend; component walk | `WindowsReparseDataTests.A_tag_that_is_not_a_filesystem_link_yields_no_target`, `PortableWalkTests.A_reparse_point_that_is_not_a_link_is_never_followed` |
| S11 | Windows symbolic link whose stored target is spelled as a relative path but flagged as rooted | Rejected — the flag is what the filesystem acts on, so relativity is taken from it and never inferred from the spelling | Windows backend | `WindowsReparseDataTests.A_link_flagged_as_rooted_says_so_whatever_it_spells` |
| S12 | Windows reparse point whose header claims a length or a name offset outside the reply | Rejected; no read outside the returned bytes | Windows backend | `WindowsReparseDataTests.A_length_longer_than_the_reply_is_refused`, `.A_name_outside_the_data_is_refused`, `.A_truncated_reply_is_refused` |
| S13 | Symlink at the *final* component of an operation that changes something | Acted on as a name, never followed — the link is removed, moved or linked to as itself, and its target is neither reached nor looked up. Keeps working under the policy that refuses every link, since a handle held in order to distrust a subtree's links must be able to clear them out | all backends | `DirMutationTests.Removing_a_symbolic_link_removes_the_link_and_not_its_target`, `.A_handle_that_refuses_to_follow_links_can_still_remove_one`, `.Removing_a_directory_refuses_a_link_that_points_at_one` |
| S14 | Symlink in a non-final component of an operation that changes something | Resolved exactly as it is for an open: the prefix goes through the same confined resolution, so a link that leaves the subtree cannot aim a removal or a rename outside it | all backends | `DirMutationTests.A_link_used_as_a_directory_component_cannot_carry_a_removal_outside`, `ResolveParentTests.A_link_that_leaves_the_subtree_is_refused`, `.A_link_used_as_a_directory_component_is_followed` |
| S10 | Symlink planted concurrently, between two steps of a resolution | Must not redirect resolution outside the sandbox root. Kernel-atomic on the `openat2` backend; bounded but not eliminated elsewhere — see §6.1 | all backends; stress harness | `PortableWalkTests.A_directory_swapped_for_an_escaping_link_mid_walk_does_not_escape`, `.A_rename_under_the_walk_does_not_redirect_it` |

The rows above are also encoded as a single table of cases that every backend is driven
through, so that a disagreement between them fails a test rather than waiting for a
deployment to land on the other one. Each backend asserts against the same expected value
rather than against the other's answer, because comparing the two would pass whenever both
were wrong in the same direction. The cases named in the **Test** column above belong to that
table; it is driven by `SymlinkPolicyTests.The_walk_answers_the_policy_table`, `.The_confined_open_answers_the_policy_table`, `SymlinkPolicyOnDiskTests.The_hosts_walk_answers_the_policy_table`, `.The_hosts_confined_open_answers_the_policy_table`.

#### 4.2.1 The caller's knob, and what it does not reach

Following a link whose resolution stays inside is the default, because refusing every link
breaks ordinary directory layouts — a link inside a tree pointing elsewhere inside the same
tree is a normal thing for a package manager or a build system to have left behind, and a
sandbox that cannot read such a tree does not get used. A caller that treats a link as
suspect wherever it points can say so instead, and gets a handle that refuses every link.

Three properties of that knob are part of the model rather than conveniences:

- **It cannot weaken containment.** Under either value, a link whose target would leave the
  subtree is refused, and so is anything that is not a filesystem link at all: a junction,
  which is always stored as an absolute target; a reparse point whose tag means something
  else; and the kernel's synthetic links. None of those is a policy question, because
  following one lands somewhere with no relationship to the sandbox root.
- **It travels with the capability and only ever tightens.** The value is chosen when a root
  is opened and copied by every handle derived from it. One operation changes it, and it can
  only make it stricter; there is no setter and no other writer. A restriction that a derived
  handle could lift would be a suggestion, not a policy — whoever was given a handle in order
  to work inside a subtree could undo it in a single call.
- **It says nothing about the last component.** Whether an operation acts on a link or on
  what the link points at is fixed by the operation: removing a name removes the name,
  reading a link reads it, and an exclusive create must fail on a name already taken. Those
  keep working under the stricter policy — a handle that could not remove a symbolic link
  would be unable to clean up the very entries the policy was chosen to distrust. Conflating
  the two axes fails in both directions, and it is the classic bug in this area.

**Two decisions recorded here rather than left to the code.**

*A containment refusal stays distinguishable.* A path refused because it tried to leave the
subtree is reported as that and not folded into not-found, which is what lets an application
log and alert on the attempts without matching on message text. It reveals nothing a holder
of the handle could not find out anyway: the link is inside the sandbox, so its target could
simply be read. What must not be revealed is whether the place it pointed at exists, and that
is S6 — the refusal is decided from the stored target before any lookup happens, so the
answer for a link aimed at a real file outside and one aimed at nothing at all is the same
answer, reached without either lookup. There is deliberately no option to collapse every
refusal into not-found: a caller that wants that can catch the one exception type and rewrite
it, and a library-wide switch would be a second policy axis to inherit, test and document.

*An absolute link target is refused, never re-anchored.* Reading it as though the sandbox
root were the filesystem root is defensible — it is what `chroot` does — but it silently
changes which file a link means and nothing in the result tells a caller which reading they
got. It is also not available in isolation: on the one platform that resolves a whole path in
a single confined operation, asking the kernel to re-anchor absolute targets also makes it
clamp an upward step at the root instead of refusing it. A link trying to climb out would
stop being a reported refusal and become a successful open of a different file, and that
platform would disagree with the others about the same tree. Both are worse than refusing.

### 4.3 Windows name handling

This is where cap-std itself shipped a CVE
([GHSA-hxf5-99xg-86hw](https://github.com/bytecodealliance/cap-std/security/advisories/GHSA-hxf5-99xg-86hw),
Windows device filenames escaping the sandbox), so it gets its own section rather than a
bullet.

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| W1 | Reserved device names: `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9` | Rejected | Windows name validation | `WindowsReservedNameTests.Every_mangling_of_a_reserved_name_is_refused` |
| W2 | Mangled variants: `CON.txt`, `CON.`, `CON ` (trailing space), `con`, `CoN` | Rejected | Windows name validation | `WindowsReservedNameTests.Every_mangling_of_a_reserved_name_is_refused`, `.Trailing_dots_and_spaces_do_not_hide_a_device` |
| W3 | Superscript digit forms `COM¹`, `COM²`, `COM³`, and `CONIN$` / `CONOUT$` | Rejected | Windows name validation | `WindowsReservedNameTests.ReservedStems` |
| W4 | Alternate data streams: `file:stream`, `CON::$DATA` | Rejected (`:` is not a legal component character) | Windows name validation | `WindowsReservedNameTests.Every_stream_spelling_of_a_reserved_name_is_refused` |
| W5 | Trailing dots and spaces, which Win32 silently strips *after* validation | Rejected before they can diverge | path parsing; Windows name validation | `CapPathParseTests.Rejects_trailing_dot_or_space` |
| W6 | 8.3 short names (`PROGRA~1`) aliasing a long name | Rejected. A component containing a tilde is opened and then asked what it is actually called; the handle is dropped if the two names disagree. A file genuinely named with a tilde answers with itself and is allowed | Windows backend | `WindowsResolutionOnDiskTests.A_short_name_alias_does_not_reach_the_object_it_aliases`, `.A_name_that_merely_contains_a_tilde_is_its_own_name` |
| W7 | Wildcards `* ? < > "` reaching the native open | Rejected | Windows name validation | `CapPathParseTests.Rejects_invalid_windows_characters` |
| W8 | A sandbox root named by a user-facing path that reaches a device rather than a directory | Rejected. The one open that resolves a path string interrogates the handle it got, rather than re-examining the path it was given | Windows backend | `WindowsResolutionOnDiskTests.The_first_directory_handle_refuses_what_is_not_a_filesystem_directory` |
| W9 | A device name that gets past the rules in W1–W5 — a name added to the reserved set after this was written, or one a future parser bug lets through | Rejected. Every handle the Windows backend produces is asked what kind of object it is, and anything but a file or directory on a filesystem is dropped before it reaches a caller | Windows backend | `WindowsDeviceHandleTests.A_handle_to_a_device_is_refused`, `.A_device_name_as_a_component_does_not_reach_a_device` |

A parse-time blocklist is necessary but **not sufficient**: the set of device names is a
property of the OS and has grown before. Windows name validation is therefore backed by a
*post-open* device check, so that a name we failed to anticipate still cannot be used. W8 and
W9 are the two places it applies — the ambient open that resolves a path string, and every
name resolved beneath a handle after it.

W9 is the one that does not depend on having anticipated anything, and it is deliberately
framed the other way round from W1–W5: rather than listing the kinds of object to refuse, it
names the one kind to accept. A handle the system classifies as something we have never heard
of is dropped, where a list of what to refuse would hand it back. The classification is the
system's own — we ask what the handle is, rather than deriving it from the volume underneath —
so there is no second list of ours to fall behind.

A consequence worth stating: reaching W9 at all means W1–W5 missed something. It is not a
condition any correct input produces, so a caller that sees it has found either a name we did
not know about or a bug, and both are worth reporting.

Case is not in this table because on Windows it is not an attack but a fact to build on. Two
names differing only in case are one file, every open this library issues matches
case-insensitively so that a name reaches the same file here as it does everywhere else, and
the consequence is that **containment on Windows never rests on comparing names as strings**.
Every decision about whether a step is allowed is taken from an open handle. Asserted in
`WindowsResolutionOnDiskTests.Names_differing_only_in_case_reach_the_same_object` and, for the
half that matters, `.Changing_the_case_of_a_refused_name_does_not_get_past_the_refusal`.

### 4.4 macOS and case-insensitive volumes

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| M1 | Unicode normalisation: NFC vs NFD forms of the same filename | Documented and consistent; must not allow a check to be bypassed by re-encoding | path parsing | parser half only: `CapPathComponentTests.Components_are_returned_verbatim` |
| M2 | Case-insensitive volume: `secret` vs `SECRET` | Containment must not depend on case-sensitive string comparison | all backends; escape corpus | Windows half only: `WindowsResolutionOnDiskTests.Changing_the_case_of_a_refused_name_does_not_get_past_the_refusal` |
| M3 | `/tmp` and `/var` being symlinks to `/private/*` | Resolved once, under ambient authority, at root acquisition | temp directory helpers | |

### 4.5 Filesystem topology

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| T1 | Mount point or bind mount appearing under the sandbox | Documented; refusing to cross one available as a policy on every backend | `openat2` backend; component walk | `PortableWalkTests.A_mount_point_can_be_refused` |
| T2 | Hardlink creation crossing the sandbox boundary | Requires a capability on *both* sides | `Dir` API | |
| T3 | Rename crossing the boundary | Same: both `Dir`s required | `Dir` API | |
| T4 | Pre-existing hardlink to an outside file, planted inside | **Not defendable** — see §6.3 | — | |

## 5. Explicit non-goals

These are not oversights. Each one is a place where a reader might reasonably expect a
guarantee, so each is stated rather than left implied.

### 5.1 cap-dotnet is not a process sandbox

Code holding a `Dir` can still call `File.Open`, `DllImport("libc")`, `Process.Start`, or
read `/proc/self/mem`. Containment is a property of *this API*, not of the process.

cap-dotnet is the right tool when the untrusted thing is **data** — a path, a filename, an
archive entry, a config value. It is the wrong tool, by itself, when the untrusted thing is
**code**. For that, pair it with an OS-level sandbox (seccomp, AppContainer, a container, a
Wasm runtime). The shipped Roslyn analyzer narrows this gap for *cooperating* code by making
ambient `System.IO` a build error, but a compiler diagnostic is not a security boundary and
must never be described as one.

### 5.2 Not a defense against a privileged attacker

An attacker with `CAP_SYS_ADMIN`, `CAP_DAC_OVERRIDE`, `SeBackupPrivilege`,
`SeRestorePrivilege`, or the ability to mount filesystems or modify the sandbox root's own
ancestors defeats the model. This is also why every test assembly asserts at startup that
it holds no such power: several negative tests would otherwise pass for the wrong reason,
having been stopped by a privilege check that never ran rather than by containment.

Note that this is not the same assertion on both platforms. Unix root bypasses DAC outright,
so the check is euid. A Windows administrator bypasses nothing by being one — ACLs are still
evaluated against an elevated token — so the check is whether an ACL-bypassing privilege is
*enabled* in the token. The distinction is not pedantry: the corpus needs an elevated token
on Windows in order to create symbolic links at all, so a blanket "must not be elevated"
rule would forbid exactly the tests this section exists to protect.

### 5.3 No resource limits

Disk quota, inode exhaustion, file-descriptor exhaustion, file count, and file size are out
of scope. A caller holding a `Dir` can fill the volume. (Handle exhaustion *behaviour* is in
scope to the extent that running out of descriptors must not cause a containment failure —
the stress harness covers it — but preventing exhaustion is not.)

One narrow exception, which is about the library's own appetite rather than the caller's.
The component-by-component resolver holds a handle open for every directory level it has
descended through, so the descriptors one resolution consumes are a function of the path it
was handed. It therefore refuses a path that nests deeper than a fixed limit, and it closes
every handle it opened on every exit including the failing ones. Neither is a defence
against a caller who wants to exhaust descriptors — they can, and that is still out of
scope. They stop a single crafted *path* from doing it, which would otherwise make opens
fail in code that never went near this library.

### 5.4 Not a confidentiality boundary for what the handle already reveals

The sandbox root's own device and inode numbers, its timestamps, and the fact of its
existence are observable through any handle derived from it.

### 5.5 No promise of atomicity across operations

`Dir.RemoveDirRecursive` is many syscalls. Directory enumeration is not a snapshot. Where
atomicity *is* provided — the exclusive create behind the temporary-file helpers, the rename
behind the atomic-write helper — it is documented per-operation and is not a general
property.

## 6. Residual risk

### 6.1 The fallback resolver narrows TOCTOU; it does not close it

On Linux ≥ 5.6, `openat2(RESOLVE_BENEATH)` makes resolution a single kernel-atomic
operation and the race does not exist. Everywhere else — older kernels, seccomp filters that
block `openat2`, macOS, Windows — resolution is a loop of per-component opens. Each step is
safe, and because every step is *handle-relative* an attacker cannot redirect resolution to
a different subtree. But between two steps a component inside the sandbox can be swapped for
another component inside the sandbox.

The consequence is bounded and worth stating plainly: **an attacker who can write inside the
sandbox may be able to steer an operation to a different object that is also inside the
sandbox.** They cannot steer it outside. For most uses — untrusted archive extraction,
per-tenant storage — that is the property that matters.

What an attacker *can* do through that window, and what they cannot, is asserted directly
rather than argued: the walk is driven against a simulated filesystem that mutates at the
exact instant between two steps, in
`PortableWalkTests.A_directory_swapped_for_an_escaping_link_mid_walk_does_not_escape` and
`.A_rename_under_the_walk_does_not_redirect_it`. Both show resolution continuing into
whatever was substituted and both show it staying beneath the root.

The stress harness must additionally **quantify** the window rather than describe it, and
the result belongs in this document.

### 6.2 `..` on the fallback path

Parent traversal is handled by popping a handle off a stack we already hold, never by
`openat(fd, "..")`. This matters: `openat(fd, "..")` asks the kernel to resolve the parent
of whatever `fd` currently refers to, which a concurrent rename can change. Popping a held
handle cannot be raced.

The consequence to be aware of is that after a rename the two answers genuinely differ. A
walk that has descended into `a` and then meets `..` returns to the directory it came from,
even if `a` has since been moved somewhere else entirely and the kernel would now say its
parent is elsewhere. That is the answer the sandbox needs — the directory it returns to is
one it has already established is beneath the root — but it is not always the answer the
filesystem would give.

### 6.3 Hardlinks planted before the sandbox was opened

If a hardlink to `/etc/shadow` already exists inside the sandbox when the `Dir` is opened,
cap-dotnet will happily open it: it *is* reachable by descending, and there is no way to
distinguish it from a legitimate file. This is a property of hardlinks, not a bug.
Applications that accept untrusted content into a directory they later sandbox should
create that directory on a filesystem where the untrusted party cannot create hardlinks to
outside files — in practice, a dedicated mount or a directory they own exclusively.

### 6.4 The root itself is resolved with ambient authority

Opening the first `Dir` requires an `AmbientAuthority` token and resolves an ordinary path
with ordinary ambient rules. Everything the model promises begins *after* that. The token
exists to make that moment greppable and auditable, not to make it safe.

### 6.5 Silently weakening containment is itself a vulnerability

A capability probe that fails open, or a demotion from the kernel-atomic backend to a walk
that goes unreported, leaves callers believing they have §2's guarantee when they have
§6.1's. No escape need be demonstrated for that to be a security bug; it is in scope for
private disclosure either way. This is why the active backend and its call counts are
required to be observable at runtime rather than being an implementation detail — see
[backends.md](backends.md).

## 7. Reporting

See [SECURITY.md](../SECURITY.md). Sandbox escapes are security issues; please report them
privately rather than opening a public issue.
