using Cap.Primitives;

namespace Cap.Std;

/// <summary>
/// The members of <see cref="CapFile"/>, as an interface a test can substitute.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CapFile"/> is the one type in this library that implements it, and
/// <see cref="IDir"/> hands it out wherever <see cref="Dir"/> hands out a <see cref="CapFile"/>.
/// What <see cref="IDir"/> says about the guarantee applies here unchanged: an
/// <see cref="ICapFile"/> refers to a file inside a confined subtree only if it came from a
/// real <see cref="Dir"/>, and code that must be certain of that takes <see cref="CapFile"/>.
/// </para>
/// <para>
/// <see cref="CapFile.UnsafeGetHandle"/> is not part of it: a stub has no operating-system
/// handle to give.
/// </para>
/// <para>
/// <strong>Versioning.</strong> As for <see cref="IDir"/>: a member added to
/// <see cref="CapFile"/> is added here in the same release, without a default
/// implementation, so a hand-written implementation outside this library can stop compiling
/// on any upgrade.
/// </para>
/// </remarks>
public interface ICapFile : IDisposable
{
    /// <inheritdoc cref="CapFile.Access"/>
    FileAccess Access { get; }

    /// <inheritdoc cref="CapFile.IsAsync"/>
    bool IsAsync { get; }

    /// <inheritdoc cref="CapFile.IsAppending"/>
    bool IsAppending { get; set; }

    /// <inheritdoc cref="CapFile.Length"/>
    long Length { get; }

    /// <inheritdoc cref="CapFile.GetMetadata()"/>
    CapMetadata GetMetadata();

    /// <inheritdoc cref="CapFile.SetLength(long)"/>
    void SetLength(long length);

    /// <inheritdoc cref="CapFile.SetTimes(CapFileTime, CapFileTime)"/>
    void SetTimes(CapFileTime lastAccess = default, CapFileTime lastWrite = default);

    /// <inheritdoc cref="CapFile.Flush(bool)"/>
    void Flush(bool toDisk);

    /// <inheritdoc cref="CapFile.Read(Span{byte}, long)"/>
    int Read(Span<byte> buffer, long fileOffset);

    /// <inheritdoc cref="CapFile.Write(ReadOnlySpan{byte}, long)"/>
    void Write(ReadOnlySpan<byte> buffer, long fileOffset);

    /// <inheritdoc cref="CapFile.ReadAsync(Memory{byte}, long, CancellationToken)"/>
    ValueTask<int> ReadAsync(Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="CapFile.WriteAsync(ReadOnlyMemory{byte}, long, CancellationToken)"/>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="CapFile.AsStream(bool, int)"/>
    Stream AsStream(bool leaveOpen = true, int bufferSize = CapFile.DefaultStreamBufferSize);
}
