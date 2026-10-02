#!/usr/bin/env bash
# Prepares the bind mount the escape corpus attacks and the resolver tests refuse to cross,
# and prints the directory to hand them in CAPDOTNET_TEST_BIND_MOUNT.
#
# Mounting needs a privilege the corpus must not hold -- it refuses to run in a process that
# could bypass file permissions -- so the mount is made here, with sudo, before the tests run
# as an ordinary user. The layout is the one the corpus expects:
#
#   <dir>/mount-fixture/sandbox/mnt   a bind mount of <dir>/mount-fixture/elsewhere, holding
#     file                            an ordinary file
#     climb -> ../..                  a link that would leave the sandbox from inside the mount
#     up -> ..                        a link back to the sandbox root
#
# <dir> must be on a filesystem that can hold symbolic links. Fails if the fixture is already
# mounted, rather than stacking a second mount on it.
#
# The mount outlives the script. A CI runner is thrown away afterwards, but on a machine that
# is kept, undo it with --cleanup, which unmounts and then removes <dir>/mount-fixture. Remove
# the fixture only that way: deleting it while mounted deletes through the mount.
#
# Needs sudo, mount, umount and findmnt (util-linux) on the PATH.
#
# Usage: prepare-bind-mount.sh <dir>
#        prepare-bind-mount.sh --cleanup <dir>
set -euo pipefail

usage="usage: prepare-bind-mount.sh [--cleanup] <dir>"
fail() { echo "prepare-bind-mount.sh: $*" >&2; exit 1; }

cleanup=false
if [ "${1:-}" = "--cleanup" ]; then
  cleanup=true
  shift
fi
[ "$#" -eq 1 ] || { echo "$usage" >&2; exit 2; }

for tool in sudo mount umount findmnt; do
  command -v "$tool" >/dev/null || { echo "prepare-bind-mount.sh: needs $tool on the PATH" >&2; exit 2; }
done

base="$1/mount-fixture"
mnt="$base/sandbox/mnt"
mounted() { findmnt --mountpoint "$mnt" >/dev/null; }

if $cleanup; then
  # A loop, since an earlier version of this script stacked a mount on each run.
  while mounted; do
    sudo umount "$mnt"
  done
  rm -rf "$base"
  exit 0
fi

if mounted; then
  fail "$mnt is already mounted; undo it with: $0 --cleanup $1"
fi

mkdir -p "$base/elsewhere" "$mnt"
echo "reached through the mount" > "$base/elsewhere/file"
ln -sfn ../.. "$base/elsewhere/climb"
ln -sfn .. "$base/elsewhere/up"

sudo mount --bind "$base/elsewhere" "$mnt"
findmnt "$mnt" >&2
echo "To undo: $0 --cleanup $1" >&2

echo "$base/sandbox"
