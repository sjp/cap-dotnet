using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop;

/// <summary>
/// A handle on whatever a name held when it was opened: a directory or a file, and never
/// both.
/// </summary>
/// <remarks>
/// <para>
/// What an open that was not told which kind to expect produces. The kind is read from the
/// object the open reached, not from the name, so it describes the thing the handle refers
/// to however the name has been reassigned since.
/// </para>
/// <para>
/// The two kinds stay two types rather than one handle with a flag. A directory handle and a
/// file handle carry different authority — one resolves names beneath it, the other reads
/// data — and anything that could be mistaken for the other would let the first be used as
/// the second. Exactly one of <see cref="Directory"/> and <see cref="File"/> is set.
/// </para>
/// <para>
/// Owning the handle, this must be disposed unless the handle has been taken out of it and
/// is owned elsewhere.
/// </para>
/// </remarks>
internal sealed class OpenedNode : IDisposable
{
    /// <summary>Wraps a directory handle.</summary>
    public OpenedNode(SafeDirHandle directory) => Directory = directory;

    /// <summary>Wraps a file handle.</summary>
    public OpenedNode(SafeFileHandle file) => File = file;

    /// <summary>The directory, when the name held one.</summary>
    public SafeDirHandle? Directory { get; }

    /// <summary>The file, when the name held anything other than a directory.</summary>
    public SafeFileHandle? File { get; }

    /// <summary>Closes whichever handle this holds.</summary>
    public void Dispose()
    {
        Directory?.Dispose();
        File?.Dispose();
    }
}
