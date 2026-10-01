using Cap.Primitives.Interop;

namespace Cap.Std;

/// <summary>
/// The shortcuts a copy takes to move a file's contents without reading them into the process.
/// </summary>
/// <remarks>
/// <para>
/// Internal, for the convenience layer's copies, which see files only as <see cref="CapFile"/>
/// and never the platform beneath them. Each member is an optimisation with a plain read and
/// write behind it, so each reports a <see cref="CapError"/> rather than throwing, and a
/// failure means only that the caller should read and write instead. The reads and writes
/// will meet, and report, whatever real fault there is.
/// </para>
/// <para>
/// Two files can share a shortcut only when both are the host's: a handle from a filesystem
/// held in memory means nothing to the kernel. Anything else is reported as
/// <see cref="CapErrorCategory.NotSupported"/> before the platform is asked, as is a
/// destination that cannot be written at a position — one without write access, or one that
/// appends.
/// </para>
/// </remarks>
public sealed partial class CapFile
{
    /// <summary>
    /// Makes <paramref name="destination"/> hold the whole of this file's contents, sharing
    /// their storage, as <see cref="IPlatformOps.CloneFileContents"/> describes.
    /// </summary>
    internal CapError CloneContentsTo(CapFile destination)
    {
        CapError refused = RefuseShortcut(destination);
        return refused.IsFailure ? refused : _backend.CloneFileContents(_handle, destination._handle);
    }

    /// <summary>
    /// Copies up to <paramref name="length"/> bytes at <paramref name="fileOffset"/> into the
    /// same place in <paramref name="destination"/> inside the kernel, as
    /// <see cref="IPlatformOps.CopyFileRange"/> describes.
    /// </summary>
    internal CapError CopyRangeTo(CapFile destination, long fileOffset, long length, out long copied)
    {
        copied = 0;
        CapError refused = RefuseShortcut(destination);
        return refused.IsFailure
            ? refused
            : _backend.CopyFileRange(_handle, destination._handle, fileOffset, length, out copied);
    }

    /// <summary>
    /// Creates <paramref name="name"/> beneath <paramref name="parent"/> as a new file sharing
    /// this one's storage, as <see cref="IPlatformOps.CloneFileToChild"/> describes.
    /// </summary>
    /// <param name="parent">The directory, which belongs to <paramref name="parentBackend"/>.</param>
    /// <param name="parentBackend">The implementation that issued <paramref name="parent"/>.</param>
    /// <param name="name">A single component.</param>
    internal CapError CloneInto(SafeDirHandle parent, IPlatformOps parentBackend, ReadOnlySpan<char> name)
    {
        Demand();
        return Readable && SharesHandlesWith(parentBackend)
            ? _backend.CloneFileToChild(_handle, parent, name)
            : CapError.FromCategory(CapErrorCategory.NotSupported);
    }

    /// <summary>
    /// Finds the next stretch of this file that holds data, at or after
    /// <paramref name="fileOffset"/>, as <see cref="IPlatformOps.FindFileData"/> describes.
    /// </summary>
    internal CapError FindData(long fileOffset, out long start, out long end)
    {
        Demand();
        return _backend.FindFileData(_handle, fileOffset, out start, out end);
    }

    /// <summary>
    /// Marks this file as one whose unwritten stretches take no storage, as
    /// <see cref="IPlatformOps.MarkFileSparse"/> describes.
    /// </summary>
    internal CapError MarkSparse()
    {
        Demand();
        return Writable ? _backend.MarkFileSparse(_handle) : CapError.FromCategory(CapErrorCategory.NotSupported);
    }

    /// <summary>
    /// Reserves storage for this file up to <paramref name="length"/> bytes without changing
    /// its length, as <see cref="IPlatformOps.ReserveFileSpace"/> describes.
    /// </summary>
    internal CapError Reserve(long length)
    {
        Demand();
        return Writable ? _backend.ReserveFileSpace(_handle, length) : CapError.FromCategory(CapErrorCategory.NotSupported);
    }

    private bool Readable => (_access & FileAccess.Read) != 0;

    private bool Writable => (_access & FileAccess.Write) != 0 && !_appending;

    /// <summary>
    /// Refuses a shortcut from this file into <paramref name="destination"/> that the two
    /// cannot take together.
    /// </summary>
    private CapError RefuseShortcut(CapFile destination)
    {
        Demand();
        destination.Demand();

        return Readable && destination.Writable && SharesHandlesWith(destination._backend)
            ? CapError.Success
            : CapError.FromCategory(CapErrorCategory.NotSupported);
    }

    /// <summary>
    /// Whether a handle from <paramref name="other"/> can be handed to this file's backend
    /// alongside this file's own: the same implementation, or two over the host's kernel.
    /// </summary>
    private bool SharesHandlesWith(IPlatformOps other) =>
        ReferenceEquals(_backend, other) || (_backend.IssuesKernelHandles && other.IssuesKernelHandles);
}
