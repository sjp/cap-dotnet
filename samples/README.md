# Samples

Empty until [0029](../issues/0029-docs-samples.md). Planned:

- **Sandboxed file server** — serve a directory tree where a crafted request path is
  structurally incapable of escaping it.
- **Plugin host** — hand each plugin a `Dir` on its own data directory and nothing else.
- **Archive extractor** — zip-slip made impossible by construction rather than by a check.

The archive extractor is the one worth writing first: it is the canonical vulnerability
this library exists to remove, it is three lines with `Dir`, and it makes the pitch without
a paragraph of explanation.
