using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop;

/// <summary>
/// Reads and writes the contents of files the host issued handles for.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every host implementation of <see cref="IPlatformOps"/>, because on all of them
/// a file handle is the operating system's own and the framework already reads and writes
/// one correctly: positioned reads and writes on Linux and macOS, and overlapped or
/// synchronous calls on Windows depending on how the handle was opened. Each member is a
/// direct call with nothing added but one check, so the host path costs what it cost before
/// the contract had these members, in time and in allocations.
/// </para>
/// <para>
/// The check is that a read or write at an offset is refused on a file with no offsets: a
/// FIFO, a socket or a device. Up to .NET 10 the framework refused one itself. From .NET 11
/// it reads and writes such a handle in order and ignores the offset, so a positioned read
/// of an empty FIFO waits for a writer instead of failing, and the caller that asked for a
/// position is never told there is none. The kind is the handle's own, learned once and
/// kept, so the check costs no syscall after the first.
/// </para>
/// </remarks>
internal static class HostFileContent
{
    public static int Read(SafeFileHandle handle, Span<byte> buffer, long fileOffset)
    {
        RequirePositions(handle);
        return RandomAccess.Read(handle, buffer, fileOffset);
    }

    public static void Write(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long fileOffset)
    {
        RequirePositions(handle);
        RandomAccess.Write(handle, buffer, fileOffset);
    }

    public static ValueTask<int> ReadAsync(
        SafeFileHandle handle,
        Memory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        RequirePositions(handle);
        return RandomAccess.ReadAsync(handle, buffer, fileOffset, cancellationToken);
    }

    public static ValueTask WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        RequirePositions(handle);
        return RandomAccess.WriteAsync(handle, buffer, fileOffset, cancellationToken);
    }

    public static long GetLength(SafeFileHandle handle) => RandomAccess.GetLength(handle);

    public static void SetLength(SafeFileHandle handle, long length) => RandomAccess.SetLength(handle, length);

    public static void FlushToDisk(SafeFileHandle handle) => RandomAccess.FlushToDisk(handle);

    public static Stream OpenStream(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) =>
        new FileStream(handle, access, bufferSize, isAsync);

    /// <summary>
    /// Refuses a handle to anything but a regular file or a block device, the kinds whose
    /// contents have positions, as the framework did before .NET 11.
    /// </summary>
    /// <remarks>
    /// Thrown before any work starts, and synchronously from the asynchronous members, as the
    /// framework's own refusal was. A closed handle is left for the framework to refuse.
    /// </remarks>
    private static void RequirePositions(SafeFileHandle handle)
    {
        if (!handle.IsClosed && handle.Type is not (FileHandleType.RegularFile or FileHandleType.BlockDevice))
        {
            throw new NotSupportedException("The file has no positions to read or write at.");
        }
    }
}
