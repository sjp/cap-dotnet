#!/usr/bin/env bash
# Lists the tests that stand aside when a filesystem held in memory stands in for the host,
# with how many there are, as Markdown.
#
# A test opts out with the NotInMemory attribute, which records its reason as a trait of the
# same name. The count is printed so that it cannot grow unnoticed: each opt-out is a test the
# in-memory backend is no longer held to, and one added to hide a disagreement with the disk,
# rather than because the test is about something only the host has, is a fault in the
# backend that has stopped being reported.
#
# Reads the built test assemblies without running them, so run it after a Release build.
#
# Usage: list-in-memory-opt-outs.sh <test-project-name>...
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
[ "$#" -gt 0 ] || { echo "usage: list-in-memory-opt-outs.sh <test-project-name>..." >&2; exit 2; }

total=0
body=""
for project in "$@"; do
  assembly="$(ls "$repo"/tests/"$project"/bin/Release/*/"$project".dll | head -n 1)"
  # The runner prints its banner even when asked not to decorate a listing.
  methods="$(dotnet "$assembly" -noLogo -noColor -filter "/[NotInMemory=*]" -list methods \
    | tr -d '\r' | grep -v '^xUnit\.net' | grep . || true)"
  count=0
  [ -z "$methods" ] || count="$(printf '%s\n' "$methods" | wc -l | tr -d ' ')"
  total=$((total + count))
  body+=$'\n'"**$project**: $count"$'\n'
  [ -z "$methods" ] || body+="$(printf '%s\n' "$methods" | sed 's/^/- `/; s/$/`/')"$'\n'
done

echo "### Tests not run in memory: $total"
echo "$body"
echo "Each carries its reason in the NotInMemory trait."
