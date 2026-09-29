#!/usr/bin/env bash
# Rewrites Directory.Packages.props so that every System.IO.Abstractions and Testably package
# is at the newest stable version on nuget.org, for the CI job that builds and tests
# Cap.IO.Abstractions against them.
#
# Usage: use-latest-io-abstractions.sh [props-file]
#
# Cap.IO.Abstractions depends on a minimum of each interface package, not a single minor
# version, so a consumer can install a newer one beside it. The interfaces gain members in
# minor releases, and a member the adapter lacks makes DirFileSystem fail to load with
# TypeLoadException. Building here against the newest versions finds that before a release
# rather than after one: a failure means the adapter has members to implement.
#
# The runtime dependencies keep their range syntax, with the floor raised; the test doubles
# get the plain version. The file is changed in place, so run this only in a throwaway
# checkout.
set -euo pipefail

props="${1:-Directory.Packages.props}"
feed=https://api.nuget.org/v3-flatcontainer

ids=$(grep -oE 'Include="(TestableIO\.System\.IO\.Abstractions|Testably\.Abstractions)[^"]*"' "$props" |
  sed -E 's/^Include="(.*)"$/\1/')

if [[ -z "$ids" ]]; then
  echo "No System.IO.Abstractions or Testably packages in $props." >&2
  exit 1
fi

for id in $ids; do
  lower=$(tr '[:upper:]' '[:lower:]' <<<"$id")
  # The flat container lists versions in ascending order; a prerelease has a hyphen.
  latest=$(curl -fsSL --retry 3 "$feed/$lower/index.json" | jq -r '[.versions[] | select(contains("-") | not)] | last')
  if [[ -z "$latest" || "$latest" == null ]]; then
    echo "No stable version of $id on nuget.org." >&2
    exit 1
  fi

  pattern="(<PackageVersion Include=\"${id//./\\.}\" Version=\")"
  if grep -qE "${pattern}\[" "$props"; then
    sed -i -E "s|${pattern}\[[^,]*,|\1[$latest,|" "$props"
  else
    sed -i -E "s|${pattern}[^\"]*\"|\1$latest\"|" "$props"
  fi

  echo "$id: $(grep -oE "${pattern}[^\"]*\"" "$props" | sed -E 's/.*Version="([^"]*)"/\1/')"
done
