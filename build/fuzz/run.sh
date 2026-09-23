#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------
# Runs one fuzz target under libFuzzer, the same way on a workstation as on the schedule.
#
#   build/fuzz/run.sh <target> <seconds> [corpus-dir]
#
# Builds the harness, instruments Cap.Primitives with SharpFuzz so that the fuzzer can see
# which branches an input reached, builds the libFuzzer driver, and runs the target for the
# given number of seconds. Linux only: the driver talks to the harness over System V shared
# memory.
#
# The working corpus -- the inputs libFuzzer keeps because each reached something new -- goes
# in <corpus-dir>/<target>, which defaults to artifacts/fuzz/corpus. It is a cache, not a
# record, and is never committed. Seed it first with the escape corpus:
#
#   CAPDOTNET_FUZZ_SEEDS=$PWD/artifacts/fuzz/corpus dotnet test tests/Cap.Fuzz.Tests \
#     --filter-method "*.Seeds_are_written_out_when_asked"
#
# An input that crashes or hangs the target is written to fuzz/regressions/<target>/, where
# the replay tests in tests/Cap.Fuzz.Tests pick it up. Committing it makes it a regression
# test. See docs/fuzzing.md.
# ---------------------------------------------------------------------------------------
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "usage: $0 <target> <seconds> [corpus-dir]" >&2
  exit 2
fi

target=$1
seconds=$2
root=$(cd "$(dirname "$0")/../.." && pwd)
work="$root/artifacts/fuzz"
corpus="${3:-$work/corpus}/$target"
findings="$root/fuzz/regressions/$target"

# The driver is built from source rather than downloaded, so that what runs is what can be
# read, and so that it builds for whichever architecture this is. The source is pinned by
# release tag and by digest: a tag can be moved, a digest cannot.
driver_tag=v2025.05.02.0904
driver_sha256=90f019e2e9ad3a0b93c7ecc2c5afb2fbfc8b5aab6aac51c7e0d349ec79354f36
driver="$work/libfuzzer-dotnet-$driver_tag-abort"

mkdir -p "$work" "$corpus" "$findings"

if [[ ! -x "$driver" ]]; then
  source_file="$work/libfuzzer-dotnet-$driver_tag.cc"
  curl --fail --silent --show-error --location --output "$source_file" \
    "https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/$driver_tag/libfuzzer-dotnet.cc"
  echo "$driver_sha256  $source_file" | sha256sum --check --quiet
  # The driver reports a failed input with a trap instruction. That raises SIGILL on x86-64,
  # which libFuzzer catches and saves the input for, but SIGTRAP on arm64, which it does not:
  # the run would stop without keeping what failed. Calling abort instead raises SIGABRT on
  # both, and that libFuzzer handles everywhere.
  clang -Werror -O2 -fsanitize=fuzzer -ftrap-function=abort "$source_file" -o "$driver"
fi

# Published rather than built, so that every assembly is in one directory and the one that
# is instrumented is the one that is loaded. Into an empty directory each time: publishing
# over the last run would leave its instrumented copy in place, and instrumenting that a
# second time is refused.
rm -rf "$work/bin"
dotnet publish "$root/fuzz/Cap.Fuzz/Cap.Fuzz.csproj" --configuration Release --output "$work/bin" -p:UseAppHost=true
dotnet tool restore --tool-manifest "$root/.config/dotnet-tools.json"
dotnet tool run sharpfuzz "$work/bin/Cap.Primitives.dll"

# -timeout      an input that runs longer than this many seconds is a hang, and is kept.
# -rss_limit_mb the harness is a whole .NET runtime, which starts well above libFuzzer's
#               default budget before any input has run.
# Everything after the corpus directory is passed through, for a one-off flag.
"$driver" \
  --target_path="$work/bin/Cap.Fuzz" \
  --target_arg="$target" \
  -timeout=10 \
  -rss_limit_mb=4096 \
  -max_total_time="$seconds" \
  -print_final_stats=1 \
  -artifact_prefix="$findings/" \
  "$corpus" "${@:4}"
