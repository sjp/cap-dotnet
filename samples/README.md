# Samples

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

## Planned

- **Sandboxed file server** — serve a directory tree where a crafted request path is
  structurally incapable of escaping it.
- **Plugin host** — hand each plugin a `Dir` on its own data directory and nothing else.
- **Archive extractor** — zip-slip made impossible by construction rather than by a check.

The archive extractor is the one worth writing first: it is the canonical vulnerability
this library exists to remove, it is three lines with `Dir`, and it makes the pitch without
a paragraph of explanation.
