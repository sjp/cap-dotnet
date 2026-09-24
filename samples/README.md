# Samples

Every sample here is built with the solution, and CI runs each one's demonstration on Linux,
Windows and macOS. Run with no arguments, each sets up a scene of its own in a scratch
directory, shows what happens, and exits non-zero if anything reached outside where it should
have been confined.

## `ContainmentCheck`

The two snippets the [README](../README.md) opens with, run for real.

```bash
dotnet run --project samples/ContainmentCheck
```

It makes a sandbox directory holding a symbolic link to a directory beside it, then reads
`reports/secret.txt` twice: once through the usual `GetFullPath` and `StartsWith` check, which
passes and reads the file outside, and once through a `Dir`, which refuses.

```
string check: passed, and read "the contents of a file outside the sandbox"
Dir:          refused: 'reports/secret.txt' resolved outside the directory the handle grants authority over.
```

The README's copies of the two snippets are checked against this program's by a test, so the
page cannot drift from what is run.

## `StaticFileServer`

An ASP.NET Core static file host whose request paths go straight to a `Dir` on the content
root.

```bash
dotnet run --project samples/StaticFileServer                 # the demonstration
dotnet run --project samples/StaticFileServer -- ./wwwroot     # serve a real directory
```

The demonstration builds a content root containing `assets`, a link to a private directory
beside it, starts on a loopback port and sends itself requests over a raw socket, so that no
client library resolves the dot segments first. Then it serves the same content root through
`UseStaticFiles` and `PhysicalFileProvider` and makes the request that matters:

```
  200  GET /docs/guide.txt
  404  GET /assets/secret.txt
  404  GET /%2e%2e/private/secret.txt
  404  GET /docs/..%2f..%2fprivate%2fsecret.txt
  ...
For comparison, the same content root served by UseStaticFiles over PhysicalFileProvider:
  200  GET /assets/secret.txt   <-- served "outside the content root"
```

Every refusal is a 404, whatever the reason, so a client learns nothing about what lies
outside.

## `PluginHost`

A host that loads plugins from their own assemblies and hands each one a `Dir` on a
directory of its own.

```bash
dotnet run --project samples/PluginHost/Host
```

- `Contracts` is the interface a plugin implements: `Run(Dir data, TextWriter log)`. That
  signature is the plugin's whole reach.
- `Plugins/Notes` keeps a file in its directory.
- `Plugins/Snoop` tries to read the notes plugin's file and the host's, by every spelling
  that would lead there, and writes whatever it gets to its own directory, where the host
  looks afterwards.

Both plugins are built with `[assembly: CapabilityStrict]`, so `File.ReadAllText` or
`AmbientAuthority.Acquire()` in either is a build error, and each is given its directory
restricted to `SymlinkPolicy.Deny`.

```
[snoop]
  refused  ../notes/notes.txt  (outside this plugin's directory)
  refused  ../../host-secret.txt  (outside this plugin's directory)
  refused  /etc/passwd  (outside this plugin's directory)
```

**This is not a sandbox for hostile plugins.** An `AssemblyLoadContext` separates assemblies,
not authority: plugin code runs in the host's process and can do anything the process can,
including P/Invoke and ignoring the analyzer by not being built with it. What this shows is
the arrangement for plugins that cooperate — each one's reach is the handle it was given, and
the build refuses code that reaches around it. A plugin nobody trusts needs an
operating-system sandbox around the process as well; see
[threat model §5.1](../docs/threat-model.md#51-cap-dotnet-is-not-a-process-sandbox).

## `AmbientAudit`

Where a process reaches outside itself, printed at the end of a run.

```bash
dotnet run --project samples/AmbientAudit
```

A report writer arranged the way an application using this library is meant to be:
authority enters once, at the top, and the handle it produces is passed down. It also
contains one piece of code that reaches for the system temporary directory on its own, as a
dependency would. The dump names both, and the counts tell the once-at-start-up entry from
the once-per-call one:

```
Ambient authority was taken at 2 sites:
  .../samples/AmbientAudit/Program.cs:40 in Main: taken once at +0.000s
  .../samples/AmbientAudit/Program.cs:92 in Render: taken 3 times, first at +0.003s
```

The recording is off unless asked for; this sample asks for it in its project file. See
[docs/ambient-authority.md](../docs/ambient-authority.md).

## `ArchiveExtractor`

A zip extractor that can't be made to write outside its destination. There's no check that
could be got wrong: each entry's name goes as-is to a `Dir` on the destination, and a name
that resolves to somewhere else is refused by the resolution itself.

```bash
dotnet run --project samples/ArchiveExtractor                            # the demonstration
dotnet run --project samples/ArchiveExtractor -- archive.zip destination  # a real archive
```

Run with no arguments, it builds a hostile archive of its own, extracts it into a scratch
directory, and exits non-zero if anything was written outside it:

```
Extracting a hostile archive into /tmp/cap-archive-extractor-AJHBEz/out:
  wrote    readme.txt
  wrote    docs/guide/intro.txt
  wrote    docs/guide/usage.txt
  refused  ../escaped.txt  (outside the destination)
  refused  docs/../../escaped.txt  (outside the destination)
  refused  docs/guide/../../../escaped.txt  (outside the destination)
  refused  /escaped-absolute.txt  (outside the destination)
```

It is also the program whose NativeAOT size is recorded in [docs/aot.md](../docs/aot.md).
CI publishes it as a native executable on every platform and runs the demonstration.

## `WasiHost`

A WebAssembly host whose guests' filesystem is a set of `Dir` handles: a
`wasi_snapshot_preview1` implementation in C#, running guests on Wasmtime.

```bash
dotnet run --project samples/WasiHost/Host                                   # the demonstration
dotnet run --project samples/WasiHost/Host -- run --dir ./data::/ program.wasm
```

The demonstration runs a guest that tries to read each of its arguments, handed a directory
as `/` and paths that reach out of it by `..`, by an absolute path and through links:

```
  notes.txt: read "the guest's own notes"
  ../secret.txt: refused, ENOTCAPABLE
  docs/../../secret.txt: refused, ENOTCAPABLE
  shortcut: refused, ENOTCAPABLE
```

A preopened directory is a `Dir` with nothing added, and each `path_*` call is one call on it.
The adapter is built with `[assembly: CapabilityStrict]` and is handed its clock and entropy
source as well, so a guest reaches nothing the host did not give it. Its tests run the escape
corpus through WASI calls made by a guest, and the WebAssembly WASI test suite's filesystem
programs. The sample's [README](WasiHost/README.md) lists what the library does not yet offer
that WASI asks for.
