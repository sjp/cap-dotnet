#!/usr/bin/env bash
# Installs the packed packages into projects created from nothing, builds them with every
# warning an error, and runs them.
#
# Two consumers, both outside the repository so that none of its build settings reach them,
# restoring from a feed that holds only the packages under test, into a package cache of
# their own:
#
#   1. one that references all seven packages and calls into each, which shows that they
#      restore together, that Cap.Primitives arrives with Cap.Std, and that the assemblies
#      load and work on this platform;
#   2. one that references only Cap.Time, which shows that the analyzer reaches a consumer
#      through a dependency on Cap.Std and not only through a direct reference.
#
# The packages hold no platform-specific assets, so the same feed is installed on every
# platform; that is what a release publishes.
#
# Usage: verify-package-install.sh <feed-dir> <version> [work-dir]
#
# Pass absolute paths. They are handed to dotnet as they are, and on Windows that means a
# native path such as $RUNNER_TEMP, not the POSIX spelling Git Bash would turn it into.
set -euo pipefail

feed="${1:?usage: verify-package-install.sh <feed-dir> <version> [work-dir]}"
version="${2:?usage: verify-package-install.sh <feed-dir> <version> [work-dir]}"
work="${3:-$(mktemp -d)}"
export NUGET_PACKAGES="$work/packages"

rm -rf "$work/all" "$work/time-only" "$NUGET_PACKAGES"
mkdir -p "$work/all" "$work/time-only"

# $1 = consumer directory
write_nuget_config() {
  cat > "$1/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
  </packageSources>
</configuration>
EOF
}

# $1 = consumer directory, $2 = PackageReference items, $3 = whether warnings are errors
write_project() {
  cat > "$1/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>$3</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
$2
  </ItemGroup>
</Project>
EOF
}

echo "== Every package, in one consumer"
consumer="$work/all"
write_nuget_config "$consumer"
references=""
for id in Cap.Std Cap.Fs.Ext Cap.Net Cap.Time Cap.Rand Cap.Directories Cap.Std.Testing; do
  references+="    <PackageReference Include=\"$id\" Version=\"$version\" />"$'\n'
done
write_project "$consumer" "$references" true

cat > "$consumer/Program.cs" <<'EOF'
using System.Net;
using Cap.Directories;
using Cap.Fs.Ext;
using Cap.Net;
using Cap.Primitives;
using Cap.Rand;
using Cap.Std;
using Cap.Std.Testing;
using Cap.Time;

AmbientAuthority authority = AmbientAuthority.Acquire();

using Dir root = Dir.Open(args[0], authority);
root.WriteAllTextAtomic("greeting.txt", "installed");
if (root.ReadAllText("greeting.txt") != "installed")
{
    throw new InvalidOperationException("Cap.Std and Cap.Fs.Ext did not round-trip a file.");
}

bool escaped;
try
{
    _ = root.ReadAllText("../outside.txt");
    escaped = true;
}
catch (SandboxEscapeException)
{
    escaped = false;
}
if (escaped)
{
    throw new InvalidOperationException("A parent link was not refused.");
}

InMemoryFileSystem memory = new();
memory.AddFile("inside/file.txt", "in memory");
using (Dir inMemory = memory.OpenRoot("inside"))
{
    if (inMemory.ReadAllText("file.txt") != "in memory")
    {
        throw new InvalidOperationException("Cap.Std.Testing did not hand out a working root.");
    }

    try
    {
        _ = inMemory.ReadAllText("../outside.txt");
        throw new InvalidOperationException("A parent link was not refused in memory.");
    }
    catch (SandboxEscapeException)
    {
    }
}

Pool pool = new PoolBuilder()
    .InsertSocketAddress(new IPEndPoint(IPAddress.Loopback, 8080), authority)
    .Build();
_ = pool;

byte[] entropy = CapRandom.System(authority).GetBytes(16);
DateTimeOffset now = CapClock.System(authority).GetUtcNow();
_ = typeof(ProjectDirs);

Console.WriteLine($"ok: {entropy.Length} random bytes at {now:O}");
EOF

mkdir -p "$work/root"
dotnet build "$consumer" -nologo -v quiet
dotnet run --project "$consumer" --no-build -- "$work/root"

echo "== Cap.Time alone, which brings the analyzer through Cap.Std"
consumer="$work/time-only"
write_nuget_config "$consumer"
write_project "$consumer" "    <PackageReference Include=\"Cap.Time\" Version=\"$version\" />" false

cat > "$consumer/Program.cs" <<'EOF'
using Cap.Primitives;
using Cap.Time;

Console.WriteLine(Clock.Now());

public static class Clock
{
    public static DateTimeOffset Now() => CapClock.System(AmbientAuthority.Acquire()).GetUtcNow();
}
EOF

output="$(dotnet build "$consumer" -nologo -v normal 2>&1)" || {
  echo "FAILED: a consumer of Cap.Time alone does not build" >&2
  echo "$output" >&2
  exit 1
}
if ! grep -qE 'Program\.cs\(8,[0-9]+\): warning CAP0003' <<<"$output"; then
  echo "FAILED: the analyzer does not reach a consumer that references only Cap.Time" >&2
  echo "$output" >&2
  exit 1
fi

echo "The packages install, build and run in fresh consumers."
