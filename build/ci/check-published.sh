#!/usr/bin/env bash
# Checks each package in a feed against the same version on nuget.org, if one is there, and
# fails if any file in them differs.
#
# The release pushes with --skip-duplicate, so that re-running a release that stopped halfway
# publishes the packages it had not reached yet. But nuget.org then skips a version it already
# has without looking at it, and versions there cannot be replaced. A package published from
# some other build would stay up beside an attestation for this one, and the attestation would
# not match it. This check runs first, so a release whose packages differ from the ones already
# published stops before it pushes any of them.
#
# Every file in the package is compared except the ones nuget.org or the packing changes:
# .signature.p7s, which nuget.org adds on upload, and the zip's packaging metadata
# ([Content_Types].xml, _rels/ and package/), which says nothing about what the package holds.
#
# One window is not covered: a package pushed in the last few minutes may not be served yet, so
# it looks unpublished here and the push then skips it unchecked.
#
# Usage: check-published.sh <feed-dir> <work-dir>
set -euo pipefail

feed="${1:?usage: check-published.sh <feed-dir> <work-dir>}"
work="${2:?usage: check-published.sh <feed-dir> <work-dir>}"

rm -rf "$work"
mkdir -p "$work"

failed=0
shopt -s nullglob
nupkgs=("$feed"/*.nupkg)
if [[ ${#nupkgs[@]} == 0 ]]; then
  echo "FAILED: no packages in $feed" >&2
  exit 1
fi

for nupkg in "${nupkgs[@]}"; do
  nuspec="$(unzip -p "$nupkg" '*.nuspec')"
  id="$(sed -n 's:.*<id>\(.*\)</id>.*:\1:p' <<<"$nuspec" | head -n 1)"
  version="$(sed -n 's:.*<version>\(.*\)</version>.*:\1:p' <<<"$nuspec" | head -n 1)"
  lower_id="${id,,}"
  lower_version="${version,,}"
  url="https://api.nuget.org/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"
  published="$work/$id.$version.published.nupkg"

  status="$(curl -sS -L --retry 3 -o "$published" -w '%{http_code}' "$url")"
  case "$status" in
    404)
      echo "$id $version is not on nuget.org yet."
      continue
      ;;
    200) ;;
    *)
      echo "FAILED: $url answered $status, so whether $id $version is published is unknown" >&2
      failed=1
      continue
      ;;
  esac

  ours="$work/$id.$version/ours"
  theirs="$work/$id.$version/theirs"
  mkdir -p "$ours" "$theirs"
  unzip -q "$nupkg" -d "$ours"
  unzip -q "$published" -d "$theirs"
  for dir in "$ours" "$theirs"; do
    rm -rf "$dir/.signature.p7s" "$dir/[Content_Types].xml" "$dir/_rels" "$dir/package"
  done

  if diff -rq "$ours" "$theirs" >&2; then
    echo "$id $version is already on nuget.org with the same contents, so the push will skip it."
  else
    echo "FAILED: $id $version is already on nuget.org with different contents" >&2
    failed=1
  fi
done

if [[ "$failed" != 0 ]]; then
  exit 1
fi
