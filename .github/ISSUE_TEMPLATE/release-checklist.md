---
name: Release checklist
about: Everything to confirm before tagging a release
title: "Release v0.y.z"
labels: release
---

Version: `v0.y.z`
Commit to tag: <!-- full SHA on main -->

See [docs/releasing.md](../../docs/releasing.md) for the process and the versioning policy.

## The boundary still holds

- [ ] **CI** is green on the commit to tag, on every leg: the escape corpus on Linux (openat2
      and the forced fallback), Linux arm64, Windows and macOS; the escape corpus on each
      filesystem; the runs with openat2 denied; and the package jobs.
- [ ] **NativeAOT and trimming** jobs are green on all three platforms.
- [ ] **Nightly stress** has passed at full size on this commit or a later one, on every
      platform. Link the run:
- [ ] **Nightly fuzzing** has passed on this commit or a later one, and every input it saved
      since the last release has been triaged. Link the run:
- [ ] **Benchmarks** show no regression past the gate against the committed baseline. Link
      the run:

## What this release changes

- [ ] Read the diff since the last release against [docs/threat-model.md](../../docs/threat-model.md).
      Update the threat model for anything that changes what is defended, what is out of
      scope or the residual risk, or note here that nothing did.
- [ ] Any change in containment behaviour, including one that fixes an escape, is described
      in the release notes. A containment fix still ships as a patch release.
- [ ] The public API changes match the version bump (minor for a breaking change or a new
      feature, patch for a fix).
- [ ] `AnalyzerReleases.Unshipped.md` entries have been moved to `AnalyzerReleases.Shipped.md`
      under this version.
- [ ] If anything was borrowed from upstream or another project since the last release,
      `NOTICE` names it.

## The advisory path works

- [ ] Private vulnerability reporting is enabled on the repository.
- [ ] The reporting contacts in [SECURITY.md](../../SECURITY.md) are current, including a
      fallback for reporters without a GitHub account.
- [ ] A dry run has been done since the last release: file a private report, draft an
      advisory from it, request a CVE up to the point of submission, and close it without
      publishing. Note the date and anything that did not work:

## Publish

- [ ] Tag signed and pushed: `git tag -s v0.y.z -m "cap-dotnet 0.y.z" && git push origin v0.y.z`
- [ ] Release workflow green up to the `release` environment, then approved.
- [ ] Packages visible on nuget.org, and `gh attestation verify` succeeds for an assembly
      from a freshly restored package.
