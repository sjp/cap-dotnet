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
not what reflection or a dependency does at run time. A call bound at run time is invisible
to it too: `File.Exists((dynamic)path)` is chosen by the runtime binder, so there is no
member in the compilation to compare with the lists, and `typeof(File).GetMethod(...)` is
just reflection. It is a tool for cooperating code:
it keeps a codebase honest about where its authority comes from. Untrusted *code* needs
an operating-system sandbox; see §5.1 of [threat-model.md](threat-model.md).

## The rules

| ID | Reports | Default |
|---|---|---|
| `CAP0000` | Never reported. It keeps the analyzer running when every other rule is configured off, so that `CapabilityStrict` still applies, and it cannot be configured | hidden |
| `CAP0001` | The filesystem reached by path: `File`, `Directory`, `FileInfo`, `DirectoryInfo`, `FileSystemWatcher`, `DriveInfo`, `ZipFile`, the path constructors of `FileStream`, `StreamReader` and `StreamWriter`, `Environment.CurrentDirectory`, `Path.GetFullPath(string)`, `Path.GetTempPath`, `Path.GetTempFileName`, `Environment.GetFolderPath`; archives extracted to or filled from a path (`TarFile`, `TarEntry.ExtractToFile`, `ZipFileExtensions.ExtractToDirectory`, `ExtractToFile`, `CreateEntryFromFile` and their async forms); programs and code run or loaded by path (every `Process.Start`, `Assembly.LoadFrom`/`LoadFile`/`UnsafeLoadFrom`, `AssemblyLoadContext.LoadFromAssemblyPath`/`LoadFromNativeImagePath`/`LoadUnmanagedDllFromPath`, `NativeLibrary.Load`/`TryLoad`); the path overloads of `XDocument`, `XElement` and `XStreamingElement` `Load`/`Save`, `XmlReader.Create`, `XmlWriter.Create`, `XmlDocument.Load`/`Save`, `XmlTextReader`, `XmlTextWriter`, `XPathDocument` and `XslCompiledTransform.Load`/`Transform`; the path overloads of `MemoryMappedFile.CreateFromFile`; the name-taking constructors of `NamedPipeClientStream` and `NamedPipeServerStream`, and `NamedPipeServerStreamAcl.Create`; `Socket.SendFile`, `SendFileAsync` and `BeginSendFile`; certificates read by path (`X509CertificateLoader.*FromFile`, the path constructors of `X509Certificate` and `X509Certificate2`, `CreateFromCertFile`, `CreateFromSignedFile`, `CreateFromPemFile`, `CreateFromEncryptedPemFile`, `GetCertContentType(string)`, and the path overloads of `X509Certificate2Collection.Import` and `ImportFromPemFile`); and the constructors of the ambient `IFileSystem` implementations: System.IO.Abstractions' `FileSystem`, `FileWrapper`, `DirectoryWrapper`, `FileInfoWrapper`, `DirectoryInfoWrapper`, `DriveInfoWrapper`, `PathWrapper`, `FileSystemWatcherWrapper` and `FileSystemWatcherFactory`, and Testably's `RealFileSystem` | off |
| `CAP0002` | The network reached by address or by name: `Socket.Bind`, `Connect`, `ConnectAsync`, `SendTo`, `SendToAsync`, `TcpListener`, `TcpClient`, `UdpClient`, and `Dns`; and the construction of the clients that resolve a name or URL themselves: `new HttpClient()`, `SocketsHttpHandler`, `HttpClientHandler`, `ClientWebSocket`, `SmtpClient`, `Ping`, `HttpListener`, `WebClient`, `WebRequest.Create`/`CreateHttp`/`CreateDefault`, `QuicConnection.ConnectAsync` and `QuicListener.ListenAsync` | off |
| `CAP0003` | `AmbientAuthority.Acquire()` called outside a composition root | warning |
| `CAP0004` | A raw handle taken out of a capability: `Dir.UnsafeGetHandle()`, `CapFile.UnsafeGetHandle()` | info |
| `CAP0005` | A path passed to this library that was built by joining strings at the call | warning |
| `CAP0006` | The system clock: `DateTime.Now`/`UtcNow`/`Today`, `DateTimeOffset.Now`/`UtcNow`, `TimeProvider.System`, `Thread.Sleep`, the constructors of `System.Threading.Timer` and `System.Timers.Timer`, and the overloads of `Task.Delay`, `Task.WaitAsync`, `PeriodicTimer` and `CancellationTokenSource` that take a timeout or interval but no `TimeProvider` | off |
| `CAP0007` | The operating system's entropy or a stand-in for it: `RandomNumberGenerator`, `System.Random`, `Guid.NewGuid`, `Guid.CreateVersion7`, `Path.GetRandomFileName` | off |
| `CAP0008` | Anything listed in a `CapBannedSymbols.txt` the project supplies | warning |

The exact lists behind `CAP0001`, `CAP0002`, `CAP0006` and `CAP0007` are in
[`src/Cap.Analyzers/Lists`](../src/Cap.Analyzers/Lists), and each diagnostic's message says
what to use instead. Where a member has overloads over a `Stream`, a reader or writer, or a
handle the caller already holds, only the overloads that take a path or name are listed:
`XDocument.Load(stream)` and `new NamedPipeServerStream(direction, isAsync, isConnected, handle)`
reach nothing themselves.

A type in a list covers every class derived from it, including their constructors and
overrides. So `CAP0007` reports `new RNGCryptoServiceProvider()` (derived from
`RandomNumberGenerator`) and a class of the project's own that derives from `Random`. A
listed method, property or event also covers the members that override it. Interfaces are
not followed: an entry for an interface covers calls made through the interface, not the
classes that implement it.

### `IFileSystem`

System.IO.Abstractions' `FileSystem` and Testably's `RealFileSystem` pass every call straight
to `File`, `Directory` and the rest, so `new FileSystem()` is the whole filesystem behind an
interface, and `CAP0001` reports it as it would report `File`. The same goes for the wrappers
`FileSystem` is assembled from. Taking an `IFileSystem` is not reported: what it reaches
depends on what the caller passed in, and a
[`DirFileSystem`](io-abstractions.md) from `Cap.IO.Abstractions` confines it to a `Dir`. The
entries name their types by metadata name, so they apply whether or not a project references
either library, and report nothing in a project that does not.

### `HttpClient` and the other by-name clients

`HttpClient`, `ClientWebSocket`, `SmtpClient` and the rest take a host name or a URL, resolve
it themselves and connect to the answer, so they reach the network as far as `Dns` and a
socket together would. `CAP0002` reports them where they are built, not where they are used:
calling `GetAsync` on an `HttpClient` taken as a parameter is not reported, for the same
reason taking an `IFileSystem` is not. Construct the client once where the program is
assembled and pass it down; the suppression there is the one line a reviewer reads.

`new HttpClient(handler)` is not reported, because the handler's own constructor is:
`new SocketsHttpHandler()` and `new HttpClientHandler()` are, and `new HttpClient()` is
because it builds a default handler of its own. A `SocketsHttpHandler` whose
`ConnectCallback` connects through a `Cap.Net.Pool` checks every address the client reaches,
so it is the one place such a setup needs a suppression.

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

`CapabilityStrict` applies however the other rules are configured. The compiler does not run
an analyzer whose every rule is off, so the analyzer carries `CAP0000`, a rule that is never
reported and that `.editorconfig` cannot turn off; switching off `CAP0003`, `CAP0004`,
`CAP0005` and `CAP0008` therefore leaves a strict assembly strict.

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

`IDir` and `ICapFile` have no `UnsafeGetHandle`, so a raw handle is only ever taken through the
concrete types, where this rule sees it.

This rule and `CAP0005` apply only to members of this library, which they recognise by the
`[assembly: CapabilityLibrary]` marker every cap-dotnet assembly carries rather than by name.
A consumer's own assembly called `Cap.Something`, with its own `UnsafeGetHandle` or `path`
parameter, is not reported.

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

- a path that is entirely constant (`"users/alice.txt"`, or constants joined together with
  `+`, interpolation, `Path.Combine`, `Path.Join`, `string.Concat` or `string.Join`, at any
  depth), since nothing in it arrived from elsewhere. `Path.DirectorySeparatorChar` and
  `Path.AltDirectorySeparatorChar` count as constants here;
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

An entry here reaches derived classes and overrides in the same way as the built-in lists.
Derived classes are where the two analyzers read the same list differently: BannedApiAnalyzers
follows overrides but matches only the type named, while `T:System.IO.Stream` here also
reports `new MemoryStream()`.

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
`Cap.Directories` reads `Environment.GetFolderPath` only inside `ProjectDirs.From` and
`ProjectDirs.OpenSpecialFolder`, after their token has been demanded, and the Windows backend loads `ntdll.dll` by module name to probe
for an export.

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

The analyzer registers for member accesses, calls and object creations. For each one it does a
few dictionary lookups: the member, what it overrides, and the types it is declared in and
derives from. It resolves its lists once per compilation. On a generated project
of 306,000 lines (1,500 files and 37,500 methods, each calling into `Cap.Std`), `ReportAnalyzer`
put it at 0.58 s of 4.5 s total analyzer time, spread across the compiler's threads. The
SDK's own analyzers took the rest. Wall-clock build times with and without it were within
run-to-run noise.

## Compiler floor

The analyzer needs .NET SDK 10.0.100 or later, which is also the oldest SDK that can build
against the `net10.0` packages. A compiler loads an analyzer only if the analyzer was built
against a compiler version no newer than its own. An older compiler reports `CS9057` and
builds without the analyzer, so every `CAP` rule goes quiet, `[assembly: CapabilityStrict]`
included, and a warning is the only sign. So the analyzer is built against the compiler in SDK
10.0.100 (`Microsoft.CodeAnalysis.CSharp` 5.0.0), not the one this repository builds with.
Dependabot does not raise that pin, because a newer pin raises the floor. `Directory.Packages.props`
says what else has to move with it.

The floor cannot fail quietly. `Cap.Std` carries `buildTransitive/Cap.Std.targets`, which adds
`CS9057` to `WarningsAsErrors` in every project that installs the package, directly or through
another package here. A compiler too old to load the analyzer then fails the build instead of
skipping the analyzer. `CS9057` is not specific to this analyzer, so the error applies to any
analyzer the compiler cannot load. A project that accepts that can add `CS9057` to `NoWarn`.

## Verifying the package

[`build/ci/verify-analyzer-package.sh`](../build/ci/verify-analyzer-package.sh) packs
`Cap.Std`, installs it into a project created outside the repository, and checks the
promised defaults in a real build. With nothing configured, `CAP0003` and `CAP0005` report
and `File.*` is left alone. With `[assembly: CapabilityStrict]`, `File.*` and the rest are
errors and the build fails. No build may report `CS9057`, and the package must make it an
error. CI runs the script on every change twice, once with the SDK in `global.json` and once
with SDK 10.0.100 building the consumer (`CAP_CONSUMER_SDK`), so the floor is exercised
instead of only stated.

[`build/ci/verify-package-install.sh`](../build/ci/verify-package-install.sh) covers the
other way in. The other packages depend on `Cap.Std` with all of its assets, so a project that
references only `Cap.Time`, say, gets the analyzer as well. The script builds such a project
and checks that `CAP0003` reports in it.
