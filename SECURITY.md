# Security policy

cap-dotnet exists to be a security boundary, so a bug that breaks the boundary is not an
ordinary bug. This file says what counts, how to report it, and what we will do.

The precise statement of what is and is not promised lives in
[docs/threat-model.md](docs/threat-model.md). Please read §5 ("Explicit non-goals") before
reporting — several things that look like escapes are documented limits, and one of them
(cap-dotnet is not a process sandbox) is the single most common misunderstanding.

## Reporting a vulnerability

**Please do not open a public issue for a suspected sandbox escape.**

Use GitHub's private vulnerability reporting on this repository
(*Security* → *Report a vulnerability*), which opens a private advisory visible only to
maintainers.

> **TODO(0030):** add a fallback email address and its PGP key here before the first
> public release. GitHub private reporting alone is not enough for reporters who do not
> have, or do not want, a GitHub account.

A useful report contains:

- the platform, OS version, and filesystem (`ext4`, `APFS`, `NTFS`, `overlayfs`, tmpfs…);
- on Linux, whether `openat2` was in use — the fallback resolver has a documented residual
  race that `openat2` does not (threat model §6.1), and which path you hit changes the
  severity;
- the sequence of `Dir` operations, and the on-disk layout they ran against;
- what was reached, and how you confirmed it was outside the sandbox. Device and inode
  numbers (`st_dev`/`st_ino`, or `VolumeSerialNumber` + `FileId`) are the right evidence —
  a path string is not, because a path can look outside while naming something inside.

## What is in scope

- Reaching, reading, writing, creating, deleting, or renaming any filesystem object not
  reachable by descending from the sandbox root.
- Learning whether a file outside the sandbox exists, including by distinguishing error
  codes or timing.
- Any way to obtain a `Dir`, `CapFile`, or handle with wider authority than the one it was
  derived from.
- A crash, hang, or unbounded resource consumption triggered by attacker-controlled path
  input or attacker-controlled on-disk link structure.
- Anything that causes the API to be *silently* less contained than documented — for
  example, a capability probe that fails open, or an `openat2` demotion to the fallback
  that goes unreported.

That last one is worth emphasising: a change that makes containment weaker without failing
loudly is treated as a vulnerability even if no escape has been demonstrated.

## What is out of scope

- Ambient `System.IO` use by the calling application. cap-dotnet does not, and cannot,
  prevent this (threat model §5.1).
- Attacks requiring root, `CAP_SYS_ADMIN`, `SeBackupPrivilege`, or control of the sandbox
  root's ancestors (§5.2).
- Resource exhaustion — filling the disk or the inode table from inside the sandbox (§5.3).
- Hardlinks to outside files that already existed inside the sandbox before the `Dir` was
  opened (§6.3).
- Redirection to a *different object inside the same sandbox* via a concurrent rename on a
  non-`openat2` backend. This is the documented residual TOCTOU window (§6.1). Reports that
  measurably widen the window, or that escape the sandbox entirely, are in scope.

If you are unsure which side of the line something falls on, report it privately. We would
much rather triage an out-of-scope report than miss an in-scope one.

## Our commitments

| | |
|---|---|
| Acknowledge the report | within 3 working days |
| Initial assessment (in scope? severity?) | within 10 working days |
| Fix or a published mitigation | within 90 days of acknowledgement |

We will credit reporters in the advisory unless asked not to, request a CVE for confirmed
escapes, and publish a GitHub Security Advisory when the fix ships.

## Supported versions

Pre-1.0, only the latest released version is supported. See
[issues/0030](issues/0030-packaging-release.md) for the versioning policy — note in
particular that **a containment fix may change behaviour in a patch release**. Code that
depended on an escape working was depending on a bug.

## A note on the test suite

The adversarial corpus ([0023](issues/0023-escape-test-corpus.md)) and the TOCTOU stress
harness ([0024](issues/0024-toctou-stress.md)) are the primary defence here, and they are
public. If you find an escape, the fix is expected to land together with the test case that
would have caught it.
