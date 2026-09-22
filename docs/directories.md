# Project directories as capabilities

Most programs need a few directories whose location is decided by the platform, not by the
program: somewhere for configuration, somewhere for data, somewhere for a cache. The usual
approach is to build a path at start-up and keep joining strings onto it for the rest of the
run. `Cap.Directories` does that step once, under an `AmbientAuthority` token, and hands back
`Dir` handles instead of paths.

```csharp
using Cap.Directories;
using Cap.Primitives;
using Cap.Std;

using ProjectDirs dirs = ProjectDirs.From("com", "Example Corp", "My App", AmbientAuthority.Acquire());

using Dir config = dirs.OpenConfig();
using Dir cache = dirs.OpenCache();
Dir? runtime = dirs.OpenRuntime();   // null where the platform or session has none
```

`ProjectDirs.From` is the only ambient step. It reads the environment, and for each kind of
directory it opens the deepest part of the location that already exists. Nothing is created
yet. The first `Open…` call for a kind creates whatever is missing, one component at a time
through the handle `From` opened, and every call returns a new handle that the caller owns.
An application that never asks for a cache never gets one.

Pass the handles down, not the `ProjectDirs`. A component given `config` can reach the
application's configuration directory and nothing above it.

## Where the directories are

The layout matches the Rust [`directories`](https://crates.io/crates/directories) crate, so
an application finds the same directories whichever library it uses.

| Kind | Linux and other Unixes | macOS | Windows |
|---|---|---|---|
| Config | `$XDG_CONFIG_HOME/myapp`, else `~/.config/myapp` | `~/Library/Application Support/com.Example-Corp.My-App` | `%APPDATA%\Example Corp\My App\config` |
| Data | `$XDG_DATA_HOME/myapp`, else `~/.local/share/myapp` | `~/Library/Application Support/com.Example-Corp.My-App` | `%APPDATA%\Example Corp\My App\data` |
| Cache | `$XDG_CACHE_HOME/myapp`, else `~/.cache/myapp` | `~/Library/Caches/com.Example-Corp.My-App` | `%LOCALAPPDATA%\Example Corp\My App\cache` |
| State | `$XDG_STATE_HOME/myapp`, else `~/.local/state/myapp` | none | none |
| Runtime | `$XDG_RUNTIME_DIR/myapp`, if that directory is private | none | none |

- **XDG.** The application's directory is its name, lowercased, with whitespace removed.
  As the XDG Base Directory specification requires, a variable that is unset, empty or not
  an absolute path is ignored and the default is used instead. A relative value would
  otherwise be resolved against whatever directory the process was started in.
- **macOS.** The qualifier, organization and application are joined into a bundle
  identifier. Whitespace inside each part becomes a hyphen, and empty parts are left out.
  Configuration and data share one directory, because that is where the platform keeps
  both.
- **Windows.** The organization and application are used as given, under the roaming and
  local application-data known folders. An empty organization is left out.
- **State and runtime** exist only where the convention defines them. On macOS and Windows
  `OpenState` and `OpenRuntime` return null. This library does not invent a location for
  them.

A name containing `/`, `\` or NUL is refused on every platform, so the application's
directory cannot end up somewhere other than the one intended.

## Permissions

On Unix, every directory this library creates is mode `0700`, as the XDG specification
asks. That includes the application's own directory and any missing parent such as
`~/.config` or `~/.local/state`. Directories that already exist are used as they are. Their
permissions were somebody's decision, and quietly changing them would be a surprise, not a
defence.

On Windows, access is decided by the security descriptor the new directory inherits from
the known folder, which is already per-account.

## The runtime directory is checked, not assumed

Sockets and lock files in `$XDG_RUNTIME_DIR` rely on nobody else being able to reach them.
The specification says the session must provide the directory owned by the user with mode
`0700`. This library checks that rather than trusting it. The directory is used only if, as
opened, it is a directory owned by the account the process runs as, with no permission bits
for group or others. The check is made on the open handle, so the directory checked is the
directory used.

When the variable is unset, relative, names nothing, or names a directory that fails the
check, `OpenRuntime` returns null. The other kinds are unaffected. No fallback is chosen,
because no other candidate location is removed when the session ends, which is the point of
a runtime directory. A caller without one decides for itself what to do instead.

## Resolved once

After `From` returns, nothing is looked up by path. If the part of a location that existed
at that point is renamed or replaced, creation still happens under the directory `From`
opened, not under whatever now holds the old name.

The ambient step itself follows symbolic links the way any program does, so a `~/.config`
that links into a dotfiles checkout is followed, as the user intended. The components
created beneath it go through the handle, and an existing link at one of those names is
refused rather than followed.

## Out of scope: user directories

There is no equivalent of the `directories` crate's `UserDirs` (home, downloads,
documents). Handing an application its whole home directory as a capability is broad
authority, which is the opposite of what this library is for. A program that really needs
one can open it with `Dir.Open(path, AmbientAuthority.Acquire())`, which makes the reach
visible where it happens.
