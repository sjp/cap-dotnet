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
/// <para>
/// A stream opened to append is given <paramref name="appendStart"/>, the length the file had
/// when it was opened. As on a <see cref="FileStream"/>, seeking before it or setting the
/// length below it is refused with <see cref="IOException"/>, so what the file held is never
/// overwritten or cut; <c>-1</c> means the stream was not opened to append.
/// </para>
/// </remarks>
internal sealed class DirFileSystemStream(
    Stream stream,
    VirtualPath paths,
    string request,
    bool isAsync,
    Action? sync,
    IDisposable? file = null,
    long appendStart = -1)
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
    public override long Position
    {
        get => base.Position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (appendStart >= 0)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentException("Invalid seek origin.", nameof(origin)),
            };

            if (target >= 0 && target < appendStart)
            {
                throw new IOException("Unable to seek backward to overwrite data that previously existed in a file opened in Append mode.");
            }
        }

        return base.Seek(offset, origin);
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        if (appendStart >= 0 && value >= 0 && value < appendStart)
        {
            throw new IOException("Unable to truncate data that previously existed in a file opened in Append mode.");
        }

        base.SetLength(value);
    }

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
