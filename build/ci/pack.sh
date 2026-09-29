#!/usr/bin/env bash
# Packs every package this repository publishes, and checks that each one holds what it is
# meant to and nothing it is not.
#
# The package list is written out here rather than taken from `dotnet pack` over the
# solution, so that a project which becomes packable by accident is not published by
# accident; it fails the check against MSBuild instead, as does a packable project left off
# the list. The checks cover what a consumer relies on and a build would not notice missing:
#
#   - Cap.Primitives travels inside Cap.Std, and there is no package of its own to depend on;
#   - the analyzer is in Cap.Std, and every other package passes it on rather than excluding it;
#   - Cap.Std.Testing depends on exactly the Cap.Std it was built with, since it implements a
#     contract inside Cap.Std that can change in any release;
#   - Cap.Std.Testing carries the build file that warns a project installing it that is not a
#     test project;
#   - Cap.IO.Abstractions is the one package with a third-party dependency, and each of its two
#     is a minimum with no maximum;
#   - the list below is exactly the projects under src/ that MSBuild considers packable;
#   - each package carries its licence, notice and readme, and records the commit it was built
#     from, which is what SourceLink resolves sources against;
#   - a release's readme links to the docs at the release's tag, not at main.
#
# Usage: pack.sh <output-dir> <version>
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:?usage: pack.sh <output-dir> <version>}"
version="${2:?usage: pack.sh <output-dir> <version>}"

packages=(Cap.Std Cap.Fs.Ext Cap.Net Cap.Time Cap.Rand Cap.Directories Cap.Std.Testing Cap.IO.Abstractions)

failed=0
fail() {
  echo "FAILED: $1" >&2
  failed=1
}

# The list above and MSBuild's view of what is packable have to agree. A project that is
# packable but not listed would be left out of a release its documentation promises, and one
# that is listed but not packable would be packed by hand against its own settings.
packable=()
for project in "$repo"/src/*/*.csproj; do
  if [[ "$(dotnet msbuild "$project" -getProperty:IsPackable -nologo)" == true ]]; then
    packable+=("$(basename "$project" .csproj)")
  fi
done
listed="$(printf '%s\n' "${packages[@]}" | sort)"
found="$(printf '%s\n' "${packable[@]}" | sort)"
if [[ "$listed" != "$found" ]]; then
  echo "FAILED: the projects under src/ that are packable are not the packages listed in $0" >&2
  diff <(echo "$listed") <(echo "$found") | sed -n 's/^< /  listed but not packable: /p; s/^> /  packable but not listed: /p' >&2
  exit 1
fi

# The readme's links name main, which is right for a build from main. A release links to the
# docs at its own tag instead; a CI build has version 0.0.0-* and no tag to link to.
readme="$repo/build/package/README.md"
if [[ "$version" != 0.0.0-* ]]; then
  staging="$(mktemp -d)"
  trap 'rm -rf "$staging"' EXIT
  sed "s#/blob/main/#/blob/v$version/#g" "$readme" > "$staging/README.md"
  readme="$staging/README.md"
fi

mkdir -p "$out"
rm -f "$out"/*.nupkg

for project in "${packages[@]}"; do
  dotnet pack "$repo/src/$project/$project.csproj" --configuration Release \
    --output "$out" -p:Version="$version" -p:CapPackageReadme="$readme" -nologo -v quiet
done

# $1 = package id, $2 = path inside the package. grep reads the whole listing rather than
# stopping at the first match with -q, which would leave unzip writing to a closed pipe and,
# under pipefail, turn a match into a failure.
has() {
  unzip -Z1 "$out/$1.$version.nupkg" | grep -xF "$2" >/dev/null
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

  if [[ "$version" != 0.0.0-* ]]; then
    packed_readme="$(unzip -p "$out/$id.$version.nupkg" README.md)"
    if grep -qF /blob/main/ <<<"$packed_readme" || ! grep -qF "/blob/v$version/" <<<"$packed_readme"; then
      fail "$id carries a readme whose links do not name v$version"
    fi
  fi

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
    grep -qF "<dependency id=\"Cap.Std\" version=\"$version\" include=\"All\" />" <<<"$spec" \
      || fail "$id does not depend on Cap.Std $version with every asset, so the analyzer would not reach its consumers"
  fi

  expected=()
  if [[ "$id" != Cap.Std ]]; then
    expected+=(Cap.Std)
  fi
  if [[ "$id" == Cap.IO.Abstractions ]]; then
    grep -qF "<dependency id=\"Cap.Fs.Ext\" version=\"$version\" include=\"All\" />" <<<"$spec" \
      || fail "$id does not depend on Cap.Fs.Ext $version with every asset"
    # A minimum and no maximum, the one Directory.Packages.props gives; Cap.IO.Abstractions.csproj
    # says why.
    for dependency in TestableIO.System.IO.Abstractions Testably.Abstractions.FileSystem.Interface; do
      minimum="$(sed -nE "s/.*<PackageVersion Include=\"$dependency\" Version=\"\[([^,]+),\)\".*/\1/p" "$repo/Directory.Packages.props")"
      if [[ -z "$minimum" ]]; then
        fail "Directory.Packages.props does not give $dependency as a minimum with no maximum"
      else
        grep -qE "<dependency id=\"$dependency\" version=\"$minimum\"( exclude=\"[^\"]*\")? />" <<<"$spec" \
          || fail "$id does not depend on $dependency at a minimum of $minimum with no maximum"
      fi
    done
    expected+=(Cap.Fs.Ext TestableIO.System.IO.Abstractions Testably.Abstractions.FileSystem.Interface)
  fi
  # Nothing else: the packages depend on .NET and each other, and on System.IO.Abstractions only
  # where it is the point of the package.
  actual="$({ grep -oE '<dependency id="[^"]+"' <<<"$spec" || true; } | sed -E 's/.*id="([^"]+)"/\1/' | sort)"
  if [[ "$actual" != "$(printf '%s\n' ${expected[@]+"${expected[@]}"} | sed '/^$/d' | sort)" ]]; then
    fail "$id depends on $(tr '\n' ' ' <<<"$actual")rather than exactly ${expected[*]:-nothing}"
  fi

  if [[ "$id" != Cap.Std ]]; then
    if has "$id" lib/net10.0/Cap.Primitives.dll; then
      fail "$id carries its own copy of Cap.Primitives"
    fi
  fi
done

has Cap.Std.Testing buildTransitive/Cap.Std.Testing.targets \
  || fail "Cap.Std.Testing does not carry buildTransitive/Cap.Std.Testing.targets"

for file in lib/net10.0/Cap.Primitives.dll lib/net10.0/Cap.Primitives.xml analyzers/dotnet/cs/Cap.Analyzers.dll buildTransitive/Cap.Std.targets; do
  has Cap.Std "$file" || fail "Cap.Std does not carry $file"
done

if [[ "$failed" != 0 ]]; then
  exit 1
fi

echo "Packed ${#packages[@]} packages at $version into $out."
