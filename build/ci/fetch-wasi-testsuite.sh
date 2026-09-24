#!/usr/bin/env bash
# Fetches the WebAssembly WASI test suite's compiled programs, and prints the directory to
# hand the WASI host sample's tests in CAPDOTNET_WASI_TESTSUITE.
#
# The programs come to about a hundred megabytes, so they are fetched rather than kept in this
# repository. The commit is pinned: the list of programs the sample's tests expect to fail was
# written against this one, and a newer suite can add or change programs under it. Moving the
# pin is a deliberate change that updates that list in the same commit.
#
# Only the preview 1 programs are checked out.
#
# Usage: fetch-wasi-testsuite.sh <dir>
set -euo pipefail

commit=609c446139956ff30239f87cb18af1dc6128bed2
target="$1/wasi-testsuite"

if [ ! -d "$target/.git" ]; then
  git init --quiet "$target"
  git -C "$target" remote add origin https://github.com/WebAssembly/wasi-testsuite.git
fi

git -C "$target" sparse-checkout set --no-cone \
  '/tests/*/testsuite/wasm32-wasip1/' '/LICENSE' >&2
git -C "$target" fetch --quiet --depth 1 --filter=blob:none origin "$commit"
git -C "$target" checkout --quiet --force "$commit"

echo "$target"
