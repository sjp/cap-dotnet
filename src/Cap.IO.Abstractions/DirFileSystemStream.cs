using System.IO.Abstractions;

namespace Cap.IO.Abstractions;

/// <summary>
/// A stream over a file opened beneath a <see cref="DirFileSystem"/>'s directory, carrying
/// the virtual path it was opened by.
/// </summary>
/// <remarks>
/// <para>
/// The stream beneath is a <see cref="Cap.Std.CapFile"/>'s, which owns the open file, so
/// disposing this closes it. <see cref="FileSystemStream.Name"/> is the virtual full name,
/// which means nothing outside the adapter.
/// </para>
/// <para>
/// <see cref="Flush(bool)"/> with <c>true</c> asks the system to store the file's writes on
/// the device, as <see cref="FileStream.Flush(bool)"/> does. The stream beneath cannot always
/// be asked that itself, so whoever opens it says how with <paramref name="sync"/>; null
/// means there is no device beneath, as in memory. <paramref name="file"/>, when given, is
/// the file the stream beneath borrows, closed after it.
/// </para>
/// </remarks>
internal sealed class DirFileSystemStream(
    Stream stream,
    string path,
    bool isAsync,
    Action? sync,
    IDisposable? file = null)
    : FileSystemStream(stream, path, isAsync)
{
    /// <summary>Whether <see cref="Flush(bool)"/> with <c>true</c> reaches a device.</summary>
    internal bool SyncsToDisk => sync is not null;

    /// <inheritdoc/>
    public override void Flush(bool flushToDisk)
    {
        Flush();
        if (flushToDisk)
        {
            sync?.Invoke();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                file?.Dispose();
            }
        }
    }
}
