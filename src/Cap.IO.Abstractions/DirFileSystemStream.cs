using System.IO.Abstractions;

namespace Cap.IO.Abstractions;

/// <summary>
/// A stream over a file opened beneath a <see cref="DirFileSystem"/>'s directory, carrying
/// the virtual path it was opened by.
/// </summary>
/// <remarks>
/// The stream beneath is a <see cref="Cap.Std.CapFile"/>'s, which owns the open file, so
/// disposing this closes it. <see cref="FileSystemStream.Name"/> is the virtual full name,
/// which means nothing outside the adapter.
/// </remarks>
internal sealed class DirFileSystemStream(Stream stream, string path, bool isAsync)
    : FileSystemStream(stream, path, isAsync);
