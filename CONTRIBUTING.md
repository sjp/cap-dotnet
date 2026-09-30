# Contributing

cap-dotnet is a security boundary, so most of what follows is about keeping that boundary
provable: every claim it makes has a test that would fail if the claim stopped being true, and
nothing weakens containment quietly. This page collects the rules that are otherwise spread
across the docs, and links to where each one is explained.

**A suspected sandbox escape is not a pull request.** Report it privately first, as
[SECURITY.md](SECURITY.md) describes, even if you already have the fix.

## Building and testing

```bash
dotnet build CapDotnet.slnx -c Release
dotnet test  CapDotnet.slnx -c Release
```

The SDK is pinned in `global.json`; the devcontainer installs it. The runner is
Microsoft.Testing.Platform, so one project runs with
`dotnet test tests/<Project>/<Project>.csproj -c Release`, and a filter goes after `--`:
`-- --filter-class '*ClassName*'` or `-- --filter-method '*MethodName*'`.

**Do not run the tests with the power to bypass file permissions.** On Unix that means not as
root. On Windows an administrator token is fine, and needed for the symbolic links the suite
attacks, but a backup, restore or take-ownership privilege is not. Every test assembly refuses
to start otherwise, because a negative containment test passes for the wrong reason in a
process that outranks the permission system. Note that bash on Windows (MSYS2) enables those
privileges in an administrator's token; run the tests from PowerShell or `cmd`.

Tests that need something the host may lack (symbolic links, hard links, junctions, `/proc`)
skip where it is missing. CI names what each runner has in `CAPDOTNET_EXPECT_HOST_FEATURES`,
so there a missing feature fails instead; see
[docs/testing.md](docs/testing.md#host-features-the-suite-expects).

The suites that pin down the disk's behaviour also run with the in-memory filesystem from
`Cap.Std.Testing` in its place. Set `CAPDOTNET_TEST_BACKEND` to `in-memory-walk` or
`in-memory-confined` to run a leg locally. A test about the host itself opts out with
`[NotInMemory("why")]`; CI prints the count, and an opt-out added to hide a disagreement with
the disk is a bug in the in-memory filesystem, not a fix.

## Rules for code under `src/`

- **No ambient authority.** Product code does not use `System.IO` paths, the clock or entropy
  directly. The analyzer in `src/Cap.Analyzers` makes that a build error for every assembly
  under `src/`, and `Cap.Primitives` also bans `System.IO.Path` outright. The few places that
  must reach the real thing suppress the rule at that line and say why; see
  [docs/analyzers.md](docs/analyzers.md#this-librarys-own-build).
- **Warnings are errors**, everywhere, and `dotnet format --verify-no-changes` runs in CI.
- **Resolution behaviour answers to the threat model.** Before changing anything in
  `src/Cap.Primitives` or a backend, read [docs/threat-model.md](docs/threat-model.md). Every
  behaviour of the resolvers must be justifiable against one of its numbered rows. A change to
  what is defended, what is out of scope or the residual risk updates the threat model in the
  same pull request.
- **Weakening containment silently is a vulnerability**, even with no escape shown
  (threat model §6.5). A capability probe that fails open, or a fallback that goes unreported,
  is treated as one.
- **New analyzer rules** are listed in `src/Cap.Analyzers/AnalyzerReleases.Unshipped.md`;
  the build fails otherwise. They move to `AnalyzerReleases.Shipped.md` at release.

## A fix lands with its test

A bug fix comes with the test that would have caught it. For containment this means a case in
the escape corpus, `tests/Cap.Escape.Tests/EscapeCorpus.cs`: a small tree, a path into it, and
the outcome every operation on that path must reach. The corpus runs it through every
operation, on every backend the host has, under both symbolic-link policies, and checks that
nothing outside the sandbox changed. An attack the table cannot describe (a mount, a second
root) is a test of its own, marked `[Defends("T1")]` with the threat-model rows it covers, so
that the suite can check every row has something defending it. See
[threat model §4.8](docs/threat-model.md#48-the-escape-corpus).

Cases are added, not weakened. A change that relaxes an existing case's expected outcome needs
an argument in the pull request that the old outcome was wrong.

## Coverage, benchmarks and fuzzing

- **Coverage** of `Cap.Primitives` is gated in CI on the merged report of every leg. The floors
  only go up; lowering one needs a reason in the pull request. See
  [docs/testing.md](docs/testing.md#coverage-of-this-library).
- **Benchmarks.** CI compares the hot paths against `bench/baselines/<os>.json` and fails a
  row more than 10% worse. A change that is slower on purpose moves the baseline in the same
  commit, preferably with figures from the platform's CI artifact rather than a laptop; see
  [docs/benchmarks.md](docs/benchmarks.md#moving-the-baseline).
- **Fuzzing.** An input the fuzzer saves under `fuzz/regressions/<target>/` is a failing test
  once committed. Fix the fault and commit the input with the fix; see
  [docs/fuzzing.md](docs/fuzzing.md#starting-inputs-and-saved-inputs).

## Pull requests

- Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for anything a user of
  the packages would notice. A change to containment behaviour goes under **Security** and
  says what a caller will now see.
- Keep a pull request to one change, and say in its description which threat-model rows it
  touches, if any. The pull request template has the checklist.
- There is no sign-off or CLA requirement. Contributions are accepted under the repository's
  [MIT licence](LICENSE); anything borrowed from another project is named in [NOTICE](NOTICE).

Releases are cut by the maintainer, following [docs/releasing.md](docs/releasing.md).
