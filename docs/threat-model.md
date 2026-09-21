# cap-dotnet threat model

**Status:** draft — ratified by issue [0002](../issues/0002-threat-model.md).
**Audience:** contributors to this repository, and reviewers deciding whether cap-dotnet is
an acceptable security control for their system.

This document is written *before* the resolution code, on purpose. Every design decision in
[0004](../issues/0004-cap-path-type.md) (path parsing),
[0006](../issues/0006-linux-openat2.md)/[0007](../issues/0007-portable-component-walk.md)/[0008](../issues/0008-windows-resolution.md)
(resolution) and [0011](../issues/0011-symlink-policy.md) (symlink policy) has to be
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
  it has leaked information and is a bug. See [0011](../issues/0011-symlink-policy.md) on
  dangling symlinks.

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

Each row must, by the time [0023](../issues/0023-escape-test-corpus.md) lands, name the test
that covers it. The **Test** column is intentionally empty for now; leaving it blank is a
visible reminder that the claim is unverified.

### 4.1 Lexical

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| L1 | `..` component in caller input | Rejected at parse time; never collapsed lexically | [0004](../issues/0004-cap-path-type.md) | |
| L2 | Absolute path (`/etc/passwd`, `C:\Windows`) | Rejected | 0004 | |
| L3 | Drive-relative (`C:file`) and root-relative (`\file`) on Windows | Rejected | 0004 | |
| L4 | UNC (`\\server\share`) and device namespace (`\\?\`, `\\.\`) | Rejected | 0004 | |
| L5 | Empty component, `.`, repeated separators | Normalised or rejected, never silently skipped past a check | 0004 | |
| L6 | Very long paths / deep nesting | Bounded; fails cleanly rather than stack-overflowing | 0007 | |

`..` deserves a note. The obvious implementation — collapse `a/../b` to `b` before touching
the disk — is **wrong**, because if `a` is a symlink to `/etc` then the kernel resolves
`a/../b` to `/b`, not `./b`. `System.IO.Path.GetFullPath` does exactly this collapsing,
which is why it is banned inside `Cap.Primitives` by the build.

### 4.2 Symlinks

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| S1 | Symlink to an absolute path outside the sandbox | Rejected by default ([0011](../issues/0011-symlink-policy.md)) | 0006/0007/0008 | |
| S2 | Relative symlink escaping via `..` | Rejected | 0007 | |
| S3 | Symlink chain that stays inside | Followed | 0007 | |
| S4 | Symlink chain exceeding the budget | Fails as `ELOOP`, does not hang | 0007 | |
| S5 | Symlink in a *non-final* component | Same rules as any other component | 0007 | |
| S6 | Dangling symlink pointing outside | Reported as not-found **without** revealing whether the target exists | 0011 | |
| S7 | `/proc/self/fd/N`, `/proc/self/root` and other magic links (Linux) | Rejected (`RESOLVE_NO_MAGICLINKS`, and explicitly in the fallback) | 0006/0007 | |
| S8 | Windows junction / mount point (always absolute) | Rejected | 0008 | |
| S9 | Windows `IO_REPARSE_TAG_APPEXECLINK`, `IO_REPARSE_TAG_WCI_LINK`, unknown tags | Rejected — these are not filesystem links and must not be interpreted as such | 0008 | |

### 4.3 Windows name handling

This is where cap-std itself shipped a CVE
([GHSA-hxf5-99xg-86hw](https://github.com/bytecodealliance/cap-std/security/advisories/GHSA-hxf5-99xg-86hw)),
so it gets its own section rather than a bullet.

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| W1 | Reserved device names: `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9` | Rejected | [0009](../issues/0009-windows-reserved-names.md) | |
| W2 | Mangled variants: `CON.txt`, `CON.`, `CON ` (trailing space), `con`, `CoN` | Rejected | 0009 | |
| W3 | Superscript digit forms `COM¹`, `COM²`, `COM³`, and `CONIN$` / `CONOUT$` | Rejected | 0009 | |
| W4 | Alternate data streams: `file:stream`, `CON::$DATA` | Rejected (`:` is not a legal component character) | 0009 | |
| W5 | Trailing dots and spaces, which Win32 silently strips *after* validation | Rejected before they can diverge | 0004/0009 | |
| W6 | 8.3 short names (`PROGRA~1`) aliasing a long name | Policy stated and enforced consistently | 0008 | |
| W7 | Wildcards `* ? < > "` reaching `NtCreateFile` | Rejected | 0009 | |

A parse-time blocklist is necessary but **not sufficient**: the set of device names is a
property of the OS and has grown before. 0009 therefore also requires a *post-open* device
check, so that a name we failed to anticipate still cannot be used.

### 4.4 macOS and case-insensitive volumes

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| M1 | Unicode normalisation: NFC vs NFD forms of the same filename | Documented and consistent; must not allow a check to be bypassed by re-encoding | 0004 | |
| M2 | Case-insensitive volume: `secret` vs `SECRET` | Containment must not depend on case-sensitive string comparison | 0008/0023 | |
| M3 | `/tmp` and `/var` being symlinks to `/private/*` | Resolved once, under ambient authority, at root acquisition | [0017](../issues/0017-temp-dir-file.md) | |

### 4.5 Filesystem topology

| # | Attack | Required behaviour | Where | Test |
|---|---|---|---|---|
| T1 | Mount point or bind mount appearing under the sandbox | Documented; `RESOLVE_NO_XDEV` available as a policy | 0006 | |
| T2 | Hardlink creation crossing the sandbox boundary | Requires a capability on *both* sides ([0012](../issues/0012-dir-public-api.md)) | 0012 | |
| T3 | Rename crossing the boundary | Same: both `Dir`s required | 0012 | |
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
Wasm runtime). The analyzer in [0026](../issues/0026-ambient-api-analyzer.md) narrows this
gap for *cooperating* code by making ambient `System.IO` a build error, but a compiler
diagnostic is not a security boundary and must never be described as one.

### 5.2 Not a defense against a privileged attacker

An attacker with `CAP_SYS_ADMIN`, `CAP_DAC_OVERRIDE`, `SeBackupPrivilege`,
`SeRestorePrivilege`, or the ability to mount filesystems or modify the sandbox root's own
ancestors defeats the model. This is also why the test suite asserts it is **not** running
elevated: as root, several negative tests would pass for the wrong reason.

### 5.3 No resource limits

Disk quota, inode exhaustion, file-descriptor exhaustion, file count, and file size are out
of scope. A caller holding a `Dir` can fill the volume. (Handle exhaustion *behaviour* is in
scope to the extent that running out of descriptors must not cause a containment failure —
see [0024](../issues/0024-toctou-stress.md) — but preventing exhaustion is not.)

### 5.4 Not a confidentiality boundary for what the handle already reveals

The sandbox root's own device and inode numbers, its timestamps, and the fact of its
existence are observable through any handle derived from it.

### 5.5 No promise of atomicity across operations

`Dir.RemoveDirRecursive` is many syscalls. Directory enumeration is not a snapshot. Where
atomicity *is* provided — the exclusive create in [0017](../issues/0017-temp-dir-file.md),
the rename in the atomic-write helper of [0018](../issues/0018-cap-fs-ext.md) — it is
documented per-operation and is not a general property.

## 6. Residual risk

### 6.1 The fallback resolver narrows TOCTOU; it does not close it

On Linux ≥ 5.6, `openat2(RESOLVE_BENEATH)` makes resolution a single kernel-atomic
operation and the race does not exist. Everywhere else — older kernels, seccomp filters that
block `openat2`, macOS, Windows — resolution is a loop of per-component opens
([0007](../issues/0007-portable-component-walk.md), [0008](../issues/0008-windows-resolution.md)).
Each step is safe, and because every step is *handle-relative* an attacker cannot redirect
resolution to a different subtree. But between two steps a component inside the sandbox can
be swapped for another component inside the sandbox.

The consequence is bounded and worth stating plainly: **an attacker who can write inside the
sandbox may be able to steer an operation to a different object that is also inside the
sandbox.** They cannot steer it outside. For most uses — untrusted archive extraction,
per-tenant storage — that is the property that matters.

0024 must **quantify** this window rather than describe it, and the result belongs in this
document.

### 6.2 `..` on the fallback path

Parent traversal is handled by popping a handle off a stack we already hold, never by
`openat(fd, "..")`. This matters: `openat(fd, "..")` asks the kernel to resolve the parent
of whatever `fd` currently refers to, which a concurrent rename can change. Popping a held
handle cannot be raced.

### 6.3 Hardlinks planted before the sandbox was opened

If a hardlink to `/etc/shadow` already exists inside the sandbox when the `Dir` is opened,
cap-dotnet will happily open it: it *is* reachable by descending, and there is no way to
distinguish it from a legitimate file. This is a property of hardlinks, not a bug.
Applications that accept untrusted content into a directory they later sandbox should
create that directory on a filesystem where the untrusted party cannot create hardlinks to
outside files — in practice, a dedicated mount or a directory they own exclusively.

### 6.4 The root itself is resolved with ambient authority

Opening the first `Dir` requires an `AmbientAuthority` token
([0016](../issues/0016-ambient-authority.md)) and resolves an ordinary path with ordinary
ambient rules. Everything the model promises begins *after* that. The token exists to make
that moment greppable and auditable, not to make it safe.

## 7. Reporting

See [SECURITY.md](../SECURITY.md). Sandbox escapes are security issues; please report them
privately rather than opening a public issue.
