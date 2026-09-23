using System.Buffers.Binary;
using Cap.Fuzz.Targets;
using Cap.Primitives.Interop.Windows;
using CsCheck;

namespace Cap.Fuzz.Tests;

/// <summary>
/// The reader for a reparse point's stored structure, given buffers that lie about themselves.
/// </summary>
/// <remarks>
/// <para>
/// The reader runs on every platform because it is a function of bytes alone, and so do these.
/// Every buffer goes through the reparse fuzz target's checks: no exception, the same verdict
/// and the same name as an independent reading of the documented layout, and nothing consulted
/// beyond the data the structure declares.
/// </para>
/// <para>
/// A reparse point's data is written by whoever created it, and inside a sandbox that can be
/// the attacker. So the buffers that matter are not random noise but real structures with one
/// thing wrong: a length that claims more than arrived, a name that starts past the end, a
/// count of bytes that is not a whole number of characters.
/// </para>
/// </remarks>
public sealed class ReparseDataPropertyTests
{
    /// <summary>Where the header's data length and the four name fields sit.</summary>
    private static readonly int[] FieldOffsets = [4, 8, 10, 12, 14];

    private static readonly Gen<string> Targets = Gen.String[Gen.Char["ab\\/.:?C\0é"], 0, 24];

    /// <summary>Any bytes at all are read or refused safely, and read correctly when read.</summary>
    [Fact]
    public void Any_bytes_are_read_or_refused_safely() =>
        Gen.Byte.Array[0, 96].Sample(
            buffer => ReparseDataTarget.Check(buffer),
            iter: PropertySettings.Iterations);

    /// <summary>
    /// A well-formed link with one of its fields replaced by any value, or cut short anywhere,
    /// is read or refused safely.
    /// </summary>
    [Fact]
    public void A_link_with_one_field_changed_is_read_or_refused_safely() =>
        Gen.Select(Targets, Gen.Bool, Gen.Int[0, FieldOffsets.Length], Gen.UShort, Gen.Int[0, 256]).Sample(
            (target, rooted, field, value, cut) =>
            {
                byte[] buffer = SymbolicLink(target, rooted);
                if (field < FieldOffsets.Length)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(FieldOffsets[field]), value);
                }

                ReparseDataTarget.Check(buffer);
                ReparseDataTarget.Check(buffer.AsSpan(0, Math.Min(cut, buffer.Length)));
            },
            iter: PropertySettings.Iterations);

    /// <summary>A link this library writes reads back as the link it meant to write.</summary>
    [Fact]
    public void A_link_written_here_reads_back_as_written() =>
        Gen.Select(Targets, Gen.Bool).Sample(
            (target, rooted) =>
            {
                byte[] buffer = SymbolicLink(target, rooted);
                Assert.True(ReparseData.TryReadTarget(buffer, out string stored, out bool isRelative));
                Assert.Equal(rooted ? ReparseData.ObjectManagerPrefix + target : target, stored);
                Assert.Equal(!rooted, isRelative);
                ReparseDataTarget.Check(buffer);
            },
            iter: PropertySettings.Iterations);

    /// <summary>
    /// Every way of cutting a link short, and every field pushed to the values on either side
    /// of what the buffer can hold, is refused wherever the name would no longer lie within
    /// the declared data.
    /// </summary>
    /// <remarks>
    /// Written out rather than sampled, because these are the edges where a bounds check is
    /// off by one, and a random value lands on an edge rarely.
    /// </remarks>
    [Fact]
    public void Malformed_links_are_refused()
    {
        foreach ((string target, bool rooted) in new[] { ("inside\\target", false), ("C:\\elsewhere", true), ("", false) })
        {
            byte[] valid = SymbolicLink(target, rooted);
            int declared = BinaryPrimitives.ReadUInt16LittleEndian(valid.AsSpan(4));

            // Cut short anywhere before the end of the declared data: the header claims more
            // than arrived.
            for (int length = 0; length < 8 + declared; length++)
            {
                Assert.False(ReparseData.TryReadTarget(valid.AsSpan(0, length), out _, out _), $"Cut to {length} bytes.");
                ReparseDataTarget.Check(valid.AsSpan(0, length));
            }

            int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(valid.AsSpan(8));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(valid.AsSpan(10));
            int room = declared - 12;

            // Each field set to every value near a boundary. The name must be refused exactly
            // when it would end past the declared data or has an odd length.
            foreach (int field in FieldOffsets)
            {
                foreach (int value in Boundaries(declared, room, nameOffset, nameLength))
                {
                    byte[] changed = (byte[])valid.Clone();
                    BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(field), (ushort)value);
                    ReparseDataTarget.Check(changed);

                    int newDeclared = field == 4 ? value : declared;
                    int newOffset = field == 8 ? value : nameOffset;
                    int newLength = field == 10 ? value : nameLength;
                    bool fits = 8 + newDeclared <= changed.Length && newDeclared >= 12 &&
                        newLength % 2 == 0 && 12 + newOffset + newLength <= newDeclared;

                    Assert.Equal(fits, ReparseData.TryReadTarget(changed, out _, out _));
                }
            }
        }

        // A tag this library does not know is refused whatever follows it, including a link's
        // layout exactly: reading a structure of unknown shape as a path is the fault.
        foreach (uint tag in new[] { 0u, ReparseTags.AppExecLink, ReparseTags.WciLink, 0xA000000Bu, 0xA000000Du, 0x2000000Cu, uint.MaxValue })
        {
            byte[] relabelled = SymbolicLink("inside", rooted: false);
            BinaryPrimitives.WriteUInt32LittleEndian(relabelled, tag);
            Assert.False(ReparseData.TryReadTarget(relabelled, out _, out _), $"Tag 0x{tag:X8} was read as a link.");
        }
    }

    private static IEnumerable<int> Boundaries(int declared, int room, int offset, int length)
    {
        int[] centres = [0, declared, room, offset, length, room - length, room - offset, ushort.MaxValue];
        return centres
            .SelectMany(centre => new[] { centre - 1, centre, centre + 1 })
            .Where(value => value is >= 0 and <= ushort.MaxValue)
            .Distinct();
    }

    private static byte[] SymbolicLink(string target, bool rooted)
    {
        byte[] buffer = new byte[ReparseData.SymbolicLinkSize(target, rooted)];
        Assert.True(ReparseData.TryBuildSymbolicLink(target, rooted, buffer, out _));
        return buffer;
    }
}
