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
# The package is always packed with the SDK global.json names. The consumer is built with the
# SDK in CAP_CONSUMER_SDK when that is set, through a global.json of its own, so that CI can
# build it with the oldest SDK the analyzer supports: a compiler older than the one the
# analyzer was built against reports CS9057 and runs without it, which none of the checks
# below would otherwise tell apart from a rule that is simply off. Every build here refuses
# CS9057 outright.
#
# Usage: [CAP_CONSUMER_SDK=10.0.100] verify-analyzer-package.sh [work-dir]
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

# dotnet picks the SDK by the global.json above the working directory, not above the project,
# so everything from here on runs in the consumer's directory.
cd "$consumer"
if [[ -n "${CAP_CONSUMER_SDK:-}" ]]; then
  cat > "$consumer/global.json" <<EOF
{
  "sdk": {
    "version": "$CAP_CONSUMER_SDK",
    "rollForward": "disable"
  }
}
EOF
  consumer_sdk="$(dotnet --version)" || {
    echo "FAILED: SDK $CAP_CONSUMER_SDK is not installed" >&2
    exit 1
  }
  if [[ "$consumer_sdk" != "$CAP_CONSUMER_SDK" ]]; then
    echo "FAILED: the consumer builds with SDK $consumer_sdk, not $CAP_CONSUMER_SDK" >&2
    exit 1
  fi
fi
echo "Consumer SDK: $(dotnet --version)"

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

# The diagnostic, not the /warnaserror option that the normal-verbosity log also prints.
skipped='(warning|error) CS9057'
skipped_means="the consumer's compiler is older than the one the analyzer was built against, so it ran without it"

echo "== Defaults"
# The package makes CS9057 an error (buildTransitive/Cap.Std.targets). A compiler that can
# load the analyzer never reports it, so the property is the only place to see that.
dotnet restore "$consumer" -nologo -v quiet
expect "$(dotnet msbuild "$consumer" -nologo -getProperty:WarningsAsErrors 2>&1)" '(^|;)CS9057(;|$)' \
  "installing Cap.Std does not make CS9057 an error"
status=0
output="$(dotnet build "$consumer" -nologo -v normal 2>&1)" || status=$?
refuse "$output" "$skipped" "$skipped_means"
if [[ "$status" != 0 ]]; then
  echo "FAILED: the consumer does not build with the defaults" >&2
  echo "$output" >&2
  exit 1
fi
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
refuse "$output" "$skipped" "$skipped_means"
expect "$output" 'Program\.cs\(13,[0-9]+\): error CAP0001' \
  "CAP0001 is not an error in a strict assembly"
expect "$output" 'Program\.cs\(11,[0-9]+\): error CAP0003' \
  "CAP0003 is not an error in a strict assembly"
expect "$output" 'Program\.cs\(12,[0-9]+\): error CAP0004' \
  "CAP0004 is not an error in a strict assembly"
expect "$output" 'Program\.cs\(14,[0-9]+\): error CAP0005' \
  "CAP0005 is not an error in a strict assembly"

echo "The analyzer ships in Cap.Std and runs in a fresh consumer as documented."
