#!/usr/bin/env bash
# Packs Cap.Std, installs the package into a project created from nothing, and checks that
# the analyzer inside it runs there with the defaults it promises.
#
# The analyzer's own tests drive it through the compiler API. What they cannot show is that
# the package puts it where NuGet looks, that a consumer's build picks it up with no
# configuration, and that the rules which are off by default really are off in a real build
# while the ones that are on really are on. This is the consumer's side of that, end to end:
#
#   1. with nothing configured, the rules about using the library well report, and File.* is
#      left alone;
#   2. with [assembly: CapabilityStrict], File.* and the rest become errors and the build fails.
#
# The consumer lives outside the repository so that none of the repository's build settings
# reach it, and restores from a feed holding only the freshly packed packages, into a package
# cache of its own, so that nothing stale can stand in for them.
#
# Usage: verify-analyzer-package.sh [work-dir]
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="${1:-$(mktemp -d)}"
version="0.0.0-verify"
feed="$work/feed"
consumer="$work/consumer"
export NUGET_PACKAGES="$work/packages"

rm -rf "$feed" "$consumer" "$NUGET_PACKAGES"
mkdir -p "$feed" "$consumer"

dotnet pack "$repo/src/Cap.Std/Cap.Std.csproj" --configuration Release \
  --output "$feed" -p:Version="$version" -nologo -v quiet

if ! unzip -l "$feed/Cap.Std.$version.nupkg" | grep -q 'analyzers/dotnet/cs/Cap.Analyzers.dll'; then
  echo "Cap.Std.$version.nupkg does not carry the analyzer." >&2
  exit 1
fi

cat > "$consumer/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
  </packageSources>
</configuration>
EOF

cat > "$consumer/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Cap.Std" Version="$version" />
  </ItemGroup>
</Project>
EOF

cat > "$consumer/Program.cs" <<'EOF'
using Cap.Primitives;
using Cap.Std;

using Dir root = Dir.Open(args[0], AmbientAuthority.Acquire());
Console.WriteLine(Worker.Read(root, args[1]));

public static class Worker
{
    public static string Read(Dir root, string name)
    {
        AmbientAuthority reachedFor = AmbientAuthority.Acquire();
        _ = root.UnsafeGetHandle();
        _ = File.ReadAllText(name);
        return root.ReadAllText("users/" + name);
    }
}
EOF

# $1 = build output, $2 = pattern that must appear, $3 = what it means
expect() {
  if ! grep -qE "$2" <<<"$1"; then
    echo "FAILED: $3" >&2
    echo "$1" >&2
    exit 1
  fi
}

# $1 = build output, $2 = pattern that must not appear, $3 = what it means
refuse() {
  if grep -qE "$2" <<<"$1"; then
    echo "FAILED: $3" >&2
    echo "$1" >&2
    exit 1
  fi
}

echo "== Defaults"
output="$(dotnet build "$consumer" -nologo -v normal 2>&1)" || {
  echo "FAILED: the consumer does not build with the defaults" >&2
  echo "$output" >&2
  exit 1
}
expect "$output" 'Program\.cs\(11,[0-9]+\): warning CAP0003' \
  "CAP0003 is not reported for Acquire outside the entry point"
refuse "$output" 'Program\.cs\(4,[0-9]+\): warning CAP0003' \
  "CAP0003 is reported for Acquire in the entry point"
expect "$output" 'Program\.cs\(14,[0-9]+\): warning CAP0005' \
  "CAP0005 is not reported for a joined path"
refuse "$output" 'CAP0001' \
  "CAP0001 is reported although nothing turned it on"

echo "== [assembly: CapabilityStrict]"
echo '[assembly: Cap.Primitives.CapabilityStrict]' > "$consumer/Strict.cs"
if output="$(dotnet build "$consumer" -nologo -v normal 2>&1)"; then
  echo "FAILED: a strict consumer that uses File.* still builds" >&2
  echo "$output" >&2
  exit 1
fi
expect "$output" 'Program\.cs\(13,[0-9]+\): error CAP0001' \
  "CAP0001 is not an error in a strict assembly"
expect "$output" 'Program\.cs\(11,[0-9]+\): error CAP0003' \
  "CAP0003 is not an error in a strict assembly"
expect "$output" 'Program\.cs\(12,[0-9]+\): error CAP0004' \
  "CAP0004 is not an error in a strict assembly"
expect "$output" 'Program\.cs\(14,[0-9]+\): error CAP0005' \
  "CAP0005 is not an error in a strict assembly"

echo "The analyzer ships in Cap.Std and runs in a fresh consumer as documented."
