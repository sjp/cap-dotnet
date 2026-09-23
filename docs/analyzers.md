# The analyzer: making ambient authority visible at build time

A `Dir` confines what can be reached *through it*. It does nothing about the line next to it:
code holding a `Dir` can still call `File.ReadAllText("/etc/passwd")`, and the library's
guarantee only holds for the calls that go through it. The analyzer that ships in the
`Cap.Std` package closes that gap for code that wants it closed. It reports each place a
program reaches the filesystem, the network, the clock or the operating system's entropy by
some route other than a capability, and each use of this library that undoes what a
capability confines.

**It is not a security boundary.** A compiler diagnostic stops only code that is compiled
with the analyzer and does not suppress it, and it sees only what is written in source,
not what reflection or a dependency does at run time. It is a tool for cooperating code:
it keeps a codebase honest about where its authority comes from. Untrusted *code* needs
an operating-system sandbox; see §5.1 of [threat-model.md](threat-model.md).

## The rules

| ID | Reports | Default |
|---|---|---|
| `CAP0001` | The filesystem reached by path: `File`, `Directory`, `FileInfo`, `DirectoryInfo`, `FileSystemWatcher`, `DriveInfo`, `ZipFile`, the path constructors of `FileStream`, `StreamReader` and `StreamWriter`, `Environment.CurrentDirectory`, `Path.GetFullPath(string)`, `Path.GetTempPath`, `Path.GetTempFileName` | off |
| `CAP0002` | The network reached by address: `Socket.Bind`, `Connect`, `ConnectAsync`, `SendTo`, `SendToAsync`, `TcpListener`, `TcpClient`, `UdpClient`, and `Dns` | off |
| `CAP0003` | `AmbientAuthority.Acquire()` called outside a composition root | warning |
| `CAP0004` | A raw handle taken out of a capability: `Dir.UnsafeGetHandle()`, `CapFile.UnsafeGetHandle()` | info |
| `CAP0005` | A path passed to this library that was built by joining strings at the call | warning |
| `CAP0006` | The system clock: `DateTime.Now`/`UtcNow`/`Today`, `DateTimeOffset.Now`/`UtcNow`, `TimeProvider.System`, `Thread.Sleep`, and the `Task.Delay` overloads that take no `TimeProvider` | off |
| `CAP0007` | The operating system's entropy or a stand-in for it: `RandomNumberGenerator`, `System.Random`, `Guid.NewGuid`, `Guid.CreateVersion7`, `Path.GetRandomFileName` | off |
| `CAP0008` | Anything listed in a `CapBannedSymbols.txt` the project supplies | warning |

The exact lists behind `CAP0001`, `CAP0002`, `CAP0006` and `CAP0007` are in
[`src/Cap.Analyzers/Lists`](../src/Cap.Analyzers/Lists), and each diagnostic's message says
what to use instead.

### Why the ambient rules start off

Referencing `Cap.Std` must not break a build that uses `File` for reasons of its own. A
project usually adopts capabilities one component at a time, and its test projects use
exactly these APIs to set up what they then check. So the rules that forbid a whole family of
framework APIs report nothing until a project asks for them. The rules about using this
library well are on from the start: only code that already uses the library can trip them.

## Turning rules on, up and off

**Everything at once**: mark the assembly strict.

```csharp
[assembly: Cap.Primitives.CapabilityStrict]
```

In a strict assembly every rule is on, and every rule is an error. That is the single line
that makes ambient `System.IO` fail the build.

**One rule at a time**: set its severity in `.editorconfig`, like any other analyzer rule.

```ini
[*.cs]
dotnet_diagnostic.CAP0001.severity = error   # turn the filesystem rule on
dotnet_diagnostic.CAP0006.severity = warning # turn the clock rule on, as a warning
dotnet_diagnostic.CAP0004.severity = none    # stop reporting raw handles
```

`.editorconfig` sections are path globs, so a rule can be on for `src/` and off for
`tests/` in the same repository. A severity written explicitly for a rule wins over
`CapabilityStrict`, which is how a strict assembly stands down one rule without giving up the
rest. `<NoWarn>` and `<WarningsAsErrors>` in the project file work as they do for any
diagnostic.

## `CAP0003`: where authority is taken

A program should reach past what it was given in one place, where it is assembled: open the
first directories, take the clock and the entropy source, and hand them to components that
then hold nothing else. See [ambient-authority.md](ambient-authority.md). This rule reports
each `AmbientAuthority.Acquire()` made anywhere else. A composition root is:

- **the program's entry point**: `Main`, or top-level statements, including lambdas and
  local functions inside them;
- **anything marked `[CompositionRoot]`**: a class, struct, method, constructor or property,
  and everything declared inside it;

  ```csharp
  [CompositionRoot]
  public static class Startup
  {
      public static Dir OpenData() => Dir.Open("/srv/data", AmbientAuthority.Acquire());
  }
  ```

- **any file `.editorconfig` designates**, which suits a folder of start-up code:

  ```ini
  [src/Startup/**.cs]
  cap_composition_root = true
  ```

A class library has no entry point, so every acquisition in one is reported until something is
marked. That is intended. A library that takes its own authority is one whose reach cannot
be read from its signatures, and each such place should be a decision someone made.

## `CAP0004`: raw handles

The handle behind a `Dir` or `CapFile` carries the same authority as the capability it came
from. Code holding it can pass it anywhere, and it can outlive the capability's disposal. The
rule reports at `info`, as a list for a reviewer rather than a build failure. Setting it to
`none` is a reasonable choice for a project that has audited its uses.

## `CAP0005`: paths built by joining strings

```csharp
string report = users.ReadAllText("alice/" + name);   // CAP0005
```

A `Dir` confines a path to what is beneath it, and no further. If `name` arrives as
`../bob/report.txt`, that line reads Bob's report: it is still beneath `users`, so nothing
escaped, but it is not beneath `alice`. Open the directory the component belongs in first,
and the handle confines the component to it:

```csharp
using Dir alice = users.OpenDir("alice");
string report = alice.ReadAllText(name);              // `..` cannot leave alice
```

The rule looks at the argument as written at the call, when the parameter takes a path
(`path`, `from`, `to`, or a name ending in `Path`) on a member of this library. It reports
`Path.Combine`, `Path.Join`, `+`, `string.Concat` and interpolation that put a `/` or `\`
between parts, and `string.Join` with a separator. It does not report:

- a path that is entirely constant (`"users/alice.txt"`, or constants joined together), since
  nothing in it arrived from elsewhere;
- a path built on an earlier line and passed in a variable, because it does not follow data
  flow;
- the path given to a method that takes an `AmbientAuthority` token, such as `Dir.Open`,
  which is where a program names a place in full on purpose;
- a symbolic link's target, which is stored, not resolved against the directory.

## `CAP0008`: a project's own list

A project extends the built-in lists with an `AdditionalFile` named `CapBannedSymbols.txt`,
or `CapBannedSymbols.<anything>.txt` to keep one list per concern:

```xml
<ItemGroup>
  <AdditionalFiles Include="CapBannedSymbols.txt" />
</ItemGroup>
```

The format is the one `Microsoft.CodeAnalysis.BannedApiAnalyzers` reads from
`BannedSymbols.txt`: one documentation-comment ID per line, optionally followed by `;` and
the message to show. Lines starting with `#` are comments.

```
# Network access goes through the gateway client.
T:System.Net.Http.HttpClient;Use the IGatewayClient that was passed in.
M:System.Console.WriteLine(System.String);Write through the ILogger that was passed in.
P:System.Environment.MachineName
```

A method written without a parameter list stands for every overload. An entry that names
nothing the project references is ignored, so one list can serve several projects. A
symbol named in a project's list is reported as `CAP0008` even if a built-in rule also covers
it, so a project can ban `File` on its own without turning on the rest of `CAP0001`.

The file name differs from BannedApiAnalyzers' on purpose. A project that uses both
analyzers would otherwise have every entry reported twice, under two IDs.

## This library's own build

Every assembly under `src/` is built with this analyzer, with `CAP0001`, `CAP0002`,
`CAP0006`, `CAP0007` and `CAP0008` as errors (see the root `.editorconfig`). The assemblies
that parse and resolve paths also ban `System.IO.Path` outright, through
[`build/CapBannedSymbols.Primitives.txt`](../build/CapBannedSymbols.Primitives.txt). A few
places are allowed to reach the real thing, and each one suppresses the rule at that line and
says why. `CapClock` and `CapRandom` are where the clock and entropy enter, behind a token.
The socket calls in `Cap.Net` come after the address has been checked against a pool.

## Why not BannedApiAnalyzers

`Microsoft.CodeAnalysis.BannedApiAnalyzers` was the obvious thing to reuse for the rules that
are lists of banned APIs, and this repository used it for its own ban before the analyzer
existed. It was not kept, for these reasons:

- **One ID for everything.** Every hit is `RS0030`. The filesystem and the clock could not
  be turned on separately, and a consumer could not keep one on and the other off.
- **Nothing can escalate it from inside the compilation.** `[assembly: CapabilityStrict]`
  works because the analyzer that reads the attribute is also the one that reports the
  diagnostics.
- **It would collide with the consumer's own use.** Many codebases already run
  BannedApiAnalyzers with their own `BannedSymbols.txt`. Shipping it inside `Cap.Std`
  would add a second copy of the analyzer to their build and merge our list into theirs.

What was worth reusing was its list format, which is familiar and well understood. The lists
here are written in it, and a list written for one analyzer can be read by the other.

## Cost

The analyzer registers for member accesses, calls and object creations, and does one
dictionary lookup for each. It resolves its lists once per compilation. On a generated project
of 306,000 lines (1,500 files and 37,500 methods, each calling into `Cap.Std`), `ReportAnalyzer`
put it at 0.58 s of 4.5 s total analyzer time, spread across the compiler's threads. The
SDK's own analyzers took the rest. Wall-clock build times with and without it were within
run-to-run noise.

## Verifying the package

[`build/ci/verify-analyzer-package.sh`](../build/ci/verify-analyzer-package.sh) packs
`Cap.Std`, installs it into a project created outside the repository, and checks the
promised defaults in a real build. With nothing configured, `CAP0003` and `CAP0005` report
and `File.*` is left alone. With `[assembly: CapabilityStrict]`, `File.*` and the rest are
errors and the build fails. CI runs it on every change.
