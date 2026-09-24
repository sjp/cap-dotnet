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

Each row names the test that covers it. Most name a case of the escape corpus, described in
§4.8, which runs every attack it can arrange against every backend and through every operation
that takes a path. A row the corpus cannot arrange — a crafted reparse point, a device name
that only a parser bug could let through, an attacker racing the resolver — names the test
that covers it elsewhere. The corpus checks this table: a row with nothing in its **Test**
column, or one the corpus defends without saying so here, fails the build.

The **Where** column names the component responsible for the defence. The three resolution
backends are described in [backends.md](backends.md).

### 4.1 Lexical

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| L1 | `..` component in caller input | Walked beneath the handle as a real step back, never collapsed lexically; a step above the handle is refused as an escape wherever it appears; a path ending in `..` is not a name any mutation can act on | path parsing; component walk; kernel-confined open | `DirParentStepTests`; escape corpus: `parent`, `parent-twice`, `parent-after-descent`, `dot-parent-dot`, `parent-to-a-file-outside`, `parent-far-past-the-root`, `parent-that-stays-inside*`, `parent-through-a-name-not-there`, `parent-trailing`, `parent-after-a-link-*` |
| L2 | Absolute path (`/etc/passwd`, `C:\Windows`) | Rejected | path parsing | `CapPathParseTests.Rejects_absolute`; escape corpus: `absolute-*`, `windows-absolute`, `windows-absolute-forward-slashes` |
| L3 | Drive-relative (`C:file`) and root-relative (`\file`) on Windows | Rejected | path parsing | `CapPathParseTests.Rejects_paths_relative_to_ambient_state`; escape corpus: `windows-root-relative`, `windows-drive-relative` |
| L4 | UNC (`\\server\share`) and device namespace (`\\?\`, `\\.\`) | Rejected | path parsing | `CapPathParseTests.Rejects_unc`, `.Rejects_device_namespace`, `WindowsReservedNameTests.A_device_namespace_prefix_on_a_device_name_is_refused`; escape corpus: `windows-unc`, `unc-forward-slashes`, `windows-device-namespace-*`, `device-namespace-forward-slashes`, `windows-object-manager-namespace` |
| L5 | Empty component, `.`, repeated separators | Normalised or rejected, never silently skipped past a check | path parsing | `CapPathParseTests.Rejects_empty`, `CapPathComponentTests.Enumerates_components`; escape corpus: `empty`, `dot`, `dot-slash-dot`, `doubled-separator`, `dot-components`, `trailing-separator-*`, `nul-*` |
| L6 | Very long paths / deep nesting | Bounded; fails cleanly rather than stack-overflowing | path parsing; component walk | `CapPathParseTests.Rejects_paths_and_components_that_are_too_long`, `PortableWalkTests.A_path_deeper_than_the_walk_will_descend_is_refused`; escape corpus: `component-too-long`, `path-too-long`, `longer-than-the-kernel-takes-at-once`, `deeper-than-the-walk-descends`, `longer-than-the-win32-path-limit` |

`..` deserves a note. The obvious implementation — collapse `a/../b` to `b` before touching
the disk — is **wrong**, because if `a` is a symlink to `/etc` then the kernel resolves
`a/../b` to `/b`, not `./b`. `System.IO.Path.GetFullPath` does exactly this collapsing,
which is why it is banned inside `Cap.Primitives` by the build.

So `..` is resolved rather than collapsed or refused. The walk steps back through a directory
handle it already holds (§6.2), and the kernel-confined open lets the kernel take the step
under `RESOLVE_BENEATH`, which refuses one that would leave. Either way a step taken at the
handle's own directory is refused as an escape, even when the rest of the path would lead
back inside. Refusing every `..` outright would not be a stronger guarantee: a symbolic link
inside the tree can already hold `..` in its target, and following it is the same walk under
the same root test (S2), so a `..` the caller writes can reach nothing a link could not.

A path ending in `..` names a directory by where it sits, not by a name in its parent. It
can be opened and described. Creating, removing, renaming or linking at it is refused before
anything is changed, since removing what `a/..` names would remove a directory the caller
never spelled out, up to and including the handle's own.

The full parsing contract — what is accepted, what is refused, and what is deliberately left
alone — is in [paths.md](paths.md).

### 4.2 Symlinks

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| S1 | Symlink to an absolute path outside the sandbox | Rejected, under every policy | symlink policy; all backends | `PortableWalkTests.An_absolute_link_is_refused`, `PortableWalkOnDiskTests.A_link_out_of_the_tree_is_refused_by_the_walk`, corpus case `escape-via-absolute-link`; escape corpus: `absolute-link-*` |
| S2 | Relative symlink escaping via `..` | Rejected | component walk | `PortableWalkTests.A_link_that_climbs_out_is_refused`, `PortableWalkOnDiskTests.A_link_out_of_the_tree_is_refused_by_the_walk`, corpus case `escape-via-parent-link`; escape corpus: `link-climbing-to-a-file-outside`, `link-to-the-parent*`, `link-to-a-sibling-outside`, `nested-link-climbing-out` |
| S3 | Symlink chain that stays inside | Followed, unless the handle's policy refuses every link, or the link is the final component of an open that creates or truncates a file (S15) | symlink policy; all backends | `PortableWalkTests.A_link_inside_the_sandbox_is_followed`, `.A_links_target_is_resolved_from_where_the_link_lives`, corpus cases `chain-within-budget`, `denied-chain`; escape corpus: `link-to-a-file-inside`, `link-to-a-directory-inside`, `link-climbing-to-the-root-and-back`, `chain-inside` |
| S4 | Symlink chain exceeding the budget | Fails as `ELOOP`, does not hang | component walk | `PortableWalkTests.A_chain_of_links_is_followed_exactly_as_far_as_the_platform_would`, `.A_link_cycle_is_stopped`, corpus cases `chain-beyond-budget`, `self-cycle`, `mutual-cycle`; escape corpus: `chain-of-five-ending-outside*`, `link-to-a-link-to-outside`, `self-cycle*`, `mutual-cycle`, `chain-beyond-the-budget` |
| S5 | Symlink in a *non-final* component | Same rules as any other component | component walk | `PortableWalkTests.A_link_inside_the_sandbox_is_followed`, `.A_step_up_is_taken_from_where_the_walk_actually_is`, corpus cases `link-as-middle-component`, `denied-link-as-middle-component`; escape corpus: every `*-as-a-component` case |
| S6 | Dangling symlink pointing outside | Decided from the target **as stored**, before anything is looked up. A link that leaves is a containment refusal whether or not the place it names exists, because that is never asked; a link that dangles *inside* is an ordinary not-found | symlink policy; all backends | `SymlinkPolicyTests.An_escaping_link_never_reveals_whether_its_target_exists_to_the_walk`, `.…_to_the_confined_open`, corpus cases `escape-via-parent-link-target-exists` / `-absent`, `dangling-link-inside`; escape corpus: `dangling-link-*`, `TopologyTests.A_link_outside_is_refused_the_same_way_whether_or_not_its_target_exists` |
| S7 | `/proc/self/fd/N`, `/proc/self/root` and other magic links (Linux) | Rejected — by `RESOLVE_NO_MAGICLINKS` on the `openat2` backend; in the walk by the two rules that already apply, since a no-follow open refuses one and its target reads back as an absolute path or as no path at all | Linux backends | corpus cases `magic-link-to-a-process-root`, `magic-link-to-an-open-descriptor`, run on disk on Linux; escape corpus: `magic-link-*` |
| S8 | Windows junction / mount point (always absolute) | Rejected | Windows backend | `WindowsResolutionOnDiskTests.A_junction_is_refused_rather_than_followed`, `WindowsReparseDataTests.A_junction_is_never_relative`; escape corpus: `junction-*` |
| S9 | Windows `IO_REPARSE_TAG_APPEXECLINK`, `IO_REPARSE_TAG_WCI_LINK`, unknown tags | Rejected — these are not filesystem links and must not be interpreted as such | Windows backend; component walk | `WindowsReparseDataTests.A_tag_that_is_not_a_filesystem_link_yields_no_target`, `PortableWalkTests.A_reparse_point_that_is_not_a_link_is_never_followed`; escape corpus: `WindowsTopologyTests.An_application_execution_alias_is_refused_rather_than_followed` |
| S11 | Windows symbolic link whose stored target is spelled as a relative path but flagged as rooted | Rejected — the flag is what the filesystem acts on, so relativity is taken from it and never inferred from the spelling | Windows backend | `WindowsReparseDataTests.A_link_flagged_as_rooted_says_so_whatever_it_spells` |
| S12 | Windows reparse point whose header claims a length or a name offset outside the reply | Rejected; no read outside the returned bytes | Windows backend | `WindowsReparseDataTests.A_length_longer_than_the_reply_is_refused`, `.A_name_outside_the_data_is_refused`, `.A_truncated_reply_is_refused` |
| S13 | Symlink at the *final* component of an operation that changes something | Acted on as a name, never followed unless the call asks for it (S17) — the link is removed, moved, linked to or given new times as itself, and its target is neither reached nor looked up. Keeps working under the policy that refuses every link, since a handle held in order to distrust a subtree's links must be able to clear them out | all backends | `DirMutationTests.Removing_a_symbolic_link_removes_the_link_and_not_its_target`, `.A_handle_that_refuses_to_follow_links_can_still_remove_one`, `.Removing_a_directory_refuses_a_link_that_points_at_one`, `DirTimesTests.A_link_has_its_own_times_set_and_its_target_is_not_reached`, `.A_link_to_something_outside_is_set_as_a_link`; escape corpus: every case whose last component is a link, through every operation that changes something |
| S14 | Symlink in a non-final component of an operation that changes something | Resolved exactly as it is for an open: the prefix goes through the same confined resolution, so a link that leaves the subtree cannot aim a removal or a rename outside it | all backends | `DirMutationTests.A_link_used_as_a_directory_component_cannot_carry_a_removal_outside`, `ResolveParentTests.A_link_that_leaves_the_subtree_is_refused`, `.A_link_used_as_a_directory_component_is_followed`; escape corpus: every case with a link before the last component, through every operation that changes something |
| S15 | Symlink at the *final* component of an open that creates or truncates a file (every `FileMode` but `Open`, and `CreateFile`, `WriteAllBytes`, `WriteAllBytesAsync`), including one whose target is inside | Refused under every policy, without the link being read, whether it leads to a file or a directory and whether or not it dangles; the link and its target are left as they were. A write names its file by a name somebody else often chose, and following a link planted under that name would empty or create a different file inside the tree. Links before the last component are still followed under the handle's policy, and an open of an existing file still follows a final link | all backends | `FinalLinkWriteTests.A_mode_that_creates_or_truncates_refuses_a_link_at_the_name`, `.The_whole_file_writes_refuse_a_link_at_the_name`; escape corpus: `link-to-a-file-inside`, `link-to-a-directory-inside-as-the-name`, `dangling-link-inside`, and every case whose last component is a link, through `CreateFile` |
| S16 | Creating a symlink whose target leaves the sandbox, for a program outside the library to follow later | A rooted target (`/etc`, and on Windows also `C:\dir`, `C:dir`, `\dir`, UNC and device paths) is refused with `SandboxEscapeException` and nothing is created, under every policy and for both kinds of link; rootedness is read by the running platform's path rules, as resolution reads a link it meets. A relative target is stored as given, one that climbs out included, and refused when followed (S2). A recursive copy that would recreate a link with a rooted target fails before touching the destination name | `Dir` link creation; recursive copy; all backends | `CreatedLinkTargetTests.A_rooted_target_is_refused_and_nothing_is_created`, `.A_relative_target_that_climbs_out_is_stored_and_refused_when_followed`, `.A_target_inside_is_stored_and_followed`, `CopyTests.A_link_with_a_rooted_target_stops_the_copy_before_the_destination_is_touched`; escape corpus: every rooted case through `CreateSymlinkTo` leaves no link |
| S17 | Symlink at the *final* component of a call that asks to follow it (`GetMetadata`, `SetTimes` and `CreateHardLink` with `followLink: true`) or asks not to (`OpenFile` and `OpenDir` with `noFollow: true`) | Followed on request exactly as a link on the way is: its target is put in place of the last component and resolved from the handle again by the same confined resolution, so a target that leaves is refused as an escape, a rooted one included, and under the policy that refuses every link it is refused as a link. The call then acts on the name the chain ends at, without following it and without opening it. Refused on request under every policy without being read, wherever it points and whether or not it dangles, while links before it are still followed under the policy, except that a path ending in a separator asks for what the link leads to and follows it, as POSIX resolution does. Neither request changes the policy of the handle or of any handle derived from it. Following is not one instant on any backend: a name replaced between the look at it and the call changes which entry beneath the handle is acted on, never whether one outside is | `Dir`; all backends | `PerCallFinalLinkTests.Following_a_final_link_on_request_acts_on_what_it_leads_to`, `.Following_a_final_link_that_leads_out_is_refused_as_an_escape`, `.A_handle_that_refuses_links_refuses_a_final_one_a_call_asks_to_follow`, `.Opening_without_following_refuses_a_link_at_the_name`, `.Opening_without_following_still_follows_links_on_the_way`; escape corpus: every case through `OpenFileNoFollow`, `OpenDirNoFollow`, `GetMetadataFollowing`, `SetTimesFollowing` and `HardLinkFromFollowing` |
| S10 | Symlink planted concurrently, between two steps of a resolution | Must not redirect resolution outside the sandbox root. Kernel-atomic on the `openat2` backend; bounded but not eliminated elsewhere — see §6.1 | all backends; stress races | `PortableWalkTests.A_directory_swapped_for_an_escaping_link_mid_walk_does_not_escape`, `.A_rename_under_the_walk_does_not_redirect_it`, `.A_link_swapped_back_for_a_directory_before_it_is_read_is_looked_at_again`; stress races: `ResolverRaceTests`, `TreeRaceTests`, `DerivationRaceTests` — see §6.1 |

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
  what the link points at is decided by the operation: removing a name removes the name,
  reading a link reads it, setting a name's times sets the link's own, an exclusive create must fail on a name already taken, and any
  open that creates or truncates a file refuses a link at the name even when the link stays
  inside (S15). Only opening something that already exists follows a final link. Describing,
  setting times and hard-linking may be asked per call to follow one, and opening may be
  asked per call not to (S17); asking changes only what happens at the last component, and a
  link followed there is held to the policy like any other. Those
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

*A rooted link target is refused when the link is made; a climbing one is not.* Nothing
beneath a handle follows a link that leaves, so refusing one when it is made is not about
this library. It is about what else reads the tree: a link persists on disk, and a shell, a
backup job or a web server serving the same directory follows it wherever it points. A
rooted target names somewhere outside from wherever the link sits, so it can be refused
from its text, and WASI hosts refuse it the same way. A relative target that climbs out
cannot be judged that way. `../x` leaves or stays depending on where the link sits, and a
rename beneath the handle can later move a link to where the same text leaves, so a check
at creation would promise something no check can keep. A caller that must not leave such
links behind has to decide which relative targets it accepts. Creating a link is not
governed by the symlink policy either: the policy says whether links are followed, and
making one follows nothing, just as removing one doesn't.

### 4.3 Windows name handling

This is where cap-std itself shipped a CVE
([GHSA-hxf5-99xg-86hw](https://github.com/bytecodealliance/cap-std/security/advisories/GHSA-hxf5-99xg-86hw),
Windows device filenames escaping the sandbox), so it gets its own section rather than a
bullet.

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| W1 | Reserved device names: `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9` | Rejected | Windows name validation | `WindowsReservedNameTests.Every_mangling_of_a_reserved_name_is_refused`; escape corpus: `reserved-name *` |
| W2 | Mangled variants: `CON.txt`, `CON.`, `CON ` (trailing space), `con`, `CoN` | Rejected | Windows name validation | `WindowsReservedNameTests.Every_mangling_of_a_reserved_name_is_refused`, `.Trailing_dots_and_spaces_do_not_hide_a_device`; escape corpus: `reserved-name *`, `trailing-dot-or-space *` |
| W3 | Superscript digit forms `COM¹`, `COM²`, `COM³`, and `CONIN$` / `CONOUT$` | Rejected | Windows name validation | `WindowsReservedNameTests.ReservedStems`; escape corpus: `reserved-name *` |
| W4 | Alternate data streams: `file:stream`, `CON::$DATA` | Rejected (`:` is not a legal component character) | Windows name validation | `WindowsReservedNameTests.Every_stream_spelling_of_a_reserved_name_is_refused`; escape corpus: `alternate-data-stream`, `default-data-stream`, `device-through-a-stream` |
| W5 | Trailing dots and spaces, which Win32 silently strips *after* validation | Rejected before they can diverge | path parsing; Windows name validation | `CapPathParseTests.Rejects_trailing_dot_or_space`; escape corpus: `trailing-dot-or-space *` |
| W6 | 8.3 short names (`PROGRA~1`) aliasing a long name | Rejected. A component containing a tilde is opened and then asked what it is actually called; the handle is dropped if the two names disagree. A file genuinely named with a tilde answers with itself and is allowed | Windows backend | `WindowsResolutionOnDiskTests.A_short_name_alias_does_not_reach_the_object_it_aliases`, `.A_name_that_merely_contains_a_tilde_is_its_own_name`; escape corpus: `WindowsTopologyTests.A_short_name_alias_does_not_reach_the_entry_it_aliases` |
| W7 | Wildcards `* ? < > "` reaching the native open | Rejected | Windows name validation | `CapPathParseTests.Rejects_invalid_windows_characters`; escape corpus: `wildcard *` |
| W8 | A sandbox root named by a user-facing path that reaches a device rather than a directory | Rejected. The one open that resolves a path string interrogates the handle it got, rather than re-examining the path it was given | Windows backend | `WindowsResolutionOnDiskTests.The_first_directory_handle_refuses_what_is_not_a_filesystem_directory`; escape corpus: `WindowsTopologyTests.A_root_named_by_a_path_to_a_device_is_not_opened` |
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
| M1 | Unicode normalisation: NFC vs NFD forms of the same filename | Documented and consistent; must not allow a check to be bypassed by re-encoding | path parsing | `CapPathComponentTests.Components_are_returned_verbatim`; escape corpus: `normalisation-variant-*`, run on every volume and folded where the volume folds |
| M2 | Case-insensitive volume: `secret` vs `SECRET` | Containment must not depend on case-sensitive string comparison | all backends; escape corpus | `WindowsResolutionOnDiskTests.Changing_the_case_of_a_refused_name_does_not_get_past_the_refusal`; escape corpus: `case-variant-*`, run on every volume and folded where the volume folds |
| M3 | `/tmp` and `/var` being symlinks to `/private/*` | Resolved once, under ambient authority, at root acquisition | temp directory helpers | `CapTempDirTests.The_directory_sits_beneath_the_system_temporary_location_under_its_own_name`; escape corpus: `TopologyTests.A_root_opened_through_a_link_is_confined_to_where_the_link_led`, `.A_temporary_location_that_is_a_link_can_be_a_root` |

### 4.5 Filesystem topology

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| T1 | Mount point or bind mount appearing under the sandbox | Crossed, as descending into anything else is: the mount table is trusted. A step up from inside the mount lands in the sandbox, and a link inside it is held to the same root. Every backend's resolver can refuse to cross a mount, but no public member asks it to yet | `openat2` backend; component walk | `PortableWalkTests.A_mount_point_can_be_refused`; escape corpus: `TopologyTests.A_mount_inside_the_root_is_crossed_but_cannot_be_climbed_out_of` |
| T2 | Hardlink creation crossing the sandbox boundary | Requires a capability on *both* sides | `Dir` API | escape corpus: every case through both ends of `CreateHardLink` |
| T3 | Rename crossing the boundary | Same: both `Dir`s required | `Dir` API | escape corpus: every case through both ends of `Rename` |
| T4 | Pre-existing hardlink to an outside file, planted inside | **Not defendable** — see §6.3 | — | escape corpus, as a documented non-defence: `TopologyTests.A_hard_link_planted_before_the_root_was_opened_reaches_the_file_it_names` |

### 4.6 Shared scratch space

The system temporary directory is writable by every account on the machine, so anything this
library puts there is created next to objects an attacker controls. These are the attacks
that follow from that, and they are the reason the scratch helpers exist at all rather than
being left to the caller.

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| SC1 | Guessing the name a scratch object is about to be created under, and creating it first as a symlink | Names drawn from the system's cryptographic generator, wide enough not to be searched | scratch directory and file helpers | `CapTempDirTests.A_name_is_wide_and_spelled_in_one_case`, `.Names_are_not_repeated` |
| SC2 | Taking the name between the check that it is free and its creation | No such sequence exists: the name is claimed by one exclusive operation, which either makes the object or reports the name as taken | scratch directory and file helpers | `DirMutationTests.Creating_refuses_a_name_that_is_already_taken`, `DirFileOpenTests.Claiming_a_name_refuses_one_that_is_taken` |
| SC3 | Reading what a process writes to its own scratch directory | Created reachable only by the account that made it, where the system records that per object | scratch directory helper | `CapTempDirTests.A_scratch_directory_is_closed_to_other_accounts`, `TemporaryHelperSimulationTests.A_scratch_directory_is_asked_for_closed_to_everybody_else` |
| SC4 | Planting a symlink inside a tree that is about to be deleted, aimed at a file outside it | Cleanup descends by handle and unlinks by name; a link is removed as a link and never followed | scratch directory disposal | `CapTempDirTests.Disposal_removes_a_link_without_reaching_what_it_points_at` |
| SC5 | Replacing the scratch directory, or a directory inside it, after it was created | Nothing is reached by name after creation: the handle refers to the object, and a name swapped for a link is refused by the open rather than followed | scratch directory helper | `DirSymlinkPolicyTests` covers the refusal the descent relies on; `TemporaryHelperSimulationTests.Disposal_does_not_empty_a_directory_through_a_link_swapped_in_beneath_it`; stress: `TreeRaceTests.Removing_a_tree_under_attack_never_touches_anything_outside` |

### 4.7 Well-known project directories

An application's configuration, data, cache, state and runtime directories are found from the
environment once, at start-up, and handed out as handles. Where the environment points is
trusted, as it is for every other program the account runs; what follows from it is not.

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| PD1 | Another account reading what an application keeps in its own directories | On Unix every directory the library creates, parents included, is mode `0700`; existing directories are left as they are | project directories | `ProjectDirsTests.Every_directory_created_is_closed_to_other_accounts`, `.A_directory_that_already_exists_keeps_its_permissions` |
| PD2 | A runtime directory owned by another account, or open to one, so that sockets and locks placed there can be reached or pre-empted | The directory is checked on the open handle — a directory, owned by the effective user, no group or other permission bits — and is not used at all otherwise | project directories | `ProjectDirsTests.A_runtime_directory_open_to_other_accounts_is_not_used`, `.A_runtime_directory_must_be_owned_by_the_user_and_closed_to_everybody_else` |
| PD3 | Renaming or replacing a location after it was found, to redirect where the application's directories get created | Whatever is missing is created through the handle opened at start-up, never by path | project directories | `ProjectDirsTests.Nothing_is_resolved_by_path_after_the_directories_are_found` |
| PD4 | A project name carrying a separator, NUL or `..`, to place the directory somewhere else | Refused before anything is opened | project directories | `ProjectLayoutTests.A_name_that_is_not_a_single_component_is_refused`, `.An_application_name_that_names_no_new_directory_is_refused` |

### 4.8 The escape corpus

The rows above are the claims; the escape corpus is what holds them to account. It is a single
table of attacks, each a small tree, a path into it, and the outcome every operation on that
path must come to — refused as an escape, not found, refused for another reason, or done. The
table is data, so an attack is written once and runs everywhere:

- **through every operation that takes a path**, and through each end of the ones that take
  two: opening, creating, describing, reading a link, removing a file, a directory or a whole
  tree, moving, and linking. A removal or a move that resolves its path differently from an
  open is the classic escape, and a corpus run only against opens would not see it;
- **on every backend the host has**, installed in turn for the length of one case, so a Linux
  run covers the kernel-atomic open and the walk and does not depend on which one the machine
  would have picked;
- **under both symbolic-link policies**, the stricter one's answers derived from the other's
  by the rule in §4.2.1 rather than written out twice.

After every operation, whatever it was expected to come to, the corpus checks that nothing
outside the sandbox changed and that nothing the operation let the caller see — a name listed,
bytes read, the identity of an object reached — came from there. A case whose expectation was
written down wrong still cannot pass by leaking. A refusal must also leave the sandbox as it
was, since a caller reading a refusal will not look for what it half-did.

Every absolute target in the corpus points at a directory the test created beside the sandbox.
An attack aimed at a system file would fail for the wrong reason if a bug let it through,
since an ordinary account cannot write there. For the same reason the corpus refuses to run at
all in a process that could bypass file permissions.

Filesystem-dependent cases adapt to the volume they run on rather than assuming one: a case
needing symbolic links or hard links is skipped where the volume has none, and a filesystem
without them must refuse to create one cleanly; a case that names an entry by a different case
or normalisation expects the entry to be reached where the volume folds names and missed where
it does not. The CI workflow runs the corpus on several filesystems, and against a bind mount
prepared inside the sandbox root, which the corpus itself cannot create.

### 4.9 Fuzzing and properties

The corpus can only hold the attacks someone thought of. The code that decides containment is
also run on inputs a machine chose, and checked for what must hold whatever the input. Three
parts are targeted:

- **the path parser**, compared with an independent restatement of its rules;
- **the reader for reparse-point data**, which parses bytes an attacker inside the sandbox can
  write, and is fed buffers that lie about their own lengths and offsets;
- **the component walk**, over simulated trees whose links, mount points and depth the input
  chooses. In every case the walk must look nowhere outside the sandbox, reach nothing
  outside, change nothing outside and leave no handle open.

Property tests apply these checks on every change. libFuzzer applies them nightly with
coverage guidance. Every input the fuzzer has saved is replayed on every change, so a fault
it once found cannot return unnoticed. [fuzzing.md](fuzzing.md) lists each check in full.

#### Known differences between backends

Where backends legitimately disagree, the corpus records the difference as the expectation for
those backends, with the reason, rather than skipping the case. A recorded difference that
stops being true fails its test like any other wrong expectation.

| Case | Backends | Behaviour | Why |
|---|---|---|---|
| `deeper-than-the-walk-descends` | `openat2` | Resolves the path | The depth bound belongs to the walk, which holds a handle per level; the kernel's confined open holds none |
| `longer-than-the-kernel-takes-at-once` | `openat2` | Refused for length, where the walk reports the first missing name | The kernel is handed the whole path at once and refuses one longer than its own limit |

## 5. Explicit non-goals

These are not oversights. Each one is a place where a reader might reasonably expect a
guarantee, so each is stated rather than left implied.

### 5.1 cap-dotnet is not a process sandbox

Code holding a `Dir` can still call `File.Open`, `DllImport("libc")`, `Process.Start`, or
read `/proc/self/mem`. Containment is a property of *this API*, not of the process.

cap-dotnet is the right tool when the untrusted thing is **data** — a path, a filename, an
archive entry, a config value. It is the wrong tool, by itself, when the untrusted thing is
**code**. For that, pair it with an OS-level sandbox (seccomp, AppContainer, a container, a
Wasm runtime). The shipped Roslyn analyzer narrows this gap for *cooperating* code: an
assembly that opts in with `[assembly: CapabilityStrict]` gets a build error for ambient
`System.IO`, sockets, clock and entropy (see [analyzers.md](analyzers.md)). But a compiler
diagnostic is not a security boundary and must never be described as one.

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
`ExhaustionTests.Running_out_of_descriptors_fails_closed_and_leaves_nothing_open` covers it on
Linux and macOS, and every stress race checks on every platform that it left nothing open —
but preventing exhaustion is not.)

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

`DeleteTree` is many syscalls. Directory enumeration is not a snapshot. Where
atomicity *is* provided — the exclusive create behind the temporary-file helpers, the rename
behind the atomic-write helper — it is documented per-operation and is not a general
property.

### 5.6 The network pool is an auditing mechanism, not a containment one

Everything above is about the filesystem, and everything above is enforced by the operating
system: code that was never handed a `Dir` cannot reach what is beneath it, however it asks.

`Cap.Net.Pool` is a different kind of thing wearing a similar shape. There is no equivalent
of "you do not hold the descriptor" for a network address — code that can call anything can
open a socket of its own, exactly as §5.1 says of the filesystem, except that here there is no
kernel-enforced layer underneath to fall back on. A pool constrains sockets opened *through
this library* by code that is cooperating.

That is still worth building, for the same reason the ambient-authority token is worth
carrying: it makes a component's network reach a value that is passed in and can be read off
the call site that built it, rather than a property of the machine it happens to be running
on. A host that genuinely enforces — a WASI runtime, a container network policy, a service
mesh — can be configured from the same list, and the two then agree by construction. What it
must never be called is a boundary.

Three properties of it *are* security-relevant in the ordinary sense, because getting them
wrong would make the audit lie about what was permitted rather than merely fail to enforce it:
an address is reduced to one form before comparison, so the several spellings of one host
cannot be used to pass a check and reach something else; nothing resolves a name, so the
address checked is the address connected to; and a grant over a range never covers the
addresses an interface configures for itself, so a broad grant cannot silently include the
instance metadata service. These are in scope and are tested.

Sockets in the Unix domain are the exception to all of the above. They are filesystem objects
named by a path, they are reached through a `Dir` rather than a pool, and the guarantee in §2
applies to them unchanged — which is why they are refused outright on platforms that offer no
way to name one relative to an open directory, rather than implemented there by resolving a
path and handing it to a socket call.

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

The stress races in `tests/Cap.Stress.Tests` measure it against a real kernel. An attacker
with write access inside the sandbox changes the tree in a tight loop — on another thread, and
for one race in another process — while an operation is repeated through it, and every object
an operation reaches is identified afterwards by its volume and file number. A race fails if
any attempt reaches an object outside the sandbox or one it cannot vouch for, changes anything
outside, or leaves a descriptor open. The races run on every change at ten thousand attempts
and nightly at a million per race and backend, and the nightly run writes its counts to the
job summary.

The window is measured by one race built for the purpose. Two directories take turns at `p`,
and whichever is not in place has its `q` swapped for a stale one and back, so that at every
instant the path `p/q/f` names a fresh file. Reaching a stale file means resolution saw `p`
at one moment and `q` at another. On Linux, over a million resolutions (8 processors, one
attacker thread):

| Backend | Reached the file the path named | Steered to a different file inside | Reached outside |
|---|--:|--:|--:|
| `openat2` | 1,000,000 | 0 | 0 |
| component walk | 999,995 | 5 | 0 |

The rate depends on how fast the attacker can make its changes relative to the resolution,
so the number to carry away is its order — a handful per million against an attacker doing
nothing else — and not its exact value. The same run's other races, where the attacker swaps a
directory for a link pointing outside, recreates a component as another kind of object, or
replaces the last component of a write, came to between a third and two thirds of attempts
refused as escapes on both backends, and none reaching outside.

Two defects the races found were fixed rather than recorded. A handle disposed on another
thread part-way through a call was reported as an unexplained I/O failure rather than as
disposed; it now throws `ObjectDisposedException`, and the call was never made against the
handle's number in either case. And the walk reported a name swapped between two of its
steps as an invalid argument or a link loop; it now looks at the name again, within the link
budget, as the kernel-atomic backend does with its own lost races.

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

Auditable includes at run time: a process can be told to record every acquisition against
its call site and print the list, which covers the acquisitions a search through the
application's own source does not — the ones inside its dependencies. See
[ambient-authority.md](ambient-authority.md).

### 6.5 Silently weakening containment is itself a vulnerability

A capability probe that fails open, or a demotion from the kernel-atomic backend to a walk
that goes unreported, leaves callers believing they have §2's guarantee when they have
§6.1's. No escape need be demonstrated for that to be a security bug; it is in scope for
private disclosure either way. This is why the active backend and its call counts are
required to be observable at runtime rather than being an implementation detail — see
[backends.md](backends.md#which-backend-is-running) for `Dir.ResolutionBackend` and the
instruments on the `Cap.Primitives` meter.

## 7. Reporting

See [SECURITY.md](../SECURITY.md). Sandbox escapes are security issues; please report them
privately rather than opening a public issue.
