using System.Buffers;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// A path or name encoded as the null-terminated bytes a Unix syscall expects.
/// </summary>
/// <remarks>
/// <para>
/// Encoding happens here rather than through the framework's UTF-8 encoder because a Unix
/// filename is a byte string and need not be valid UTF-8 at all. Names that are not are
/// carried through .NET strings by an escaping scheme that round-trips exactly, and only
/// the matching encoder reverses it; anything else would hand the kernel a different name
/// from the one the caller was given back by an earlier enumeration.
/// </para>
/// <para>
/// An embedded <c>U+0000</c> is refused here as well as by the path parser. The parser is
/// the real defence, but this is the last point before the bytes reach the kernel, which
/// stops at the first zero byte: a name that got this far with one in it would be silently
/// truncated to a prefix — a different, shorter name that the caller never asked about and
/// that no earlier check looked at.
/// </para>
/// </remarks>
internal ref struct UnixPathBuffer
{
    private byte[]? _rented;
    private Span<byte> _buffer;
    private int _length;

    /// <summary>
    /// Encodes <paramref name="path"/>, using <paramref name="scratch"/> when it fits.
    /// </summary>
    public static UnixPathBuffer Create(ReadOnlySpan<char> path, Span<byte> scratch)
    {
        UnixPathBuffer result = default;

        int byteCount = PathEncoding.GetByteCount(path);
        if (byteCount < 0)
        {
            return result;
        }

        int needed = byteCount + 1;
        if (needed <= scratch.Length)
        {
            result._buffer = scratch;
        }
        else
        {
            result._rented = ArrayPool<byte>.Shared.Rent(needed);
            result._buffer = result._rented;
        }

        if (!PathEncoding.TryGetBytes(path, result._buffer[..byteCount], out int written) ||
            written != byteCount ||
            result._buffer[..byteCount].Contains((byte)0))
        {
            result.Dispose();
            return default;
        }

        result._buffer[byteCount] = 0;
        result._length = needed;
        return result;
    }

    /// <summary>
    /// True when the input encoded to a usable name. False means the string could not be a
    /// filename on this platform, which is a caller error rather than a filesystem failure.
    /// </summary>
    public readonly bool IsValid => _length > 0;

    /// <summary>The encoded bytes, including the terminating zero.</summary>
    public readonly ReadOnlySpan<byte> Bytes => _buffer[.._length];

    /// <summary>Returns any pooled buffer.</summary>
    public void Dispose()
    {
        byte[]? rented = _rented;
        _rented = null;
        _buffer = default;
        _length = 0;
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
