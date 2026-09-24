#!/usr/bin/env bash
# Packs the repository twice, from two different directories, and checks that every
# assembly in the packages comes out byte for byte the same.
#
# A reproducible assembly lets anyone rebuild a release from its tagged commit and compare
# the result with what was published, rather than taking the published binary on trust. The
# second build runs from a copy of the checkout at another path, so an absolute path leaking
# into an assembly or its embedded PDB counts as a failure. ContinuousIntegrationBuild maps
# source paths to a fixed root, and this is what shows it held.
#
# The .nupkg files themselves are not compared. The zip format records a timestamp for each
# entry, so the packages differ from one pack to the next even when their contents do not.
#
# Run it on a clean CI checkout: the copy is taken from the working tree, bin/ and obj/ aside.
#
# Usage: check-reproducible.sh <work-dir>
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="${1:?usage: check-reproducible.sh <work-dir>}"
version="0.0.0-reproducible"

rm -rf "$work"
mkdir -p "$work/second" "$work/first-extracted" "$work/second-extracted"

export CI=true

"$repo/build/ci/pack.sh" "$work/first" "$version"

tar -C "$repo" --exclude=./artifacts --exclude=bin --exclude=obj -cf - . | tar -C "$work/second" -xf -
"$work/second/build/ci/pack.sh" "$work/second-packages" "$version"

failed=0
for nupkg in "$work/first"/*.nupkg; do
  name="$(basename "$nupkg" .nupkg)"
  unzip -q "$nupkg" '*.dll' -d "$work/first-extracted/$name"
  unzip -q "$work/second-packages/$name.nupkg" '*.dll' -d "$work/second-extracted/$name"
done

while IFS= read -r -d '' dll; do
  relative="${dll#"$work/first-extracted/"}"
  other="$work/second-extracted/$relative"
  if [[ ! -f "$other" ]]; then
    echo "FAILED: $relative is missing from the second build" >&2
    failed=1
  elif ! cmp -s "$dll" "$other"; then
    echo "FAILED: $relative differs between the two builds" >&2
    failed=1
  fi
done < <(find "$work/first-extracted" -name '*.dll' -print0)

if [[ "$failed" != 0 ]]; then
  exit 1
fi

echo "Every packed assembly is identical across two builds from different directories."
