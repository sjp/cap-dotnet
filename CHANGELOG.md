# Changelog

Every release's changes, written for the people who use the packages. The format is
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[SemVer](https://semver.org/) as [docs/releasing.md](docs/releasing.md#versioning) describes.

A change that alters containment behaviour, including a fix for an escape, is listed under
**Security** and says what a caller will now see. A containment fix ships in a patch release
even when it changes behaviour.

Add an entry under `Unreleased` in the change that makes it. When a release is cut, those
entries move under a heading for its version and date; the release workflow refuses a tag
whose version has no section here, and the section becomes the release's notes on GitHub.

## [Unreleased]

### Added

- `Cap.Std`: `Dir`, `CapFile` and `CapMetadata`, a filesystem reached through directory
  handles that cannot name anything outside the directory they were opened on, with the
  analyzer that flags ambient filesystem, network, clock and entropy use.
- `Cap.Fs.Ext`: atomic writes, tree walks and globs, and handle-to-handle copy and delete.
- `Cap.Net`: sockets restricted to a pool of permitted endpoints.
- `Cap.Time` and `Cap.Rand`: the system clock and OS entropy behind a token.
- `Cap.Directories`: well-known project directories as `Dir` handles.
- `Cap.Std.Testing`: an in-memory filesystem that hands out real `Dir` handles, for tests.
- `Cap.IO.Abstractions`: System.IO.Abstractions' `IFileSystem`, confined to a `Dir`.

[Unreleased]: https://github.com/sjp/cap-dotnet/commits/main
