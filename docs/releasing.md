# Releasing

cap-dotnet is a security boundary, so how it is released is part of what it promises. A
consumer should be able to tell that a package came from this repository, built by its CI
from a known commit, and to rebuild that commit and get the same assemblies back. This page
covers what is published, how versions are numbered, how a release is cut, and how to check a
package you have downloaded.

## What is published

| Package | Contents |
|---|---|
| `Cap.Std` | `Cap.Std.dll`, `Cap.Primitives.dll`, and the analyzer under `analyzers/dotnet/cs` |
| `Cap.Fs.Ext` | `Cap.Fs.Ext.dll`; depends on `Cap.Std` |
| `Cap.Net` | `Cap.Net.dll`; depends on `Cap.Std` |
| `Cap.Time` | `Cap.Time.dll`; depends on `Cap.Std` |
| `Cap.Rand` | `Cap.Rand.dll`; depends on `Cap.Std` |
| `Cap.Directories` | `Cap.Directories.dll`; depends on `Cap.Std` |

There is no `Cap.Primitives` package. The assembly holds the few public types every package
shares, such as `AmbientAuthority`, `CapPath` and `SymlinkPolicy`, together with the
resolution code behind them. It ships inside `Cap.Std`, so nobody takes a dependency on it as
a separate thing with its own version. The analyzer is not a package of its own either. Every
other package depends on `Cap.Std` with all of its assets, so the analyzer reaches a project
that installs only `Cap.Time`, say, as well as one that installs `Cap.Std` directly.

Each package carries `LICENSE` (MIT), `NOTICE` and a readme. Each assembly embeds its PDB,
with SourceLink pointing at the commit it was built from, so a debugger can step into the
source without a symbol server.

## Versioning

Versions follow [SemVer 2.0](https://semver.org/). Until 1.0 every release is `0.y.z`:

- `y` goes up for a change to the public API that breaks source or binary compatibility, and
  for a new feature;
- `z` goes up for a fix.

**A fix to a containment bug ships in a patch release, even when it changes behaviour.** If a
path used to reach outside the sandbox and now throws `SandboxEscapeException`, then code that
depended on the old behaviour was depending on an escape. That change will not wait for the
next minor version, and it will not be held back to preserve compatibility. The advisory for
the fix says what changed.

The project stays at 0.x until both of these are true:

- the escape corpus has stayed the same across a whole release, with no case added because of
  an escape found after release, and no case weakened;
- the public API has had a review from someone outside the project.

The release workflow refuses a tag outside `v0.y.z`, so moving to 1.0 means changing that
check on purpose. It can't happen by a typo.

Pre-1.0, only the latest release is supported ([SECURITY.md](../SECURITY.md#supported-versions)).

## Cutting a release

1. Open an issue from the **Release checklist** template and work through it. It asks for the
   escape corpus, the nightly stress and fuzz runs, the NativeAOT jobs and the benchmarks to
   be green, for the threat model to be reviewed against what the release changes, and for the
   advisory process to have been tried out with a dry run.
2. Tag the commit on `main` and push the tag:

   ```bash
   git tag -s v0.2.0 -m "cap-dotnet 0.2.0"
   git push origin v0.2.0
   ```

3. The [release workflow](../.github/workflows/release.yml) packs the six packages and checks
   their contents. It builds them a second time from another directory and confirms every
   assembly is identical. It attests their provenance, and installs them into fresh projects
   on Linux, Windows and macOS.
4. Publishing waits for approval on the `release` environment. Once approved, the workflow
   pushes the packages to nuget.org and creates a GitHub release with the same `.nupkg` files
   attached. A version with a pre-release suffix, such as `v0.2.0-rc.1`, becomes a
   pre-release on GitHub and on nuget.org.

CI runs steps 3 and 4 up to publishing on every change: the same pack script, the same checks,
and the same installs. So a release shouldn't fail in any way a pull request didn't.

### One-time setup

These live outside the repository and have to be in place before the first release:

- **nuget.org trusted publishing.** On nuget.org, add a trusted publishing policy for this
  repository with the workflow file `release.yml` and the environment `release`. Set the
  repository secret `NUGET_USER` to the nuget.org account name (the profile name, not the
  email) that owns the packages. There is no API key to store.
- **The `release` environment.** Create it under *Settings → Environments*, require a
  reviewer, and allow deployments only from tags matching `v*`.
- **Private vulnerability reporting.** Turn it on under *Settings → Code security*;
  [SECURITY.md](../SECURITY.md) sends reporters there.

## Verifying a package

Every release carries a [build provenance attestation](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations)
signed through Sigstore. It records the repository, the workflow and the commit that produced
each file. The attestation covers each `.nupkg` as built, and each assembly inside one. You
need the [GitHub CLI](https://cli.github.com/).

**A package from the GitHub release** can be checked whole:

```bash
gh release download v0.2.0 --repo sjp/cap-dotnet --pattern 'Cap.Std.*.nupkg'
gh attestation verify Cap.Std.0.2.0.nupkg --repo sjp/cap-dotnet
```

**A package from nuget.org** is checked through its assemblies. nuget.org adds its own
repository signature to every package it accepts, which changes the `.nupkg` file but not the
assemblies in it:

```bash
gh attestation verify ~/.nuget/packages/cap.std/0.2.0/lib/net10.0/Cap.Std.dll --repo sjp/cap-dotnet
gh attestation verify ~/.nuget/packages/cap.std/0.2.0/lib/net10.0/Cap.Primitives.dll --repo sjp/cap-dotnet
gh attestation verify ~/.nuget/packages/cap.std/0.2.0/analyzers/dotnet/cs/Cap.Analyzers.dll --repo sjp/cap-dotnet
```

`dotnet nuget verify --all Cap.Std.0.2.0.nupkg` checks nuget.org's repository signature on the
same file. That signature shows the package came through nuget.org. The attestation shows
where it was built.

### Rebuilding from source

The assemblies are reproducible. Check out the tag and pack with `CI=true`, which maps source
paths to a fixed root so the directory you build in doesn't reach the output:

```bash
git checkout v0.2.0
CI=true build/ci/pack.sh ./rebuilt 0.2.0
```

Each assembly under `./rebuilt` should be byte-for-byte identical to the published one,
provided the .NET SDK is the version `global.json` names. CI checks this on every change with
[`build/ci/check-reproducible.sh`](../build/ci/check-reproducible.sh), which builds twice from
two directories and compares the results.

## Licence

cap-dotnet is MIT-licensed. It ports cap-std's design, but no code or test data was copied
from it. [`NOTICE`](../NOTICE) records this, and says what to do if material is ever borrowed
from upstream.
