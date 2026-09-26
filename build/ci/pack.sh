#!/usr/bin/env bash
# Packs every package this repository publishes, and checks that each one holds what it is
# meant to and nothing it is not.
#
# The package list is written out here rather than taken from `dotnet pack` over the
# solution, so that a project which becomes packable by accident is not published by
# accident. The checks cover what a consumer relies on and a build would not notice missing:
#
#   - Cap.Primitives travels inside Cap.Std, and there is no package of its own to depend on;
#   - the analyzer is in Cap.Std, and every other package passes it on rather than excluding it;
#   - Cap.Std.Testing depends on exactly the Cap.Std it was built with, since it implements a
#     contract inside Cap.Std that can change in any release;
#   - Cap.Std.Testing carries the build file that warns a project installing it that is not a
#     test project;
#   - each package carries its licence, notice and readme, and records the commit it was built
#     from, which is what SourceLink resolves sources against.
#
# Usage: pack.sh <output-dir> <version>
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:?usage: pack.sh <output-dir> <version>}"
version="${2:?usage: pack.sh <output-dir> <version>}"

packages=(Cap.Std Cap.Fs.Ext Cap.Net Cap.Time Cap.Rand Cap.Directories Cap.Std.Testing)

mkdir -p "$out"
rm -f "$out"/*.nupkg

for project in "${packages[@]}"; do
  dotnet pack "$repo/src/$project/$project.csproj" --configuration Release \
    --output "$out" -p:Version="$version" -nologo -v quiet
done

failed=0
fail() {
  echo "FAILED: $1" >&2
  failed=1
}

# $1 = package id, $2 = path inside the package
has() {
  unzip -Z1 "$out/$1.$version.nupkg" | grep -qxF "$2"
}

nuspec() {
  unzip -p "$out/$1.$version.nupkg" "$1.nuspec"
}

for nupkg in "$out"/*.nupkg; do
  id="$(basename "$nupkg" ".$version.nupkg")"
  if [[ ! " ${packages[*]} " == *" $id "* ]]; then
    fail "$id was packed but is not one of the published packages"
  fi
done

for id in "${packages[@]}"; do
  if [[ ! -f "$out/$id.$version.nupkg" ]]; then
    fail "$id.$version.nupkg was not produced"
    continue
  fi

  for file in LICENSE NOTICE README.md "lib/net10.0/$id.dll" "lib/net10.0/$id.xml"; do
    has "$id" "$file" || fail "$id does not carry $file"
  done

  spec="$(nuspec "$id")"
  grep -q '<license type="expression">MIT</license>' <<<"$spec" \
    || fail "$id does not declare the MIT licence"
  grep -qE '<repository type="git" url="https://github.com/sjp/cap-dotnet"[^>]* commit="[0-9a-f]{40}"' <<<"$spec" \
    || fail "$id does not record the repository and commit it was built from"
  if grep -q 'id="Cap.Primitives"' <<<"$spec"; then
    fail "$id depends on a Cap.Primitives package, which is not published"
  fi

  if [[ "$id" == Cap.Std.Testing ]]; then
    grep -qF "<dependency id=\"Cap.Std\" version=\"[$version]\" include=\"All\" />" <<<"$spec" \
      || fail "$id does not depend on exactly Cap.Std $version with every asset"
  elif [[ "$id" != Cap.Std ]]; then
    grep -qE "<dependency id=\"Cap.Std\" version=\"$version\" include=\"All\" />" <<<"$spec" \
      || fail "$id does not depend on Cap.Std $version with every asset, so the analyzer would not reach its consumers"
  fi

  if [[ "$id" != Cap.Std ]]; then
    if has "$id" lib/net10.0/Cap.Primitives.dll; then
      fail "$id carries its own copy of Cap.Primitives"
    fi
  fi
done

has Cap.Std.Testing buildTransitive/Cap.Std.Testing.targets \
  || fail "Cap.Std.Testing does not carry buildTransitive/Cap.Std.Testing.targets"

for file in lib/net10.0/Cap.Primitives.dll lib/net10.0/Cap.Primitives.xml analyzers/dotnet/cs/Cap.Analyzers.dll; do
  has Cap.Std "$file" || fail "Cap.Std does not carry $file"
done

if [[ "$failed" != 0 ]]; then
  exit 1
fi

echo "Packed ${#packages[@]} packages at $version into $out."
