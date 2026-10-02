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

The layout follows the Rust [`directories`](https://crates.io/crates/directories) crate. For
names whose only whitespace is single spaces between words, an application finds the same
directories whichever library it uses; the derivation below says where the two part.

| Kind | Linux | macOS | Windows |
|---|---|---|---|
| Config | `$XDG_CONFIG_HOME/myapp`, else `~/.config/myapp` | `~/Library/Application Support/com.Example-Corp.My-App` | `%APPDATA%\Example Corp\My App\config` |
| Data | `$XDG_DATA_HOME/myapp`, else `~/.local/share/myapp` | `~/Library/Application Support/com.Example-Corp.My-App` | `%APPDATA%\Example Corp\My App\data` |
| Cache | `$XDG_CACHE_HOME/myapp`, else `~/.cache/myapp` | `~/Library/Caches/com.Example-Corp.My-App` | `%LOCALAPPDATA%\Example Corp\My App\cache` |
| State | `$XDG_STATE_HOME/myapp`, else `~/.local/state/myapp` | none | none |
| Runtime | `$XDG_RUNTIME_DIR/myapp`, if that directory is private | none | none |

- **XDG.** The application's directory is its name, lowercased with `ToLowerInvariant`,
  with every Unicode whitespace character removed.
  As the XDG Base Directory specification requires, a variable that is unset, empty or not
  an absolute path is ignored and the default is used instead. A relative value would
  otherwise be resolved against whatever directory the process was started in.
- **macOS.** The qualifier, organization and application are joined into a bundle
  identifier with `.`. Each part is trimmed of Unicode whitespace, every run of whitespace
  inside it becomes a single hyphen, and parts left empty are left out. Case is kept.
  Configuration and data share one directory, because that is where the platform keeps
  both.
- **Windows.** The organization and application are used as given, under the roaming and
  local application-data known folders. An empty organization is left out.
- **State and runtime** exist only where the convention defines them. On macOS and Windows
  `OpenState` and `OpenRuntime` return null. This library does not invent a location for
  them.
- **Other operating systems.** The XDG column describes the convention every other Unix
  follows, but cap-dotnet has filesystem backends only for Linux, macOS and Windows.
  Elsewhere `ProjectDirs.From` fails, because there is no way to open a handle there; the
  rest of the library fails the same way.

A name containing `/`, `\` or NUL is refused on every platform, so the application's
directory cannot end up somewhere other than the one intended.

### Where this differs from the crate

The crate changes only the space character (U+0020), so these names land elsewhere there (`\t` stands for a tab):

| Name | Here | `directories` crate |
|---|---|---|
| Application `My  App` on macOS | `….My-App` | `….My--App` (each space becomes a hyphen; runs are kept) |
| Application `Tab\tApp` on Linux | `tabapp` | `tab\tapp` (only spaces are removed) |
| Application `Tab\tApp` on macOS | `….Tab-App` | `….Tab\tApp` |
| Qualifier `com x` on macOS | `com-x.…` | `com x.…` (the qualifier is used as given) |
| Application ` App ` on macOS | `….App` | `….-App-` (nothing is trimmed) |

Lowercasing also differs for the few characters with special Unicode casing, such as `İ`,
which Rust's `to_lowercase` maps to two characters. An application name that is empty, or
only whitespace, is refused here; the crate accepts it.

## Moving from `Environment.GetFolderPath`

A program written against `System.IO` usually keeps its files at
`Path.Combine(Environment.GetFolderPath(SpecialFolder.ApplicationData), "Example Corp", "My App")`,
which is not where `ProjectDirs.From` puts them. `ProjectDirs.OpenSpecialFolder` opens that
same directory as a handle, so existing files are found where they are:

```csharp
using Dir settings = ProjectDirs.OpenSpecialFolder(
    Environment.SpecialFolder.ApplicationData, "Example Corp", "My App", AmbientAuthority.Acquire());

string json = settings.ReadAllText("settings.json");
```

- **The folder** is whatever `Environment.GetFolderPath` answers on the host. Only
  `ApplicationData` and `LocalApplicationData` are accepted, because this is a way to an
  application's own directory rather than to the account's documents or desktop; a program
  that needs one of those can use `Dir.Open`, which makes the reach visible where it happens.
- **The names** are used exactly as given: no case is changed and no whitespace removed. An
  empty organization is left out. `/`, `\`, NUL, `.`, `..` and an empty application name are
  refused.
- **Creation** works as it does for `ProjectDirs.From`: the deepest existing part is opened
  by path, following links; whatever is missing is created through that handle, one name at
  a time, without following a link and with mode `0700` on Unix. The directory is created
  by the call itself, as `Directory.CreateDirectory` would, and the caller owns the handle.

A program with no existing files to find should prefer `ProjectDirs.From`, which follows each
platform's own conventions and keeps configuration, data and cache apart.

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
