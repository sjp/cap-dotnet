using System.IO.Abstractions;

namespace Cap.IO.Abstractions;

/// <summary>
/// A stream over a file opened beneath a <see cref="DirFileSystem"/>'s directory, carrying
/// the virtual path it was opened by.
/// </summary>
/// <remarks>
/// <para>
/// The stream beneath is a <see cref="Cap.Std.CapFile"/>'s, which owns the open file, so
/// disposing this closes it. <see cref="Name"/> is the virtual full name, which means nothing
/// outside the adapter; it is folded from <paramref name="request"/> the first time it is read,
/// since most streams are never asked.
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
    VirtualPath paths,
    string request,
    bool isAsync,
    Action? sync,
    IDisposable? file = null)
    : FileSystemStream(stream, request, isAsync)
{
    private readonly string _request = request;
    private string? _name;

    /// <inheritdoc/>
    /// <remarks>
    /// The request is already absolute, so folding it later gives what folding it at the open
    /// would have, whatever the current directory has become since.
    /// </remarks>
    public override string Name => _name ??= paths.GetFullPath(_request, paths.Root);

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
