using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop;

/// <summary>
/// Reads and writes the contents of files the host issued handles for.
/// </summary>
/// <remarks>
/// Shared by every host implementation of <see cref="IPlatformOps"/>, because on all of them
/// a file handle is the operating system's own and the framework already reads and writes
/// one correctly: positioned reads and writes on Linux and macOS, and overlapped or
/// synchronous calls on Windows depending on how the handle was opened. Each member is a
/// direct call with nothing added, so the host path costs what it cost before the contract
/// had these members, in time and in allocations.
/// </remarks>
internal static class HostFileContent
{
    public static int Read(SafeFileHandle handle, Span<byte> buffer, long fileOffset) =>
        RandomAccess.Read(handle, buffer, fileOffset);

    public static void Write(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long fileOffset) =>
        RandomAccess.Write(handle, buffer, fileOffset);

    public static ValueTask<int> ReadAsync(
        SafeFileHandle handle,
        Memory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken) =>
        RandomAccess.ReadAsync(handle, buffer, fileOffset, cancellationToken);

    public static ValueTask WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken) =>
        RandomAccess.WriteAsync(handle, buffer, fileOffset, cancellationToken);

    public static long GetLength(SafeFileHandle handle) => RandomAccess.GetLength(handle);

    public static void SetLength(SafeFileHandle handle, long length) => RandomAccess.SetLength(handle, length);

    public static void FlushToDisk(SafeFileHandle handle) => RandomAccess.FlushToDisk(handle);

    public static Stream OpenStream(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) =>
        new FileStream(handle, access, bufferSize, isAsync);
}
