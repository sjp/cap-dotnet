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
# Reads the built test assemblies without running them, so run it after a build of the given
# configuration (Release unless -c says otherwise). The target framework is the only one built
# unless -f names it. Fails when an assembly is missing, and when the runner lists a different
# number of opt-outs than the project's source carries, so a listing that went wrong cannot
# pass for a count of zero.
#
# Usage: list-in-memory-opt-outs.sh [-c <configuration>] [-f <framework>] <test-project-name>...
set -euo pipefail

usage="usage: list-in-memory-opt-outs.sh [-c <configuration>] [-f <framework>] <test-project-name>..."
fail() { echo "list-in-memory-opt-outs.sh: $*" >&2; exit 1; }

configuration=Release
framework=""
while getopts "c:f:" option; do
  case "$option" in
    c) configuration="$OPTARG" ;;
    f) framework="$OPTARG" ;;
    *) echo "$usage" >&2; exit 2 ;;
  esac
done
shift $((OPTIND - 1))
[ "$#" -gt 0 ] || { echo "$usage" >&2; exit 2; }

repo="$(cd "$(dirname "$0")/../.." && pwd)"

total=0
body=""
for project in "$@"; do
  output="$repo/tests/$project/bin/$configuration"
  if [ -n "$framework" ]; then
    tfm="$framework"
  else
    built=()
    for dir in "$output"/*/; do
      [ -d "$dir" ] && built+=("$(basename "$dir")")
    done
    case "${#built[@]}" in
      0) fail "$project has no $configuration build under $output" ;;
      1) tfm="${built[0]}" ;;
      *) fail "$project has a $configuration build for several frameworks (${built[*]}); name one with -f" ;;
    esac
  fi
  assembly="$output/$tfm/$project.dll"
  [ -f "$assembly" ] || fail "$project is not built: expected $assembly"

  # The runner prints its banner even when asked not to decorate a listing.
  listing="$(dotnet "$assembly" -noLogo -noColor -filter "/[NotInMemory=*]" -list methods)" \
    || fail "$project: the test runner could not list $assembly"
  methods="$(printf '%s\n' "$listing" | tr -d '\r' | grep -v '^xUnit\.net' | grep . || true)"
  count=0
  [ -z "$methods" ] || count="$(printf '%s\n' "$methods" | wc -l | tr -d ' ')"

  # Each attribute marks one method, so the source and the runner should agree.
  declared="$(grep -rho --include='*.cs' --exclude-dir=bin --exclude-dir=obj '\[NotInMemory(' \
    "$repo/tests/$project" | wc -l | tr -d ' ')"
  [ "$count" -eq "$declared" ] \
    || fail "$project: the runner lists $count opt-outs but its source declares $declared"

  total=$((total + count))
  body+=$'\n'"**$project**: $count"$'\n'
  [ -z "$methods" ] || body+="$(printf '%s\n' "$methods" | sed 's/^/- `/; s/$/`/')"$'\n'
done

echo "### Tests not run in memory: $total"
echo "$body"
echo "Each carries its reason in the NotInMemory trait."
