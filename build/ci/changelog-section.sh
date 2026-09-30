#!/usr/bin/env bash
# Prints the body of one version's section of CHANGELOG.md, and fails if the file has no
# heading for that version or the section under it is empty.
#
# Usage: changelog-section.sh <version> [changelog]
#
# A section starts at a heading `## [<version>]`, optionally followed by ` - <date>`, and runs
# to the next `## ` heading or the link references at the foot of the file. The release
# workflow uses the output as the GitHub release's notes, so a release cannot be tagged
# before its changes are written down.
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 <version> [changelog]" >&2
  exit 2
fi

version="$1"
changelog="${2:-CHANGELOG.md}"

if [[ ! -f "$changelog" ]]; then
  echo "::error::$changelog does not exist." >&2
  exit 1
fi

# The version is compared as a string, not a pattern, so its dots match only dots. awk exits
# 3 when there is no heading, to tell that apart from an empty section.
status=0
section="$(awk -v heading="## [$version]" '
  found && (/^## / || /^\[[^]]+\]: /) { exit }
  found { print; next }
  $0 == heading || index($0, heading " ") == 1 { found = 1 }
  END { if (!found) exit 3 }
' "$changelog")" || status=$?

if [[ $status -eq 3 ]]; then
  echo "::error::$changelog has no '## [$version]' heading. Move the Unreleased entries under it." >&2
  exit 1
elif [[ $status -ne 0 ]]; then
  exit "$status"
fi

if [[ -z "${section//[[:space:]]/}" ]]; then
  echo "::error::The '## [$version]' section of $changelog is empty." >&2
  exit 1
fi

# Command substitution has already dropped the trailing blank lines; drop the leading ones.
printf '%s\n' "$section" | sed -e '/./,$!d'
