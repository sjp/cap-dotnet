# Why there is no `Dir.FullName`

`DirectoryInfo` has `FullName`. `Dir` does not, and will not. This is the most common
question about the API, so the answer is here rather than in an issue thread.

## A path is not the directory

A `Dir` is an open handle on one particular directory. It keeps referring to that directory
if the directory is renamed, if its parent is renamed, or if something else is created at
the name it used to have. A path is only a name, looked up afresh every time it is used, and
what it names can change between one use and the next.

So a `FullName` property would have to answer with a name that was true at some instant.
Nothing guarantees it is still true when the caller reads it, and there are cases where no
single true answer exists at all: a directory reached through a bind mount has as many names
as it has mount points, and one that has been removed has none.

## What callers would do with it

The code a `FullName` invites is the code this library exists to replace:

```csharp
string target = Path.Combine(dir.FullName, userPath);     // joining
if (target.StartsWith(dir.FullName)) { /* ... */ }        // prefix checking
File.ReadAllText(target);                                 // ambient lookup
```

Each line looks reasonable, and together they are the check the [README](../README.md) shows
being defeated by one symbolic link. A handle that published its path would make that the
easiest thing to write, and every convenience built on it would hand the containment decision
back to string comparison.

It would also turn the capability into a key to a bigger door. A component given a `Dir` on
`uploads/tenant-7` should be able to reach what is under it and nothing else. Handed a
string, it can pass that string to `File`, `Path.GetDirectoryName` and `..`, all of which
work with the process's own authority rather than the handle's. Without the string, the
handle is all it has.

## What to do instead

**To reach something beneath it**, pass a relative path to the `Dir`, or open a `Dir` on the
part you want and pass that on.

**To hand it to another component**, pass the `Dir` itself, or a narrower one derived from it
with `OpenDir`. A component that needs a directory should take a `Dir` parameter rather than a
string.

**To call an API that only takes a path** — a third-party library, a child process's argument
list — that API is going to use ambient authority whatever you do. Decide that deliberately,
at the composition root, from the configuration value you opened the `Dir` from in the first
place. That string is already trusted, and nothing about it came from the `Dir`.

**To log where a handle points**, use `TryGetPath`:

```csharp
if (dir.TryGetPath(AmbientAuthority.Acquire(), out string? path))
{
    logger.LogInformation("Writing reports to {Directory}", path);
}
```

It asks the operating system which name the directory answers to right now. It takes the
ambient-authority token because the answer names the directory from a root the handle confers
no authority over, and it returns false where the operating system cannot answer — a Linux
container without `/proc`, for one. The analyzer reports the token taken outside a
composition root, which is where a log line like this usually belongs anyway.

The result is for reading by people. Opening it again, joining onto it, or comparing it with
another path brings back exactly the problem described above.

## What cap-std does

cap-std, which this library ports, makes the same choice: its `Dir` has no path accessor
either. The reasoning is the same, and so is the escape hatch for diagnostics.
