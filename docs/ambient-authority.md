# Ambient authority: the one way in

Everything in this library derives its authority from something the caller already holds. A
`Dir` names what is beneath it; a handle opened through one carries no more than its parent
did. That chain has to begin somewhere, and the places it begins are the only points at
which a component reaches something nobody handed it:

```csharp
using Dir workspace = Dir.Open("/srv/reports", AmbientAuthority.Acquire());
```

Every API that reaches outside the capability graph — opening the first directory by an
ordinary path, and in time the system clock, the operating system's entropy, a socket, a
well-known user directory — takes one of these tokens, and no API that does so omits it.

## It is an audit, not a lock

Any code that can call anything can call `AmbientAuthority.Acquire()`. Nothing stops it and
nothing is meant to. What the token buys is that those points are *enumerable*:

```bash
grep -rn 'AmbientAuthority.Acquire' src/
```

is a complete list of the places a codebase takes authority it was not given, and a review
can then ask of each one whether it belongs there. A library that quietly reached the
filesystem without a token would not be on that list, which is the only thing the token
exists to prevent.

`default(AmbientAuthority)` is not a token. Every API that requires one refuses a value
nobody acquired, with an `ArgumentException`, because the alternative is a way to satisfy
the parameter without writing the name the search looks for. It is an argument error rather
than a security failure: it means the calling code is wrong, not that containment was
attacked.

## Recording where it was taken

The search answers the question for code somebody can read. It does not answer it for a
dependency, whose source nobody searched and which is perfectly free to take authority
inside itself. Turning the recording on makes the process answer for itself:

1. **AppContext switch** `Cap.Primitives.RecordAmbientAuthority`

   ```xml
   <!-- in the consuming application's .csproj -->
   <ItemGroup>
     <RuntimeHostConfigurationOption Include="Cap.Primitives.RecordAmbientAuthority" Value="true" />
   </ItemGroup>
   ```

   or, before anything takes authority:

   ```csharp
   AppContext.SetSwitch(AmbientAuthority.RecordingSwitchName, true);
   ```

   The project-file form is in effect before the first line of `Main` runs, which is where a
   static initializer would take authority if it were going to. The switch is read afresh at
   each acquisition, so it can also be turned on around one suspect phase of start-up.

2. **Environment variable** `CAPDOTNET_RECORD_AMBIENT_AUTHORITY=1`

   For an operator who cannot rebuild. Read once, so it cannot change under a running
   process.

Then, typically once, after start-up:

```csharp
Console.WriteLine(AmbientAuthority.DescribeRecordedSites());
```

```
Ambient authority was taken at 3 sites:
  /src/Program.cs:14 in Main: taken once at +0.000s
  /src/PluginHost.cs:62 in LoadPlugin: taken 7 times, first at +0.118s
  /src/Cache.cs:31 in OpenCache: taken once at +0.402s
```

`AmbientAuthority.RecordedSites` is the same answer as data, for a process that wants to
assert something about it — a test that fails when a new entry point appears, or a start-up
check that refuses to serve if anything but the composition root took authority.

### What the record is, and is not

**One entry per place, not per occurrence.** A line inside a loop that takes authority a
thousand times is one site with a count of a thousand. The question being asked is *where* a
process reaches outside itself, which is a property of the program rather than of the run —
and an answer shaped that way stays a fixed size however long the process lives, so it is
safe to leave the recording on in production.

**Times are measured from the first acquisition, not from a clock.** Whether a site fired
during start-up or an hour into serving traffic is the legible part; reading the time of day
to say so would mean consulting an ambient clock in order to report on ambient authority.

**Off by default, and free when off.** A process that never asks pays one switch lookup per
acquisition, at a call that already costs a directory open.

**There is no way to clear it.** An audit a component could erase behind itself answers a
weaker question than the one being asked. Turning the switch off stops new entries; what was
recorded stays recorded.

**An empty report is not a clean bill of health.** With the recording off, nothing is
recorded however much authority is taken, so the report says that rather than printing an
empty list.

## Where to take it

Once, at the composition root, and pass the handle down. A component that is handed a `Dir`
can be reasoned about from its signature: it can reach what is beneath that handle and
nothing else, whatever string it is given. A component that takes its own authority cannot,
and the recording is how an application finds out that it has one.

`samples/AmbientAudit` is a working program shaped this way, with one deliberate offender in
it.
