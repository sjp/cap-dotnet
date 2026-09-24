using System.Text;

namespace WasiHost.Preview1;

public sealed partial class WasiPreview1
{
    private Errno ArgsSizesGet(GuestMemory memory, uint countAddress, uint sizeAddress) =>
        StringsSizes(memory, _options.Arguments, countAddress, sizeAddress);

    private Errno ArgsGet(GuestMemory memory, uint pointers, uint buffer) =>
        StringsGet(memory, _options.Arguments, pointers, buffer);

    private Errno EnvironSizesGet(GuestMemory memory, uint countAddress, uint sizeAddress) =>
        StringsSizes(memory, EnvironmentStrings(), countAddress, sizeAddress);

    private Errno EnvironGet(GuestMemory memory, uint pointers, uint buffer) =>
        StringsGet(memory, EnvironmentStrings(), pointers, buffer);

    private List<string> EnvironmentStrings() =>
        [.. _options.Environment.Select(pair => $"{pair.Key}={pair.Value}")];

    private static Errno StringsSizes(GuestMemory memory, IReadOnlyList<string> strings, uint countAddress, uint sizeAddress)
    {
        uint size = 0;
        foreach (string value in strings)
        {
            size += (uint)Encoding.UTF8.GetByteCount(value) + 1;
        }

        Errno error = memory.WriteU32(countAddress, (uint)strings.Count);
        return error != Errno.Success ? error : memory.WriteU32(sizeAddress, size);
    }

    /// <summary>
    /// Writes each string, NUL-terminated, one after another from <paramref name="buffer"/>, and
    /// a pointer to each into the array at <paramref name="pointers"/>.
    /// </summary>
    private static Errno StringsGet(GuestMemory memory, IReadOnlyList<string> strings, uint pointers, uint buffer)
    {
        uint cursor = buffer;
        for (int i = 0; i < strings.Count; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(strings[i] + "\0");
            Errno error = memory.WriteU32(pointers + (uint)(i * 4), cursor);
            if (error != Errno.Success)
            {
                return error;
            }

            if (!memory.TrySlice(cursor, (uint)bytes.Length, out Span<byte> destination))
            {
                return Errno.Fault;
            }

            bytes.CopyTo(destination);
            cursor += (uint)bytes.Length;
        }

        return Errno.Success;
    }

    private Errno ClockResGet(GuestMemory memory, uint clock, uint resultAddress)
    {
        ulong nanoseconds = (ClockId)clock switch
        {
            // A DateTimeOffset counts in ticks of 100 nanoseconds.
            ClockId.Realtime => 100,
            ClockId.Monotonic => Math.Max(1, 1_000_000_000 / (ulong)_clock.TimestampFrequency),
            _ => 0,
        };

        return nanoseconds == 0 ? Errno.Inval : memory.WriteU64(resultAddress, nanoseconds);
    }

    private Errno ClockTimeGet(GuestMemory memory, uint clock, uint resultAddress)
    {
        switch ((ClockId)clock)
        {
            case ClockId.Realtime:
                TimeSpan sinceEpoch = _clock.GetUtcNow() - DateTimeOffset.UnixEpoch;
                return memory.WriteU64(resultAddress, (ulong)sinceEpoch.Ticks * 100);

            case ClockId.Monotonic:
                TimeSpan elapsed = _clock.GetElapsedTime(0, _clock.GetTimestamp());
                return memory.WriteU64(resultAddress, (ulong)elapsed.Ticks * 100);

            default:
                // Processor-time clocks: a TimeProvider does not have one, and the host has
                // not handed the adapter anything else to read.
                return Errno.Inval;
        }
    }

    private Errno RandomGet(GuestMemory memory, uint buffer, uint length)
    {
        if (!memory.TrySlice(buffer, length, out Span<byte> destination))
        {
            return Errno.Fault;
        }

        _random.Fill(destination);
        return Errno.Success;
    }
}
