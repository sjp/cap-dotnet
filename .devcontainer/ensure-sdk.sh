#!/usr/bin/env bash
set -euo pipefail

# Install the SDK global.json names if it is missing. rollForward never selects a lower
# SDK, so a dependabot bump of global.json leaves the feature's SDK unable to run anything.
# Installing into the feature's root keeps it beside the existing runtime, no DOTNET_ROOT needed.
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"

if dotnet --version >/dev/null 2>&1; then
    exit 0
fi

dotnet_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
echo "Installing the .NET SDK global.json names into $dotnet_root"
curl -fsSL https://dot.net/v1/dotnet-install.sh \
    | sudo bash -s -- --jsonfile "$repo_root/global.json" --install-dir "$dotnet_root"
dotnet --version
