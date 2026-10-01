using System.Buffers.Binary;
using System.Text;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Tests;

/// <summary>
/// Reading the structure Windows stores in a reparse point.
/// </summary>
/// <remarks>
/// <para>
/// This runs on every platform because none of it touches a filesystem. The structure is a
/// sequence of bytes, and every hazard in it is a hazard of parsing: a header that claims
/// more data than arrived, a name whose offset points past the end, a length that is not a
/// whole number of characters, a tag that owns a layout nothing here knows. Building those
/// by hand is the only way to test them — a kernel will not produce one, and an attacker
/// will.
/// </para>
/// <para>
/// The data is attacker-controlled wherever it matters. Creating a reparse point needs no
/// privilege for some tags, and anything that can write inside a sandbox can put one there
/// with whatever header it likes.
/// </para>
/// </remarks>
public sealed class WindowsReparseDataTests
{
    /// <summary>
    /// A link this library writes reads back as the link it meant to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creating a symbolic link beneath a directory handle means writing this structure by
    /// hand, because the call that would write it for us takes two paths and resolves both
    /// with the process's own authority. A wrong offset in it is invisible: the link is
    /// created, the call reports success, and only something that reads the link back finds
    /// out — which on the platform concerned might be months later and somewhere else.
    /// </para>
    /// <para>
    /// The reader is already exercised against hand-built structures on every platform, so
    /// pairing the writer with it costs nothing and turns a Windows-only unknown into an
    /// assertion that runs on every build agent. The rooted cases are the ones worth insisting
    /// on: what the filesystem resolves and what a reader is shown are deliberately not the
    /// same characters there, and each kind of full path is respelled differently. The backend
    /// never writes one, but the escape corpus builds them to test the reader against, so they
    /// have to look like the links the system's own call makes.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(@"inside\target", @"inside\target")]
    [InlineData(@"..\sibling", @"..\sibling")]
    [InlineData("inside/target", @"inside\target")]
    [InlineData("../sibling/file", @"..\sibling\file")]
    [InlineData("C:/elsewhere/file", @"\??\C:\elsewhere\file")]
    [InlineData("//srv/share/x", @"\??\UNC\srv\share\x")]
    [InlineData(@"C:\elsewhere\file", @"\??\C:\elsewhere\file")]
    [InlineData(@"\\srv\share\x", @"\??\UNC\srv\share\x")]
    [InlineData(@"\\?\C:\x", @"\??\C:\x")]
    [InlineData(@"\\.\C:\x", @"\??\C:\x")]
    [InlineData(@"\\?\UNC\srv\share", @"\??\UNC\srv\share")]
    [InlineData(@"C:x", @"C:x")]
    [InlineData(@"\x", @"\x")]
    public void A_link_written_here_reads_back_as_what_was_written(string target, string substitute)
    {
        bool rooted = CapPath.IsRooted(target, CapPathSyntax.Windows);
        byte[] buffer = new byte[ReparseData.SymbolicLinkSize(target, rooted)];

        Assert.True(ReparseData.TryBuildSymbolicLink(target, rooted, buffer, out int written));
        Assert.Equal(buffer.Length, written);

        Assert.True(ReparseData.TryReadTarget(buffer, out string stored, out bool isRelative));
        Assert.Equal(!rooted, isRelative);
        Assert.Equal(substitute, stored);
        Assert.False(stored.StartsWith(ReparseData.ObjectManagerPrefix + @"\", StringComparison.Ordinal));
        Assert.Equal(rooted, CapPath.IsRooted(stored, CapPathSyntax.Windows));
    }

    /// <summary>
    /// A target written with <c>/</c> is stored with <c>\</c> where the filesystem resolves
    /// it, and as written where a reader is shown it.
    /// </summary>
    /// <remarks>
    /// The filesystem does not read <c>/</c> as a separator in a relative substitute name, so
    /// a link stored as written would be followed by this library and dangle for every other
    /// program on the machine. The print name is a label, and keeps what the caller wrote, as
    /// it does for a link the system's own call makes.
    /// </remarks>
    [Fact]
    public void A_forward_slash_is_stored_as_a_backslash_and_shown_as_written()
    {
        const string Target = "sub/dir/file";
        byte[] buffer = new byte[ReparseData.SymbolicLinkSize(Target, rooted: false)];

        Assert.True(ReparseData.TryBuildSymbolicLink(Target, rooted: false, buffer, out _));

        Assert.True(ReparseData.TryReadTarget(buffer, out string stored, out bool isRelative));
        Assert.Equal(@"sub\dir\file", stored);
        Assert.True(isRelative);
        Assert.Equal(Target, PrintName(buffer));
    }

    /// <summary>A destination that is too small is refused rather than half filled.</summary>
    [Fact]
    public void A_buffer_that_does_not_fit_the_link_is_refused()
    {
        const string Target = "inside\\target";
        byte[] buffer = new byte[ReparseData.SymbolicLinkSize(Target, rooted: false) - 1];

        Assert.False(ReparseData.TryBuildSymbolicLink(Target, rooted: false, buffer, out int written));
        Assert.Equal(0, written);
    }

    /// <summary>
    /// A link that says its target is to be resolved from the directory holding it. This is
    /// the only shape a walk can do anything with.
    /// </summary>
    [Fact]
    public void A_relative_link_reports_its_target_and_its_relativity()
    {
        byte[] buffer = SymbolicLink("inside\\target", relative: true, print: "inside\\target");

        Assert.True(ReparseData.TryReadTarget(buffer, out string target, out bool isRelative));
        Assert.Equal("inside\\target", target);
        Assert.True(isRelative);
    }

    /// <summary>
    /// A link whose flag is clear is anchored at a filesystem root, and says so whatever its
    /// stored name looks like.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted, and the second is the one that matters. The flag is what the
    /// filesystem acts on, so a name spelled as an ordinary relative path is still rooted if
    /// the flag says it is — and a reader that inferred relativity from the spelling would
    /// walk it as though it stayed inside.
    /// </remarks>
    [Theory]
    [InlineData("\\??\\C:\\Windows")]
    [InlineData("C:\\Windows")]
    [InlineData("looks\\relative")]
    public void A_link_flagged_as_rooted_says_so_whatever_it_spells(string substitute)
    {
        byte[] buffer = SymbolicLink(substitute, relative: false);

        Assert.True(ReparseData.TryReadTarget(buffer, out string target, out bool isRelative));
        Assert.Equal(substitute, target);
        Assert.False(isRelative);
    }

    /// <summary>
    /// A junction has no relativity flag and is never relative. That is the whole reason one
    /// cannot be followed while staying beneath a directory handle.
    /// </summary>
    [Fact]
    public void A_junction_is_never_relative()
    {
        byte[] buffer = MountPoint("\\??\\C:\\Windows", print: "C:\\Windows");

        Assert.True(ReparseData.TryReadTarget(buffer, out string target, out bool isRelative));
        Assert.Equal("\\??\\C:\\Windows", target);
        Assert.False(isRelative);
    }

    /// <summary>
    /// The substitute name is what the filesystem resolves; the print name is a label. A
    /// point whose two names disagree is read by the one that acts.
    /// </summary>
    /// <remarks>
    /// Worth its own case because the disagreement is free to create and the print name is
    /// the one a user is shown. A reader that took the print name would report a harmless
    /// target and resolve a different one.
    /// </remarks>
    [Fact]
    public void The_resolved_name_is_read_and_not_the_displayed_one()
    {
        byte[] buffer = SymbolicLink("actual", relative: true, print: "something-else-entirely");

        Assert.True(ReparseData.TryReadTarget(buffer, out string target, out _));
        Assert.Equal("actual", target);
    }

    /// <summary>
    /// Tags that are not filesystem links, and are therefore never read as one.
    /// </summary>
    /// <remarks>
    /// An application execution alias is the sharp case. Its data is a series of counted
    /// strings and none of them sits where a link keeps its target, so a reader that assumed
    /// every reparse point held a link would take whatever landed at the offset it expected
    /// and resolve it. The container link is refused for a different reason — whatever it
    /// names is on the host, outside anything a handle in this process confers authority
    /// over — and the last two stand for every tag invented after this was written.
    /// </remarks>
    [Theory]
    [InlineData(ReparseTags.AppExecLink)]
    [InlineData(ReparseTags.WciLink)]
    [InlineData(0xA000001Fu)]
    [InlineData(0x00000001u)]
    public void A_tag_that_is_not_a_filesystem_link_yields_no_target(uint tag)
    {
        Assert.False(ReparseTags.IsFilesystemLink(tag));

        // A well-formed link body under the wrong tag: the bytes would parse, and are still
        // refused, because it is the tag that says whether they mean anything.
        byte[] buffer = SymbolicLink("inside\\target", relative: true);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, tag);

        Assert.False(ReparseData.TryReadTarget(buffer, out string target, out bool isRelative));
        Assert.Equal(string.Empty, target);
        Assert.False(isRelative);
    }

    /// <summary>The two tags that are links, so the allowlist is not vacuous.</summary>
    [Fact]
    public void The_two_link_tags_are_recognised()
    {
        Assert.True(ReparseTags.IsFilesystemLink(ReparseTags.SymbolicLink));
        Assert.True(ReparseTags.IsFilesystemLink(ReparseTags.MountPoint));
    }

    /// <summary>
    /// Tags that stand for another object are recognised as redirections, and refused.
    /// </summary>
    /// <remarks>
    /// The two links, a container link, a container tombstone, a made-up tag with the bit the
    /// system reserves for redirections — standing for every such tag invented after this was
    /// written — and the distributed file system link, which redirects without the bit.
    /// </remarks>
    [Theory]
    [InlineData(ReparseTags.SymbolicLink, true)]
    [InlineData(ReparseTags.MountPoint, true)]
    [InlineData(ReparseTags.WciLink, true)]
    [InlineData(0xA000001Fu, true)]
    [InlineData(0x2000_0123u, true)]
    [InlineData(ReparseTags.Dfs, false)]
    public void A_redirecting_tag_is_recognised_as_one(uint tag, bool surrogate)
    {
        Assert.Equal(surrogate, ReparseTags.IsNameSurrogate(tag));
        Assert.True(ReparseTags.Redirects(tag));
    }

    /// <summary>
    /// Tags whose filter serves the entry's own contents do not redirect, so the entry is
    /// opened as itself — and none of them is read as a link either.
    /// </summary>
    /// <remarks>
    /// A file compressed with <c>compact /exe</c>, a deduplicated file, a cloud placeholder and
    /// one of its numbered variants, a projected directory, a hierarchical storage placeholder
    /// and an application execution alias.
    /// </remarks>
    [Theory]
    [InlineData(0x80000017u)]
    [InlineData(0x80000013u)]
    [InlineData(0x9000001Au)]
    [InlineData(0x9000301Au)]
    [InlineData(0x9000001Cu)]
    [InlineData(0xC0000004u)]
    [InlineData(ReparseTags.AppExecLink)]
    public void A_tag_a_filter_serves_does_not_redirect(uint tag)
    {
        Assert.False(ReparseTags.IsNameSurrogate(tag));
        Assert.False(ReparseTags.Redirects(tag));
        Assert.False(ReparseTags.IsFilesystemLink(tag));

        byte[] buffer = SymbolicLink("inside\\target", relative: true);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, tag);
        Assert.False(ReparseData.TryReadTarget(buffer, out _, out _));
    }

    /// <summary>
    /// A header claiming more data than the filesystem returned. Believing it would read
    /// past the buffer.
    /// </summary>
    [Fact]
    public void A_length_longer_than_the_reply_is_refused()
    {
        byte[] buffer = SymbolicLink("inside", relative: true);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(buffer.Length + 64));

        Assert.False(ReparseData.TryReadTarget(buffer, out _, out _));
    }

    /// <summary>
    /// A name offset past the end of the data, and a length that runs past it. Each is a read
    /// out of bounds if the stated value is trusted.
    /// </summary>
    [Theory]
    [InlineData(4096, 12)]
    [InlineData(0, 4096)]
    public void A_name_outside_the_data_is_refused(ushort nameOffset, ushort nameLength)
    {
        byte[] buffer = SymbolicLink("inside", relative: true);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), nameOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), nameLength);

        Assert.False(ReparseData.TryReadTarget(buffer, out _, out _));
    }

    /// <summary>
    /// A byte count that is not a whole number of characters. The name is UTF-16, so an odd
    /// length describes half a character and cannot be decoded as written.
    /// </summary>
    [Fact]
    public void An_odd_name_length_is_refused()
    {
        byte[] buffer = SymbolicLink("inside", relative: true);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), 11);

        Assert.False(ReparseData.TryReadTarget(buffer, out _, out _));
    }

    /// <summary>
    /// A reply too short to hold the offsets it is supposed to carry, and one too short even
    /// for the tag.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(4)]
    [InlineData(0)]
    public void A_truncated_reply_is_refused(int length)
    {
        byte[] full = SymbolicLink("inside", relative: true);
        byte[] truncated = full.AsSpan(0, length).ToArray();

        // The declared length is trimmed too, so this is a short reply and not a reply whose
        // header lies -- the case above covers that one.
        if (length >= 8)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(truncated.AsSpan(4), (ushort)(length - 8));
        }

        Assert.False(ReparseData.TryReadTarget(truncated, out _, out _));
    }

    /// <summary>The tag cannot be read from a reply shorter than the header.</summary>
    [Fact]
    public void A_reply_shorter_than_the_header_has_no_tag()
    {
        Assert.False(ReparseData.TryReadTag([], out uint tag));
        Assert.Equal(0u, tag);

        Assert.False(ReparseData.TryReadTag(new byte[7], out _));
        Assert.True(ReparseData.TryReadTag(new byte[8], out _));
    }

    /// <summary>
    /// A link, laid out as the filesystem lays one out: four offsets, a flags word, then the
    /// substitute name followed by the print name.
    /// </summary>
    private static byte[] SymbolicLink(string substitute, bool relative, string print = "")
    {
        const int FlagsSize = sizeof(uint);
        return Build(ReparseTags.SymbolicLink, FlagsSize, substitute, print, relative ? 1u : 0u);
    }

    /// <summary>A junction, which is the same shape with no flags word.</summary>
    private static byte[] MountPoint(string substitute, string print = "") =>
        Build(ReparseTags.MountPoint, flagsSize: 0, substitute, print, flags: 0);

    /// <summary>
    /// A symbolic link's print name, which nothing in the library reads: the offsets are
    /// measured from the start of the characters, after the header, the offsets and the flags
    /// word.
    /// </summary>
    private static string PrintName(byte[] link)
    {
        const int PathBuffer = 20;
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(link.AsSpan(12));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(link.AsSpan(14));
        return Encoding.Unicode.GetString(link, PathBuffer + offset, length);
    }

    private static byte[] Build(uint tag, int flagsSize, string substitute, string print, uint flags)
    {
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(print);

        // Header, then the four offsets, then the flags word where the tag has one, then the
        // characters of both names one after the other.
        const int HeaderSize = 8;
        const int OffsetsSize = 4 * sizeof(ushort);
        int pathBuffer = HeaderSize + OffsetsSize + flagsSize;
        int dataLength = OffsetsSize + flagsSize + substituteBytes.Length + printBytes.Length;

        byte[] buffer = new byte[pathBuffer + substituteBytes.Length + printBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)dataLength);

        // The name offsets are measured from the start of the characters, not from the start
        // of the reply.
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substituteBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)substituteBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)printBytes.Length);

        if (flagsSize != 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), flags);
        }

        substituteBytes.CopyTo(buffer, pathBuffer);
        printBytes.CopyTo(buffer, pathBuffer + substituteBytes.Length);
        return buffer;
    }
}
