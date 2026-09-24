using System.Buffers.Binary;
using System.Text;
using Wasmtime;

namespace WasiHost.Preview1;

/// <summary>
/// The guest's linear memory, as one call sees it, with every access bounds-checked.
/// </summary>
/// <remarks>
/// <para>
/// A guest passes pointers as plain 32-bit numbers, and nothing stops it passing one that
/// runs off the end of its memory. Each access here checks the whole range first and reports
/// <see cref="Errno.Fault"/> instead of reading or writing out of bounds, so a hostile guest
/// cannot turn a bad pointer into a read of host memory.
/// </para>
/// <para>
/// A span handed out is valid only until the guest runs again or its memory grows. This type
/// lives for a single host call, and neither happens during one.
/// </para>
/// </remarks>
internal readonly ref struct GuestMemory
{
    /// <summary>Decodes a guest path, refusing bytes that are not UTF-8.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Memory _memory;

    public GuestMemory(Memory memory) => _memory = memory;

    /// <summary>The bytes at a guest address, or false when they are not all inside its memory.</summary>
    public bool TrySlice(uint address, uint length, out Span<byte> bytes)
    {
        if ((ulong)address + length > (ulong)_memory.GetLength() || length > int.MaxValue)
        {
            bytes = default;
            return false;
        }

        bytes = _memory.GetSpan(address, (int)length);
        return true;
    }

    public Errno ReadU32(uint address, out uint value)
    {
        value = 0;
        if (!TrySlice(address, 4, out Span<byte> bytes))
        {
            return Errno.Fault;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return Errno.Success;
    }

    public Errno WriteU8(uint address, byte value)
    {
        if (!TrySlice(address, 1, out Span<byte> bytes))
        {
            return Errno.Fault;
        }

        bytes[0] = value;
        return Errno.Success;
    }

    public Errno WriteU32(uint address, uint value)
    {
        if (!TrySlice(address, 4, out Span<byte> bytes))
        {
            return Errno.Fault;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Errno.Success;
    }

    public Errno WriteU64(uint address, ulong value)
    {
        if (!TrySlice(address, 8, out Span<byte> bytes))
        {
            return Errno.Fault;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return Errno.Success;
    }

    /// <summary>Copies bytes into the guest, writing as many as fit in the destination.</summary>
    public Errno WriteBytes(uint address, uint capacity, ReadOnlySpan<byte> source, out uint written)
    {
        written = (uint)Math.Min(capacity, (uint)source.Length);
        if (!TrySlice(address, written, out Span<byte> destination))
        {
            return Errno.Fault;
        }

        source[..(int)written].CopyTo(destination);
        return Errno.Success;
    }

    /// <summary>
    /// Reads a path argument. A path that is not valid UTF-8 has no name to hand the library.
    /// </summary>
    public Errno ReadPath(uint address, uint length, out string path)
    {
        path = string.Empty;
        if (!TrySlice(address, length, out Span<byte> bytes))
        {
            return Errno.Fault;
        }

        try
        {
            path = StrictUtf8.GetString(bytes);
            return Errno.Success;
        }
        catch (DecoderFallbackException)
        {
            return Errno.IlSeq;
        }
    }

    /// <summary>
    /// Reads a vector of buffers: pairs of a guest address and a length, eight bytes each.
    /// </summary>
    public Errno ReadIoVecs(uint address, uint count, out (uint Address, uint Length)[] vectors)
    {
        vectors = [];
        if (!TrySlice(address, checked(count * 8), out Span<byte> bytes))
        {
            return Errno.Fault;
        }

        vectors = new (uint, uint)[count];
        for (int i = 0; i < count; i++)
        {
            vectors[i] = (
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 8)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[((i * 8) + 4)..]));
        }

        return Errno.Success;
    }
}
