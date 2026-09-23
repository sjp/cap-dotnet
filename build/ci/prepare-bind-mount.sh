#!/usr/bin/env bash
# Prepares the bind mount the escape corpus attacks, and prints the directory to hand it in
# CAPDOTNET_TEST_BIND_MOUNT.
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
# <dir> must be on a filesystem that can hold symbolic links.
#
# Usage: prepare-bind-mount.sh <dir>
set -euo pipefail

base="$1/mount-fixture"
mkdir -p "$base/elsewhere" "$base/sandbox/mnt"
echo "reached through the mount" > "$base/elsewhere/file"
ln -sfn ../.. "$base/elsewhere/climb"
ln -sfn .. "$base/elsewhere/up"

sudo mount --bind "$base/elsewhere" "$base/sandbox/mnt"
findmnt "$base/sandbox/mnt" >&2

echo "$base/sandbox"
